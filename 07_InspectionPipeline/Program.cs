// =============================================================================
//  예제 07 : Inspection Pipeline (콜백 + 검사 스레드 + 디스플레이 워커)
// =============================================================================
//  목적
//    실전 머신비전 시스템의 정통 4단계 파이프라인:
//      ① SDK 이벤트 스레드 (그랩 콜백) — 최소한의 일만, 즉시 리턴
//         · ScopedBuffer 생성으로 SDK 버퍼 점유
//         · 검사 큐(BlockingCollection)에 enqueue (소유권 이전)
//         · 큐 꽉 차면 드롭 + 카운터 증가
//
//      ② 검사 스레드 (Task.Run 으로 전용 스레드 1개)
//         · 큐에서 dequeue
//         · 사용자 검사 알고리즘 호출 — raw pixel pointer 접근
//         · 디스플레이용으로 ConvertedBuffer 만들어 디스플레이 슬롯에 양보
//         · ScopedBuffer.Dispose() — SDK 큐로 반환
//
//      ③ 디스플레이 워커 스레드 (Task.Run)
//         · 슬롯에서 ConvertedBuffer consume
//         · OpenCV 로 다운스케일 + LockBits 로 비트맵에 직접 쓰기
//         · 비트맵 슬롯에 publish
//
//      ④ UI 타이머 (30 Hz)
//         · 비트맵 슬롯 consume → VideoPanel.SetImage
//         · 통계 라벨 갱신
//
//  분리의 이점
//    · 검사 latency 가 디스플레이 부담의 영향을 안 받음
//    · 디스플레이가 느려도 검사는 일정한 속도 유지 (단일 슬롯 + 백프레셔)
//    · SDK 콜백은 즉시 리턴 → SDK 큐 오버플로우 위험 최소
//
//  버퍼 풀 사이징
//    필요 버퍼 수 ≥ ⌈검사 latency(ms) × 그랩 fps / 1000⌉ + safety margin
//    · 예) 검사 50ms, 그랩 100fps → 5 + safety 4 = 9
//    · 본 샘플 기본 BufferCount = 16 (튜닝 가능 상수)
//
//  학습 포인트
//    · BlockingCollection<T> : 스레드 안전 큐 + 백프레셔 정책 (드롭)
//    · 단일 슬롯 + Interlocked.Exchange : 락 없는 핸드오프
//    · Task.Run + CancellationToken : 워커 스레드 라이프사이클
//    · 종료 시퀀스 : Grab Stop → Cancel → CompleteAdding → WhenAll
//    · 사용자 검사 hook 패턴 (Inspect 메서드)
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenCvSharp;
using EG = Euresys.EGrabber;

namespace InspectionPipeline
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    // ── 통계 스냅샷 (UI 표시용) ───────────────────────────────────────────────
    internal sealed record NodeStats(
        double Fps,
        int    W,
        int    H,
        double Mbps,
        long   Frames,
        long   Dropped,
        int    QueueDepth);

    // ── Bitmap 풀 (디스플레이 워커 전용) ─────────────────────────────────────
    internal sealed class BitmapPool
    {
        private readonly ConcurrentQueue<Bitmap> _free = new();
        private volatile int _w, _h;

        public void Init(int w, int h)
        {
            Drain();
            _w = w; _h = h;
            for (int i = 0; i < 3; i++)
                _free.Enqueue(new Bitmap(w, h, PixelFormat.Format24bppRgb));
        }

        public Bitmap Rent(int w, int h)
        {
            if (w != _w || h != _h) Init(w, h);
            return _free.TryDequeue(out var b)
                ? b
                : new Bitmap(w, h, PixelFormat.Format24bppRgb);
        }

        public void Return(Bitmap bmp)
        {
            if (bmp.Width == _w && bmp.Height == _h) _free.Enqueue(bmp);
            else bmp.Dispose();
        }

        public void Drain()
        {
            while (_free.TryDequeue(out var b)) b.Dispose();
        }
    }

    // ── 히스토그램 패널 (검사 스레드가 계산한 256-bin 분포 표시) ──────────────
    // 검사 스레드가 int[256] 슬롯에 publish → UI 타이머가 SetHistogram 호출.
    internal sealed class HistogramPanel : Control
    {
        private int[]? _hist;

        public HistogramPanel()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            BackColor      = Color.Black;
            ForeColor      = Color.LimeGreen;
            DoubleBuffered = true;
        }

        public void SetHistogram(int[]? hist) { _hist = hist; Invalidate(); }

        protected override void OnPaintBackground(PaintEventArgs pevent) { /* skip */ }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            var hist = _hist;
            if (hist == null || hist.Length != 256)
            {
                using var ft = new Font("Consolas", 9f);
                using var br = new SolidBrush(Color.DimGray);
                g.DrawString("(no data)", ft, br, 8, 8);
                return;
            }

            // 최대 빈도 — Y 스케일 정규화용
            int max = 0;
            for (int i = 0; i < 256; i++)
                if (hist[i] > max) max = hist[i];
            if (max <= 0) return;

            int cw = ClientSize.Width;
            int ch = ClientSize.Height;

            int padTop = 22, padBottom = 22, padX = 8;
            int graphH = Math.Max(10, ch - padTop - padBottom);
            int graphW = Math.Max(10, cw - padX * 2);

            // 배경 그리드 (4 분할)
            using (var grid = new Pen(Color.FromArgb(40, 60, 40), 1))
            {
                for (int i = 1; i < 4; i++)
                {
                    int y = padTop + graphH * i / 4;
                    g.DrawLine(grid, padX, y, padX + graphW, y);
                }
            }

            // 막대
            float binW = (float)graphW / 256f;
            using (var brush = new SolidBrush(ForeColor))
            {
                for (int i = 0; i < 256; i++)
                {
                    float barH = (float)hist[i] / max * graphH;
                    if (barH < 1) continue;
                    float x = padX + i * binW;
                    float y = padTop + (graphH - barH);
                    g.FillRectangle(brush, x, y, Math.Max(1f, binW), barH);
                }
            }

            // 외곽 + X 축
            using (var axis = new Pen(Color.Gray, 1))
            {
                g.DrawRectangle(axis, padX, padTop, graphW, graphH);
            }

            // 라벨
            using var f      = new Font("Consolas", 8f);
            using var lblBr  = new SolidBrush(Color.LightGray);
            using var titleBr = new SolidBrush(Color.LimeGreen);

            g.DrawString("Histogram (8-bit)", new Font("Consolas", 9f, FontStyle.Bold),
                         titleBr, padX, 2);
            g.DrawString("0",   f, lblBr, padX - 2,                 padTop + graphH + 3);
            g.DrawString("128", f, lblBr, padX + graphW / 2 - 8,    padTop + graphH + 3);
            g.DrawString("255", f, lblBr, padX + graphW - 18,       padTop + graphH + 3);

            // 우상단 max 빈도 표시
            string maxTxt = $"max={max}";
            var sz = g.MeasureString(maxTxt, f);
            g.DrawString(maxTxt, f, lblBr, cw - sz.Width - 4, 4);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
    }

    // ── 영상 표시 컨트롤 (06 과 동일) ────────────────────────────────────────
    internal sealed class VideoPanel : Control
    {
        private Bitmap? _image;

        public VideoPanel()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            BackColor      = Color.Black;
            DoubleBuffered = true;
        }

        public void SetImage(Bitmap? bmp) { _image = bmp; Invalidate(); }

        protected override void OnPaintBackground(PaintEventArgs pevent) { /* skip */ }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g   = e.Graphics;
            var img = _image;
            if (img == null) { g.Clear(BackColor); return; }

            g.CompositingMode   = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.Low;
            g.PixelOffsetMode   = PixelOffsetMode.HighSpeed;
            g.Clear(BackColor);

            float scale = Math.Min((float)ClientSize.Width  / img.Width,
                                   (float)ClientSize.Height / img.Height);
            int dw = (int)(img.Width  * scale);
            int dh = (int)(img.Height * scale);
            g.DrawImage(img,
                (ClientSize.Width  - dw) / 2,
                (ClientSize.Height - dh) / 2,
                dw, dh);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
    }

    public class MainForm : Form
    {
        // ── 튜닝 가능 상수 ────────────────────────────────────────────────────
        // BufferCount: SDK DMA 버퍼 수 = 검사 시간 동안 in-flight 가능한 프레임 수
        //   필요량 = ⌈검사 latency × fps / 1000⌉ + safety
        //   너무 작으면: BufferLost (SDK 큐 오버플로우)
        //   너무 크면: 메모리만 좀 더 씀 (큰 문제 아님)
        private const int  BufferCount              = 16;

        // 검사 큐 용량: 콜백과 검사 스레드 사이 버퍼링.
        //   꽉 차면 콜백은 즉시 드롭 (블로킹 X — 그랩 멈춤 방지)
        private const int  InspectionQueueCapacity  = 16;

        // 디스플레이용 최대 변(픽셀). 통계는 원본 해상도 기준.
        private const int  MaxDisplayDim            = 960;

        // FPS 슬라이딩 윈도우 (1 초)
        private const long FpsWindowSec             = 1;

        // ── UI 컨트롤 ──────────────────────────────────────────────────────────
        private readonly VideoPanel     _videoPanel;
        private readonly HistogramPanel _histogramPanel;
        private readonly Button     _btnStart;
        private readonly Button     _btnStop;
        private readonly Label      _lblFrames;
        private readonly Label      _lblFps;
        private readonly Label      _lblResolution;
        private readonly Label      _lblMbps;
        private readonly Label      _lblQueue;
        private readonly Label      _lblDropped;
        private readonly Label      _lblStatus;

        // ── 그랩 ↔ 검사 ↔ 디스플레이 채널 ────────────────────────────────────
        // 검사 큐: SDK 콜백 → 검사 스레드 (BlockingCollection)
        private BlockingCollection<EG.ScopedBuffer>? _inspectionQueue;

        // 디스플레이 슬롯: 검사 → 디스플레이 워커 (단일 슬롯, lock-free)
        // 검사 스레드가 양보한 ConvertedBuffer 를 보관. 워커가 consume.
        private EG.ConvertedBuffer? _displayPending;

        // UI 슬롯: 디스플레이 워커 → UI 타이머 (비트맵 / stats)
        private Bitmap?    _latestBitmap;
        private NodeStats? _latestStats;

        // 히스토그램 슬롯: 검사 스레드 → UI 타이머 (int[256])
        // 검사 코드가 매 프레임 계산해 publish, UI 가 30Hz 로 소비.
        // 새 hist 가 publish 되면 이전 것은 그냥 덮어씀 (256 int 배열은 가벼움).
        private int[]?     _latestHistogram;

        // 풀
        private readonly BitmapPool _pool = new();

        // 현재 화면에 띄워진 비트맵 (다음 프레임 시 풀로 반환)
        private Bitmap? _displayBitmap;

        // ── SDK 객체 ──────────────────────────────────────────────────────────
        private EG.EGenTL?            _gentl;
        private EG.EGrabberDiscovery? _discovery;
        private EG.EGrabber?          _grabber;

        // ── Task 들 ───────────────────────────────────────────────────────────
        private CancellationTokenSource? _cts;
        private Task?                    _eventTask;        // ProcessEventsAsync
        private Task?                    _inspectionTask;   // 검사 스레드
        private Task?                    _displayTask;      // 디스플레이 워커
        private volatile bool            _running;

        // ── 통계 카운터 (Interlocked) ─────────────────────────────────────────
        private long _totalFrames;
        private long _droppedFrames;

        // FPS 계산용 (SDK 콜백 스레드에서만 접근 — 단일 스레드)
        private readonly System.Collections.Generic.Queue<long> _frameTs = new();
        private readonly long                                   _windowTicks = Stopwatch.Frequency * FpsWindowSec;
        private readonly Stopwatch                              _sw          = Stopwatch.StartNew();

        // ── 디스플레이 타이머 ─────────────────────────────────────────────────
        private readonly System.Windows.Forms.Timer _displayTimer;

        // ── 생성자: UI 레이아웃 ────────────────────────────────────────────────
        public MainForm()
        {
            Text           = "Euresys Inspection Pipeline (callback + workers)";
            ClientSize     = new System.Drawing.Size(1100, 720);
            MinimumSize    = new System.Drawing.Size(720, 480);
            StartPosition  = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            _videoPanel     = new VideoPanel     { Dock = DockStyle.Fill  };
            _histogramPanel = new HistogramPanel { Dock = DockStyle.Right, Width = 300 };

            var bottomPanel = new Panel
            {
                Dock    = DockStyle.Bottom,
                Height  = 50,
                Padding = new Padding(8, 6, 8, 6)
            };

            _btnStart      = new Button { Text = "Start", Width = 70, Left = 8,   Top = 10 };
            _btnStop       = new Button { Text = "Stop",  Width = 70, Left = 84,  Top = 10, Enabled = false };
            _lblFrames     = new Label  { Text = "Frames: 0",    AutoSize = true, Left = 170, Top = 14, ForeColor = Color.DimGray };
            _lblFps        = new Label  { Text = "FPS: --",      AutoSize = true, Left = 280, Top = 14, ForeColor = Color.DimGray };
            _lblResolution = new Label  { Text = "----x----",    AutoSize = true, Left = 380, Top = 14, ForeColor = Color.DimGray };
            _lblMbps       = new Label  { Text = "-- MB/s",      AutoSize = true, Left = 490, Top = 14, ForeColor = Color.DimGray };
            _lblQueue      = new Label  { Text = "Q: 0",         AutoSize = true, Left = 600, Top = 14, ForeColor = Color.DimGray };
            _lblDropped    = new Label  { Text = "Drop: 0",      AutoSize = true, Left = 680, Top = 14, ForeColor = Color.DimGray };
            _lblStatus     = new Label  { Text = "Ready",        AutoSize = true, Left = 790, Top = 14, ForeColor = Color.DimGray };

            _btnStart.Click += BtnStart_Click;
            _btnStop.Click  += BtnStop_Click;

            bottomPanel.Controls.AddRange(new Control[]
            {
                _btnStart, _btnStop, _lblFrames, _lblFps, _lblResolution,
                _lblMbps, _lblQueue, _lblDropped, _lblStatus
            });
            // Z-order 주의: 마지막에 추가될수록 먼저 dock 됨.
            //   bottomPanel(Bottom, full width) → histogramPanel(Right, above bottom) → videoPanel(Fill)
            Controls.Add(_videoPanel);
            Controls.Add(_histogramPanel);
            Controls.Add(bottomPanel);

            FormClosing += MainForm_FormClosing;

            _displayTimer = new System.Windows.Forms.Timer { Interval = 33 };  // ~30 Hz
            _displayTimer.Tick += DisplayTimer_Tick;
            _displayTimer.Start();
        }

        // ── UI 타이머 Tick (UI 스레드) ────────────────────────────────────────
        private void DisplayTimer_Tick(object? sender, EventArgs e)
        {
            // ① 비트맵 슬롯 consume
            var bmp = Interlocked.Exchange(ref _latestBitmap, null);
            if (bmp != null)
            {
                var old = _displayBitmap;
                _displayBitmap = bmp;
                _videoPanel.SetImage(bmp);
                if (old != null) _pool.Return(old);
            }

            // ② 히스토그램 슬롯 consume (검사 스레드가 publish, null 이면 직전 값 유지)
            var hist = Interlocked.Exchange(ref _latestHistogram, null);
            if (hist != null)
                _histogramPanel.SetHistogram(hist);

            // ③ Stats 슬롯 peek (consume X — 다음 Tick 까지 라벨 유지)
            var stats = Volatile.Read(ref _latestStats);
            if (stats != null)
            {
                _lblFrames.Text     = $"Frames: {stats.Frames,8}";
                _lblResolution.Text = $"{stats.W}x{stats.H}";
                _lblMbps.Text       = $"{stats.Mbps,6:F0} MB/s";
                _lblQueue.Text      = $"Q: {stats.QueueDepth,2}";
                _lblDropped.Text    = $"Drop: {stats.Dropped}";

                Color fpsColor = stats.Fps >= 30 ? Color.Green
                               : stats.Fps >= 15 ? Color.DarkOrange
                                                 : Color.Red;
                _lblFps.ForeColor = fpsColor;
                _lblFps.Text      = $"FPS: {stats.Fps,6:F1}";

                _lblDropped.ForeColor = stats.Dropped > 0 ? Color.OrangeRed : Color.DimGray;
                _lblQueue.ForeColor   = stats.QueueDepth >= InspectionQueueCapacity * 3 / 4
                                            ? Color.OrangeRed : Color.DimGray;
            }
        }

        // ── Start ─────────────────────────────────────────────────────────────
        private void BtnStart_Click(object? sender, EventArgs e)
        {
            _btnStart.Enabled = false;
            _btnStop.Enabled  = false;
            SetStatus("Initializing...", Color.DarkOrange);

            // 카운터/큐 리셋
            Interlocked.Exchange(ref _totalFrames,   0);
            Interlocked.Exchange(ref _droppedFrames, 0);
            _frameTs.Clear();
            _sw.Restart();

            try
            {
                InitGrabber();
                if (_grabber == null)
                {
                    SetStatus("No grabber found", Color.Red);
                    _btnStart.Enabled = true;
                    return;
                }

                _cts             = new CancellationTokenSource();
                _inspectionQueue = new BlockingCollection<EG.ScopedBuffer>(InspectionQueueCapacity);

                // ① 워커 스레드부터 시작 (콜백 들어오기 전에 준비)
                _inspectionTask = Task.Run(() => InspectionLoop());
                _displayTask    = Task.Run(() => DisplayWorkerLoop(_cts.Token));

                // ② 콜백 등록 + 이벤트 활성화
                _grabber.RegisterEventCallback<EG.NewBufferData>(OnNewBuffer);
                _grabber.EnableEvent(EG.EventType.NewBufferData);

                // ③ DMA 버퍼 풀 (검사 + 디스플레이 in-flight 흡수 가능한 크기)
                _grabber.ReallocBuffers((ulong)BufferCount);

                // ④ 이벤트 펌프 시작 (SDK 가 콜백 호출)
                _eventTask = _grabber.ProcessEventsAsync(EG.EventType.NewBufferData, _cts.Token);

                // ⑤ 그랩 시작 (무한)
                _grabber.Start();

                _running          = true;
                _btnStop.Enabled  = true;
                SetStatus($"Running (buffers={BufferCount}, queue={InspectionQueueCapacity})", Color.Green);
            }
            catch (Exception ex)
            {
                SetStatus($"Error: {ex.Message}", Color.Red);
                CleanupAll();
                _btnStart.Enabled = true;
            }
        }

        // ── Stop ──────────────────────────────────────────────────────────────
        private async void BtnStop_Click(object? sender, EventArgs e)
        {
            _btnStop.Enabled = false;
            SetStatus("Stopping...", Color.DarkOrange);
            _running = false;

            await StopPipelineAsync().ConfigureAwait(true);

            CleanupAll();
            _btnStart.Enabled = true;
            SetStatus("Stopped", Color.DimGray);
        }

        // ── 종료 시퀀스 ───────────────────────────────────────────────────────
        // 순서:
        //   1) 그랩 정지 — SDK 가 새 콜백을 더 안 만듦
        //   2) Cancel — 이벤트 펌프 + 디스플레이 워커 종료 신호
        //   3) 이벤트 펌프 await — SDK 가 남은 콜백 모두 처리
        //   4) CompleteAdding — 검사 큐 드레인 모드
        //   5) 검사 + 디스플레이 워커 await
        private async Task StopPipelineAsync()
        {
            try { _grabber?.Stop(); } catch { }
            _cts?.Cancel();

            // 이벤트 펌프 종료 대기 — 이후 새 enqueue 없음
            if (_eventTask != null)
            {
                try { await _eventTask.ConfigureAwait(true); } catch { }
            }

            _inspectionQueue?.CompleteAdding();

            if (_inspectionTask != null)
            {
                try { await _inspectionTask.ConfigureAwait(true); } catch { }
            }
            if (_displayTask != null)
            {
                try { await _displayTask.ConfigureAwait(true); } catch { }
            }

            // 디스플레이 슬롯에 남은 ConvertedBuffer 정리
            var pending = Interlocked.Exchange(ref _displayPending, null);
            pending?.Dispose();
        }

        // ── 창 닫기 ───────────────────────────────────────────────────────────
        private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!_running)
            {
                _displayTimer.Stop();
                CleanupAll();
                return;
            }

            e.Cancel = true;          // 일단 막기
            _displayTimer.Stop();
            _running = false;
            await StopPipelineAsync().ConfigureAwait(true);
            CleanupAll();
            BeginInvoke((Action)Close);
        }

        // =====================================================================
        //  ① SDK 이벤트 스레드 — 그랩 콜백 (최소 처리, 즉시 리턴)
        // =====================================================================
        private void OnNewBuffer(EG.EGrabber g, EG.NewBufferData data)
        {
            // SDK 버퍼 점유
            var buf = new EG.ScopedBuffer(g, data);
            bool ownedByQueue = false;

            try
            {
                // 메타 정보 (가벼움)
                var bi  = buf.GetInfo();
                int w   = (int)bi.Width;
                int h   = (int)bi.DeliveredHeight;
                uint bpp = bi.BitsPerPixel;

                // FPS 슬라이딩 윈도우 갱신 — 콜백 스레드는 단일이라 락 불필요
                long now = _sw.ElapsedTicks;
                _frameTs.Enqueue(now);
                while (_frameTs.Count > 0 && now - _frameTs.Peek() > _windowTicks)
                    _frameTs.Dequeue();
                double fps = _frameTs.Count;

                long total = Interlocked.Increment(ref _totalFrames);
                double mbps = w * h * (bpp / 8.0) * fps / 1_000_000.0;

                // 검사 큐로 소유권 이전 (백프레셔: 꽉차면 드롭)
                if (_inspectionQueue != null && _inspectionQueue.TryAdd(buf))
                {
                    ownedByQueue = true;
                }
                else
                {
                    Interlocked.Increment(ref _droppedFrames);
                }

                // Stats publish (UI 타이머가 peek)
                long dropped = Interlocked.Read(ref _droppedFrames);
                int qDepth = _inspectionQueue?.Count ?? 0;
                Interlocked.Exchange(ref _latestStats,
                    new NodeStats(fps, w, h, mbps, total, dropped, qDepth));
            }
            catch
            {
                // 콜백에서 예외 절대 누출 금지 — 이벤트 펌프가 죽음
                // 드롭 카운터로만 표시
                Interlocked.Increment(ref _droppedFrames);
            }
            finally
            {
                if (!ownedByQueue)
                    buf.Dispose();   // 큐에 안 들어갔으면 즉시 반환
            }
        }

        // =====================================================================
        //  ② 검사 스레드 — 사용자 검사 알고리즘 + 디스플레이로 양보
        // =====================================================================
        private void InspectionLoop()
        {
            if (_inspectionQueue == null) return;

            try
            {
                // GetConsumingEnumerable: 큐 비면 대기, CompleteAdding + 큐 빌 때까지 반복
                foreach (var buf in _inspectionQueue.GetConsumingEnumerable())
                {
                    using (buf)
                    {
                        try
                        {
                            // ★ 사용자 검사 코드
                            Inspect(buf);

                            // 디스플레이용 양보 (백프레셔)
                            HandOffToDisplay(buf);
                        }
                        catch
                        {
                            // 검사 코드 예외가 파이프라인 죽이지 않도록 격리
                        }
                    }
                }
            }
            catch (Exception) { /* InvalidOp on disposed collection 등 */ }
        }

        // ── 사용자 검사 코드 진입점 ───────────────────────────────────────────
        // 고객은 이 메서드 본문에 자기 검사 알고리즘 추가.
        // raw pixel pointer 에 직접 접근 가능 (복사 없음).
        // 주의: 이 메서드는 검사 스레드에서 실행됨 → UI 컨트롤 직접 접근 금지.
        //
        // 예시 구현: 8-bit 픽셀 히스토그램(256 bin) 계산.
        //   · Mono8, BayerXX8, BGR8 등 8-bit 포맷 모두에 대해 raw 바이트 분포를 계산.
        //   · 큰 이미지는 stride 로 서브샘플링 — 약 200K 샘플이면 통계적으로 충분.
        //   · 결과는 단일 슬롯(_latestHistogram) 에 publish → UI 타이머가 30Hz 로 소비.
        //
        // 다른 검사 패턴 (대체 가능):
        //   · OpenCV Mat 으로 wrap 후 사용자 알고리즘:
        //       using var mat = new Mat(h, w, MatType.CV_8UC1, basePtr);
        //   · ROI 평균, 결함 검출, OCR, NG/OK 판정 등
        //
        // 주의:
        //   · 검사 시간이 1/fps 보다 길면 큐가 쌓이고 결국 드롭 시작
        //   · 검사 latency 측정해서 BufferCount / InspectionQueueCapacity 튜닝
        private unsafe void Inspect(EG.ScopedBuffer buf)
        {
            var basePtr = buf.GetInfo<IntPtr>(EG.BUFFER_INFO_CMD.BUFFER_INFO_BASE);
            ulong size  = buf.GetInfo<ulong> (EG.BUFFER_INFO_CMD.BUFFER_INFO_SIZE);

            if (basePtr == IntPtr.Zero || size == 0) return;

            // 256-bin 히스토그램. 8-bit 픽셀(또는 raw 바이트) 기준.
            var hist = new int[256];

            // 서브샘플링: 큰 이미지일 때 stride 로 건너뛰며 sampling.
            // 200K 샘플이면 분포 통계로 충분.
            int total  = (int)Math.Min(size, int.MaxValue);
            int stride = Math.Max(1, total / 200_000);

            byte* p = (byte*)basePtr;
            for (int i = 0; i < total; i += stride)
                hist[p[i]]++;

            // UI 슬롯에 publish (이전 hist 는 덮어쓰기, GC 가 정리)
            Interlocked.Exchange(ref _latestHistogram, hist);
        }

        // ── 디스플레이 슬롯에 ConvertedBuffer 양보 (백프레셔) ─────────────────
        private void HandOffToDisplay(EG.ScopedBuffer buf)
        {
            // 이전 ConvertedBuffer 가 아직 디스플레이 워커한테 안 가져가짐 → 이번엔 스킵
            // (디스플레이가 검사를 못 따라옴, 검사 속도는 영향 없음)
            if (Volatile.Read(ref _displayPending) != null) return;

            EG.ConvertedBuffer? conv = null;
            try
            {
                // BGR8 변환 (자체 메모리 — SDK 버퍼와 독립)
                conv = buf.Convert(
                    "BGR8",
                    EG.IMAGE_CONVERT_OUTPUT_CONFIG.IMAGE_CONVERT_OUTPUT_CONFIG_DEFAULT,
                    EG.IMAGE_CONVERT_OUTPUT_OPERATION.IMAGE_CONVERT_OUTPUT_OPERATION_COPY,
                    0);

                var old = Interlocked.Exchange(ref _displayPending, conv);
                conv = null;             // 슬롯이 소유권 가져감
                old?.Dispose();          // 보통 null. 안전망
            }
            finally
            {
                conv?.Dispose();         // 예외 시 정리
            }
        }

        // =====================================================================
        //  ③ 디스플레이 워커 스레드 — 다운스케일 + LockBits + 비트맵 publish
        // =====================================================================
        private void DisplayWorkerLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var conv = Interlocked.Exchange(ref _displayPending, null);
                    if (conv == null)
                    {
                        // 슬롯 비어있음 — 짧게 대기 후 재시도
                        // (이벤트 시그널 쓰면 더 효율적이지만 학습 단순화 위해 폴링)
                        try { Thread.Sleep(5); } catch { }
                        continue;
                    }

                    using (conv)
                    {
                        int w = (int)conv.Width;
                        int h = (int)conv.Height;
                        ProcessConvertedForDisplay(conv.Pixels, w, h);
                    }
                }
            }
            catch
            {
                // 워커 죽으면 디스플레이만 멈춤. 검사/그랩은 계속.
            }
        }

        // BGR8 픽셀 → 다운스케일 → 풀 비트맵에 LockBits 로 쓰기 → UI 슬롯에 publish
        private void ProcessConvertedForDisplay(IntPtr srcPixels, int srcW, int srcH)
        {
            var (dw, dh) = ComputeDisplaySize(srcW, srcH);
            var bmp = _pool.Rent(dw, dh);

            var bmpData = bmp.LockBits(
                new Rectangle(0, 0, dw, dh),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);
            try
            {
                using var src = new Mat(srcH, srcW, MatType.CV_8UC3, srcPixels);
                using var dst = new Mat(dh,   dw,   MatType.CV_8UC3,
                                        bmpData.Scan0, (long)bmpData.Stride);
                if (dw == srcW && dh == srcH)
                    src.CopyTo(dst);
                else
                    Cv2.Resize(src, dst, new OpenCvSharp.Size(dw, dh),
                               0, 0, InterpolationFlags.Area);
            }
            finally { bmp.UnlockBits(bmpData); }

            // UI 슬롯에 publish, 밀려난 이전 비트맵은 풀로
            var oldBmp = Interlocked.Exchange(ref _latestBitmap, bmp);
            if (oldBmp != null) _pool.Return(oldBmp);
        }

        // ── 다운스케일 사이즈 계산 (짝수 정렬 + max dim 제한) ─────────────────
        private static (int w, int h) ComputeDisplaySize(int w, int h)
        {
            int m = Math.Max(w, h);
            if (m <= MaxDisplayDim) return (w, h);
            int dw = (int)((long)w * MaxDisplayDim / m) & ~1;
            int dh = (int)((long)h * MaxDisplayDim / m) & ~1;
            return (Math.Max(dw, 2), Math.Max(dh, 2));
        }

        // =====================================================================
        //  ④ SDK 초기화 / 정리 (06 과 유사)
        // =====================================================================
        private void InitGrabber()
        {
            _gentl     = new EG.EGenTL(EG.CtiPath.Coaxlink);
            _discovery = new EG.EGrabberDiscovery(_gentl);
            _discovery.Discover(false);

            if (_discovery.GrabberCount == 0)
            {
                _discovery.Dispose(); _gentl.Dispose();

                _gentl     = new EG.EGenTL(EG.CtiPath.Playlink);
                _discovery = new EG.EGrabberDiscovery(_gentl);
                _discovery.Discover(false);

                if (_discovery.GrabberCount == 0) return;
            }

            _grabber = new EG.EGrabber(_discovery.EGrabbers[0]);

            try { _grabber.RunScript("config-rg.js"); } catch { /* 없으면 무시 */ }
        }

        private void CleanupAll()
        {
            try { _grabber?.Dispose();   } catch { } _grabber   = null;
            try { _discovery?.Dispose(); } catch { } _discovery = null;
            try { _gentl?.Dispose();     } catch { } _gentl     = null;

            _inspectionQueue?.Dispose(); _inspectionQueue = null;
            _cts?.Dispose();             _cts             = null;
            _eventTask = null; _inspectionTask = null; _displayTask = null;

            _videoPanel.SetImage(null);
            _histogramPanel.SetHistogram(null);
            var disp = Interlocked.Exchange(ref _displayBitmap, null);
            disp?.Dispose();
            var latest = Interlocked.Exchange(ref _latestBitmap, null);
            latest?.Dispose();
            Interlocked.Exchange(ref _latestStats, null);
            Interlocked.Exchange(ref _latestHistogram, null);
            _pool.Drain();
        }

        private void SetStatus(string text, Color color)
        {
            if (InvokeRequired) { BeginInvoke(() => SetStatus(text, color)); return; }
            _lblStatus.Text      = text;
            _lblStatus.ForeColor = color;
        }
    }
}
