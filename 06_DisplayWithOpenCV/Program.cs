// =============================================================================
//  예제 06 : Display With OpenCvSharp  (고속 그랩 안정 디스플레이 적용판)
// =============================================================================
//  목적
//    eGrabber 로 카메라 영상을 취득하여 WinForms 에 실시간 표시한다.
//    실제 Coaxlink/Grablink 보드가 없으면 PlayLink(소프트웨어 시뮬레이터)로 자동
//    전환되므로 하드웨어 없이도 동작을 확인할 수 있다.
//
//  HighSpeedDisplay_Guide.md 의 패턴 적용:
//    §1 고정 30 Hz 디스플레이 타이머 (그랩 fps 와 분리)
//    §2 단일 슬롯 + Interlocked.Exchange (락 없는 핸드오프)
//    §3 백프레셔 — UI 가 안 가져갔으면 Convert 자체를 스킵
//    §4 Bitmap 풀 (3 장) — GC 를 그랩 루프 밖으로
//    §5 다운스케일 (max 960 px) — 통계는 원본 기준
//    §6 LockBits + OpenCV Mat 으로 비트맵 메모리에 직접 쓰기
//    §7 OptimizedDoubleBuffer + Low 보간 — 깜빡임 방지
//
//  핵심 흐름
//    [그랩 스레드]                          [디스플레이 타이머 30 Hz, UI 스레드]
//    SDK 프레임 획득                          ↓ Tick
//    └ UI 가 이전 프레임 안 가져갔을 때만   _latestBitmap 슬롯에서 비트맵 consume
//      Convert → Downscale → LockBits         _latestStats 는 peek (표시용)
//      _latestBitmap 슬롯에 publish           VideoPanel.SetImage(bmp)
//    stats 는 항상 갱신                        이전 비트맵은 풀에 반환
//
//  중요: EGrabberDiscovery 수명
//    EGrabber 는 내부적으로 EGrabberDiscovery 의 핸들을 참조한다.
//    EGrabber 와 함께 해제해야 한다. (GC_ERR_INVALID_HANDLE 방지)
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

namespace DisplayWithOpenCV
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

    // ── 그랩 스레드 → UI 스레드로 넘기는 통계 스냅샷 ─────────────────────────────
    // record 라 불변. Interlocked.Exchange 로 원자적 교체.
    internal sealed record NodeStats(double Fps, int W, int H, double Mbps, long Frames);

    // ── Bitmap 풀 (§4) ──────────────────────────────────────────────────────────
    // 4K BGR24 비트맵을 매 프레임 new 하면 LOH 단편화로 화면이 끊긴다.
    // 사이즈가 바뀌면 풀을 통째로 재구축. 풀이 비어 있으면 일회용 비트맵 fallback.
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

    // ── 영상 표시 컨트롤 (§7) ───────────────────────────────────────────────────
    // PictureBox 대체. OptimizedDoubleBuffer + AllPaintingInWmPaint 로 자동
    // 더블버퍼 + WM_ERASEBKGND 무력화 → 깜빡임 0.
    // 라이브 영상은 30 Hz 로 새로 칠해지므로 보간은 Low 면 충분하다.
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
            BackColor = Color.Black;
            DoubleBuffered = true;
        }

        public void SetImage(Bitmap? bmp) { _image = bmp; Invalidate(); }

        protected override void OnPaintBackground(PaintEventArgs pevent) { /* skip */ }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g   = e.Graphics;
            var img = _image;
            if (img == null) { g.Clear(BackColor); return; }

            g.CompositingMode   = CompositingMode.SourceCopy;   // 알파 블렌딩 스킵
            g.InterpolationMode = InterpolationMode.Low;        // 빠른 보간
            g.PixelOffsetMode   = PixelOffsetMode.HighSpeed;
            g.Clear(BackColor);

            // Fit 모드 — 비율 유지, 화면에 꽉 채움
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
        // ── UI 컨트롤 ──────────────────────────────────────────────────────────
        private readonly VideoPanel _videoPanel;
        private readonly Button     _btnStart;
        private readonly Button     _btnStop;
        private readonly Label      _lblFrames;
        private readonly Label      _lblFps;
        private readonly Label      _lblResolution;
        private readonly Label      _lblMbps;
        private readonly Label      _lblStatus;

        // ── 그랩 ↔ 디스플레이 채널 (§2 단일 슬롯) ──────────────────────────────
        // bitmap 은 consume(꺼내면 null), stats 는 peek (Volatile.Read 만).
        // stats 를 consume 하면 화면 표시 한 번 누락되어 라벨이 깜빡인다.
        private Bitmap?     _latestBitmap;
        private NodeStats?  _latestStats;

        private readonly BitmapPool _pool = new();

        // 디스플레이 사이드에서 현재 화면에 띄워둔 비트맵 (다음 프레임 시 풀로 반환)
        private Bitmap?     _displayBitmap;

        // ── 그랩 상태 ──────────────────────────────────────────────────────────
        private CancellationTokenSource? _cts;
        private Task?                    _grabTask;

        private EG.EGenTL?            _gentl;
        private EG.EGrabberDiscovery? _discovery;
        private EG.EGrabber?          _grabber;

        // ── 디스플레이 타이머 (§1) ─────────────────────────────────────────────
        private readonly System.Windows.Forms.Timer _displayTimer;

        // 디스플레이용 최대 변(픽셀). 카메라가 4K 라도 화면에는 다운스케일된
        // 비트맵을 그린다. GDI+ 페인트 비용이 폭증하는 것을 막는다.
        // 통계(FPS/MB·s)는 원본 해상도 기준으로 계산.
        private const int MaxDisplayDim = 960;

        // FPS 슬라이딩 윈도우 (1 초)
        private const long FpsWindowSec = 1;

        // ── 생성자: UI 레이아웃 ────────────────────────────────────────────────
        public MainForm()
        {
            Text          = "Euresys Grab Viewer (eGrabber + OpenCvSharp)";
            ClientSize    = new System.Drawing.Size(1000, 720);
            MinimumSize   = new System.Drawing.Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            _videoPanel = new VideoPanel { Dock = DockStyle.Fill };

            var bottomPanel = new Panel
            {
                Dock    = DockStyle.Bottom,
                Height  = 50,
                Padding = new Padding(8, 6, 8, 6)
            };

            _btnStart      = new Button { Text = "Start", Width = 80, Left =   8, Top = 10 };
            _btnStop       = new Button { Text = "Stop",  Width = 80, Left =  96, Top = 10, Enabled = false };
            _lblFrames     = new Label  { Text = "Frames: 0",   AutoSize = true, Left = 200, Top = 14, ForeColor = Color.DimGray };
            _lblFps        = new Label  { Text = "FPS: --",     AutoSize = true, Left = 310, Top = 14, ForeColor = Color.DimGray };
            _lblResolution = new Label  { Text = "----x----",   AutoSize = true, Left = 410, Top = 14, ForeColor = Color.DimGray };
            _lblMbps       = new Label  { Text = "-- MB/s",     AutoSize = true, Left = 520, Top = 14, ForeColor = Color.DimGray };
            _lblStatus     = new Label  { Text = "Ready",       AutoSize = true, Left = 630, Top = 14, ForeColor = Color.DimGray };

            _btnStart.Click += BtnStart_Click;
            _btnStop.Click  += BtnStop_Click;

            bottomPanel.Controls.AddRange(new Control[]
            {
                _btnStart, _btnStop, _lblFrames, _lblFps, _lblResolution, _lblMbps, _lblStatus
            });
            Controls.Add(_videoPanel);
            Controls.Add(bottomPanel);

            FormClosing += MainForm_FormClosing;

            // 디스플레이 타이머: 33 ms ≈ 30 Hz. 그랩 fps 와 완전 분리.
            _displayTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _displayTimer.Tick += DisplayTimer_Tick;
            _displayTimer.Start();
        }

        // ── 디스플레이 타이머 Tick (UI 스레드) ────────────────────────────────
        // - bitmap 슬롯에서 consume → 화면 갱신 + 이전 비트맵 풀에 반환
        // - stats 슬롯에서 peek → 라벨 갱신
        private void DisplayTimer_Tick(object? sender, EventArgs e)
        {
            // §2 단일 슬롯 핸드오프
            var bmp = Interlocked.Exchange(ref _latestBitmap, null);
            if (bmp != null)
            {
                var old = _displayBitmap;
                _displayBitmap = bmp;
                _videoPanel.SetImage(bmp);
                if (old != null) _pool.Return(old);
            }

            var stats = Volatile.Read(ref _latestStats);
            if (stats != null)
            {
                _lblFrames.Text     = $"Frames: {stats.Frames,8}";
                _lblResolution.Text = $"{stats.W}x{stats.H}";
                _lblMbps.Text       = $"{stats.Mbps,6:F0} MB/s";

                Color fpsColor = stats.Fps >= 30 ? Color.Green
                               : stats.Fps >= 15 ? Color.DarkOrange
                                                 : Color.Red;
                _lblFps.ForeColor = fpsColor;
                _lblFps.Text      = $"FPS: {stats.Fps,6:F1}";
            }
        }

        // ── Start ─────────────────────────────────────────────────────────────
        private void BtnStart_Click(object? sender, EventArgs e)
        {
            _btnStart.Enabled = false;
            _btnStop.Enabled  = true;
            SetStatus("Initializing...", Color.DarkOrange);

            _cts      = new CancellationTokenSource();
            _grabTask = Task.Run(() => GrabLoop(_cts.Token));
        }

        // ── Stop ──────────────────────────────────────────────────────────────
        private async void BtnStop_Click(object? sender, EventArgs e)
        {
            _btnStop.Enabled = false;
            SetStatus("Stopping...", Color.DarkOrange);

            _cts?.Cancel();
            if (_grabTask != null)
                await _grabTask.ConfigureAwait(true);

            CleanupGrabber();
            ReleaseDisplay();
            _btnStart.Enabled = true;
            SetStatus("Stopped", Color.DimGray);
        }

        // ── 창 닫기 ───────────────────────────────────────────────────────────
        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _displayTimer.Stop();

            if (_grabTask == null || _grabTask.IsCompleted)
            {
                CleanupGrabber();
                ReleaseDisplay();
                return;
            }

            e.Cancel = true;
            _cts?.Cancel();
            _grabTask.ContinueWith(_ => BeginInvoke((Action)Close), TaskScheduler.Default);
        }

        // ── 화면/풀 리소스 정리 ───────────────────────────────────────────────
        private void ReleaseDisplay()
        {
            _videoPanel.SetImage(null);
            var disp   = Interlocked.Exchange(ref _displayBitmap, null);
            var latest = Interlocked.Exchange(ref _latestBitmap, null);
            disp?.Dispose();
            latest?.Dispose();
            Interlocked.Exchange(ref _latestStats, null);
            _pool.Drain();
        }

        // ── 그랩 백그라운드 루프 ──────────────────────────────────────────────
        private void GrabLoop(CancellationToken ct)
        {
            try
            {
                InitGrabber();
                if (_grabber == null) return;   // 그래버 없음 → 종료

                _grabber.ReallocBuffers(3ul);
                _grabber.Start();   // 인자 생략 = 무한 그랩 (유레시스 공식 권장)

                // FPS: 1 초 슬라이딩 윈도우 내 프레임 수
                var  frameTs     = new System.Collections.Generic.Queue<long>();
                long windowTicks = Stopwatch.Frequency * FpsWindowSec;
                var  sw          = Stopwatch.StartNew();
                long totalFrames = 0;

                while (!ct.IsCancellationRequested)
                {
                    EG.ScopedBuffer? buf = null;
                    try
                    {
                        buf = new EG.ScopedBuffer(_grabber, 500ul);
                        var  bi  = buf.GetInfo();
                        int  w   = (int)bi.Width;
                        int  h   = (int)bi.DeliveredHeight;
                        uint bpp = bi.BitsPerPixel;

                        // FPS 갱신 (싸니까 매번)
                        long now = sw.ElapsedTicks;
                        frameTs.Enqueue(now);
                        while (frameTs.Count > 0 && now - frameTs.Peek() > windowTicks)
                            frameTs.Dequeue();
                        double fps = frameTs.Count;
                        totalFrames++;

                        double mbps = w * h * (bpp / 8.0) * fps / 1_000_000.0;

                        // §3 백프레셔: UI 가 이전 프레임 안 가져갔으면 변환 스킵.
                        // Convert/Resize/LockBits 가 비싸므로 슬롯이 비었을 때만 수행.
                        if (w > 0 && h > 0 && Volatile.Read(ref _latestBitmap) == null)
                        {
                            EG.ConvertedBuffer? conv = null;
                            try
                            {
                                conv = buf.Convert(
                                    "BGR8",
                                    EG.IMAGE_CONVERT_OUTPUT_CONFIG.IMAGE_CONVERT_OUTPUT_CONFIG_DEFAULT,
                                    EG.IMAGE_CONVERT_OUTPUT_OPERATION.IMAGE_CONVERT_OUTPUT_OPERATION_COPY,
                                    0);

                                var (dw, dh) = ComputeDisplaySize(w, h);
                                var bmp      = _pool.Rent(dw, dh);

                                // §6 LockBits + OpenCV Mat → 비트맵 메모리에 직접 쓰기.
                                // 중간 byte[] 거치지 않음. SIMD 한 번에 끝.
                                var bmpData = bmp.LockBits(
                                    new Rectangle(0, 0, dw, dh),
                                    ImageLockMode.WriteOnly,
                                    PixelFormat.Format24bppRgb);
                                try
                                {
                                    using var src = new Mat(h,  w,  MatType.CV_8UC3, conv.Pixels);
                                    using var dst = new Mat(dh, dw, MatType.CV_8UC3,
                                                            bmpData.Scan0, (long)bmpData.Stride);
                                    if (dw == w && dh == h)
                                        src.CopyTo(dst);
                                    else
                                        Cv2.Resize(src, dst, new OpenCvSharp.Size(dw, dh),
                                                   0, 0, InterpolationFlags.Area);
                                }
                                finally { bmp.UnlockBits(bmpData); }

                                // §2 슬롯에 publish, 밀려난 이전 비트맵은 풀로 반환
                                var oldBmp = Interlocked.Exchange(ref _latestBitmap, bmp);
                                if (oldBmp != null) _pool.Return(oldBmp);
                            }
                            finally { conv?.Dispose(); }
                        }

                        // stats 는 항상 최신값 publish (백프레셔로 비트맵 스킵돼도 라벨은 갱신)
                        Interlocked.Exchange(ref _latestStats,
                            new NodeStats(fps, w, h, mbps, totalFrames));
                    }
                    catch (TimeoutException) { /* ct 체크 후 재시도 */ }
                    finally
                    {
                        buf?.Dispose();
                    }
                }

                _grabber.Stop();
                Interlocked.Exchange(ref _latestStats, null);
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    BeginInvoke(() => SetStatus($"Error: {ex.Message}", Color.Red));
            }
        }

        // ── 다운스케일 사이즈 계산 (§5) ───────────────────────────────────────
        // 짝수 정렬(`& ~1`) — OpenCV/SIMD/스트라이드 안전
        private static (int w, int h) ComputeDisplaySize(int w, int h)
        {
            int m = Math.Max(w, h);
            if (m <= MaxDisplayDim) return (w, h);
            int dw = (int)((long)w * MaxDisplayDim / m) & ~1;
            int dh = (int)((long)h * MaxDisplayDim / m) & ~1;
            return (Math.Max(dw, 2), Math.Max(dh, 2));
        }

        // ── 그래버 초기화: Coaxlink 없으면 PlayLink 폴백 ──────────────────────
        // 상태 변경 콜백은 BeginInvoke 로 비동기 마샬링 — 그랩 스레드 블로킹 방지.
        private void InitGrabber()
        {
            _gentl     = new EG.EGenTL(EG.CtiPath.Coaxlink);
            _discovery = new EG.EGrabberDiscovery(_gentl);
            _discovery.Discover(false);

            if (_discovery.GrabberCount == 0)
            {
                _discovery.Dispose();
                _gentl.Dispose();

                _gentl     = new EG.EGenTL(EG.CtiPath.Playlink);
                _discovery = new EG.EGrabberDiscovery(_gentl);
                _discovery.Discover(false);

                if (_discovery.GrabberCount == 0)
                {
                    BeginInvoke(() => SetStatus("No grabber found", Color.Red));
                    return;
                }

                BeginInvoke(() => SetStatus("Grabbing (PlayLink)", Color.DarkBlue));
            }
            else
            {
                BeginInvoke(() => SetStatus("Grabbing", Color.Green));
            }

            _grabber = new EG.EGrabber(_discovery.EGrabbers[0]);

            try { _grabber.RunScript("config-rg.js"); } catch { /* 없으면 무시 */ }
        }

        // ── 리소스 해제: 생성 역순으로 ───────────────────────────────────────
        private void CleanupGrabber()
        {
            _grabber?.Dispose();   _grabber   = null;
            _discovery?.Dispose(); _discovery = null;
            _gentl?.Dispose();     _gentl     = null;
        }

        private void SetStatus(string text, Color color)
        {
            _lblStatus.Text      = text;
            _lblStatus.ForeColor = color;
        }
    }
}
