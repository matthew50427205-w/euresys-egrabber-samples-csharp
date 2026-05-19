// =============================================================================
//  예제 06 : Display With OpenCvSharp
// =============================================================================
//  목적
//    eGrabber 로 카메라 영상을 취득하여 WinForms PictureBox 에 실시간 표시한다.
//    실제 Coaxlink/Grablink 보드가 없으면 PlayLink(소프트웨어 시뮬레이터)로 자동
//    전환되므로 하드웨어 없이도 동작을 확인할 수 있다.
//
//  흐름 요약
//    1. EGenTL → EGrabberDiscovery.Discover() → EGrabber 생성
//    2. EGrabber.Start() 로 연속 취득 시작
//    3. 백그라운드 Task 에서 ScopedBuffer 로 프레임 획득
//    4. ScopedBuffer.Convert("BGR8") 로 픽셀 포맷 정규화
//    5. ConvertedBuffer.Pixels(IntPtr) 를 OpenCvSharp Mat 으로 래핑
//    6. Mat → System.Drawing.Bitmap 변환 후 Control.Invoke 로 UI 스레드에 표시
//
//  중요: EGrabberDiscovery 수명
//    EGrabber 는 내부적으로 EGrabberDiscovery 의 핸들을 참조한다.
//    따라서 EGrabberDiscovery 는 EGrabber 와 함께 해제해야 한다.
//    (using 블록으로 먼저 해제하면 GC_ERR_INVALID_HANDLE 발생)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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

    public class MainForm : Form
    {
        // ── UI 컨트롤 ──────────────────────────────────────────────────────────
        private readonly PictureBox _pictureBox;
        private readonly Button     _btnStart;
        private readonly Button     _btnStop;
        private readonly Label      _lblFrames;
        private readonly Label      _lblFps;
        private readonly Label      _lblStatus;

        // ── 그랩 상태 ──────────────────────────────────────────────────────────
        // _cts: Stop 버튼 또는 창 닫기 시 그랩 루프를 취소하는 토큰 소스
        private CancellationTokenSource? _cts;
        private Task?                    _grabTask;

        // EGenTL / EGrabberDiscovery / EGrabber 는 함께 생성되고 함께 해제된다.
        private EG.EGenTL?            _gentl;
        private EG.EGrabberDiscovery? _discovery;
        private EG.EGrabber?          _grabber;

        // FPS 계산에 사용할 슬라이딩 윈도우 크기
        private const int FpsWindow = 30;

        // ── 생성자: UI 레이아웃 ────────────────────────────────────────────────
        public MainForm()
        {
            Text          = "Euresys Grab Viewer (eGrabber + OpenCvSharp)";
            ClientSize    = new System.Drawing.Size(1000, 720);
            MinimumSize   = new System.Drawing.Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;

            // 영상 표시 영역 - 비율 유지(Zoom)하며 전체 채움
            _pictureBox = new PictureBox
            {
                Dock      = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode  = PictureBoxSizeMode.Zoom
            };

            var bottomPanel = new Panel
            {
                Dock    = DockStyle.Bottom,
                Height  = 50,
                Padding = new Padding(8, 6, 8, 6)
            };

            _btnStart  = new Button { Text = "Start", Width = 80, Left =   8, Top = 10 };
            _btnStop   = new Button { Text = "Stop",  Width = 80, Left =  96, Top = 10, Enabled = false };
            _lblFrames = new Label  { Text = "Frames: 0", AutoSize = true, Left = 200, Top = 14, ForeColor = Color.DimGray };
            _lblFps    = new Label  { Text = "FPS: --",   AutoSize = true, Left = 310, Top = 14, ForeColor = Color.DimGray };
            _lblStatus = new Label  { Text = "Ready",     AutoSize = true, Left = 420, Top = 14, ForeColor = Color.DimGray };

            _btnStart.Click += BtnStart_Click;
            _btnStop.Click  += BtnStop_Click;

            bottomPanel.Controls.AddRange(new Control[] { _btnStart, _btnStop, _lblFrames, _lblFps, _lblStatus });
            Controls.Add(_pictureBox);
            Controls.Add(bottomPanel);

            FormClosing += MainForm_FormClosing;
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
                await _grabTask.ConfigureAwait(true); // true: UI 스레드로 복귀

            CleanupGrabber();
            _pictureBox.Image = null;
            _btnStart.Enabled = true;
            SetStatus("Stopped", Color.DimGray);
        }

        // ── 창 닫기: 그랩이 실행 중이면 취소 후 완료를 기다렸다가 닫는다 ────────
        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (_grabTask == null || _grabTask.IsCompleted)
            {
                CleanupGrabber();
                return;
            }

            // 닫기를 일단 취소하고, 그랩 루프가 끝나면 다시 Close() 호출
            e.Cancel = true;
            _cts?.Cancel();
            _grabTask.ContinueWith(_ => Invoke(Close), TaskScheduler.Default);
        }

        // ── 그랩 백그라운드 루프 ──────────────────────────────────────────────
        private void GrabLoop(CancellationToken ct)
        {
            try
            {
                InitGrabber();

                // DMA 버퍼 3개 확보 후 연속 취득 시작
                // ulong.MaxValue: 프레임 수 제한 없이 Stop() 호출 전까지 계속 취득
                // controlRemoteDevice=true: 카메라 AcquisitionStart 명령 자동 전송
                _grabber!.ReallocBuffers(3ul);
                _grabber.Start(ulong.MaxValue, true);

                var    tsQueue     = new Queue<long>();
                var    sw          = Stopwatch.StartNew();
                long   totalFrames = 0;
                double fps         = 0.0;

                while (!ct.IsCancellationRequested)
                {
                    EG.ScopedBuffer?    buf  = null;
                    EG.ConvertedBuffer? conv = null;
                    try
                    {
                        // ScopedBuffer: 큐에서 프레임 하나를 꺼내는 RAII 래퍼.
                        // Dispose() 시 버퍼가 자동으로 큐에 반환된다.
                        // 500ms 안에 프레임이 없으면 TimeoutException → ct 체크 후 재시도.
                        buf = new EG.ScopedBuffer(_grabber, 500ul);

                        // ConvertedBuffer: 카메라 원본 픽셀 포맷(Mono8, BayerRG8 등)을
                        // BGR8 로 변환한 독립 버퍼. conv.Pixels 로 raw 포인터에 접근한다.
                        conv = buf.Convert(
                            "BGR8",
                            EG.IMAGE_CONVERT_OUTPUT_CONFIG.IMAGE_CONVERT_OUTPUT_CONFIG_DEFAULT,
                            EG.IMAGE_CONVERT_OUTPUT_OPERATION.IMAGE_CONVERT_OUTPUT_OPERATION_COPY,
                            0);

                        int    w   = (int)conv.Width;
                        int    h   = (int)conv.Height;
                        IntPtr ptr = conv.Pixels;

                        // FPS: 최근 FpsWindow 개 프레임의 타임스탬프 차이로 계산
                        totalFrames++;
                        long now = sw.ElapsedTicks;
                        tsQueue.Enqueue(now);
                        if (tsQueue.Count > FpsWindow)
                        {
                            long oldest = tsQueue.Dequeue();
                            fps = (FpsWindow - 1) / TimeSpan.FromTicks(now - oldest).TotalSeconds;
                        }

                        // Mat: conv.Pixels 가 가리키는 메모리를 복사 없이 래핑.
                        // MatToBitmap 내부에서 Clone() 으로 독립 버퍼를 만들기 때문에
                        // conv.Dispose() 이후에도 Bitmap 은 유효하다.
                        using var mat = new Mat(h, w, MatType.CV_8UC3, ptr);
                        var bmp = MatToBitmap(mat);

                        long   frames     = totalFrames;
                        double capturedFps = fps;

                        // Control.Invoke: 백그라운드 스레드에서 UI를 직접 건드리면
                        // 크로스-스레드 예외가 발생하므로, UI 스레드에 위임한다.
                        Invoke(() =>
                        {
                            var old = _pictureBox.Image;
                            _pictureBox.Image = bmp;
                            old?.Dispose(); // 이전 Bitmap 즉시 해제 (GDI 리소스 누수 방지)

                            _lblFrames.Text = $"Frames: {frames,8}";

                            Color fpsColor = capturedFps >= 30 ? Color.Green
                                           : capturedFps >= 15 ? Color.DarkOrange
                                                               : Color.Red;
                            _lblFps.ForeColor = fpsColor;
                            _lblFps.Text      = $"FPS: {capturedFps,6:F1}";
                        });
                    }
                    catch (TimeoutException) { /* 타임아웃은 정상 - ct 체크 후 재시도 */ }
                    finally
                    {
                        conv?.Dispose();
                        buf?.Dispose();
                    }
                }

                _grabber.Stop();
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    Invoke(() => SetStatus($"오류: {ex.Message}", Color.Red));
            }
        }

        // ── 그래버 초기화: Coaxlink 없으면 PlayLink 폴백 ──────────────────────
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
                    Invoke(() => SetStatus("그래버 없음", Color.Red));
                    return;
                }

                Invoke(() => SetStatus("Grabbing (PlayLink)", Color.DarkBlue));
            }
            else
            {
                Invoke(() => SetStatus("Grabbing", Color.Green));
            }

            _grabber = new EG.EGrabber(_discovery.EGrabbers[0]);

            // 같은 폴더에 config-rg.js 가 있으면 카메라 설정 스크립트 적용
            try { _grabber.RunScript("config-rg.js"); } catch { /* 없으면 무시 */ }
        }

        // ── OpenCvSharp Mat(BGR8) → System.Drawing.Bitmap ────────────────────
        // Bitmap(IntPtr) 생성자는 외부 포인터를 참조만 한다.
        // Clone() 으로 독립 메모리로 복사해야 conv 해제 후에도 안전하다.
        private static Bitmap MatToBitmap(Mat bgr)
        {
            var tmp = new Bitmap(bgr.Width, bgr.Height, (int)bgr.Step(), PixelFormat.Format24bppRgb, bgr.Data);
            var bmp = (Bitmap)tmp.Clone();
            tmp.Dispose();
            return bmp;
        }

        // ── 리소스 해제: 생성 역순으로 (Grabber → Discovery → GenTL) ──────────
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
