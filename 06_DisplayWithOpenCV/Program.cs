// =============================================================================
//  예제 06 : Display With OpenCvSharp  -  WinForms PictureBox 에 실시간 표시
// -----------------------------------------------------------------------------
//  목적
//    - 사용자분의 EuresysGrab 프로젝트와 동일한 패턴.
//    - eGrabber 의 ConvertedBuffer(BGR8) → cv::Mat → Bitmap → PictureBox.
//    - Start/Stop 버튼, FPS / Frames 카운터, 상태 표시.
//
//  설계
//    - 그랩은 백그라운드 Task (CancellationToken) 에서 동기식 ScopedBuffer 루프.
//    - UI 갱신은 Control.Invoke 로 UI 스레드에 마샬링.
//    - 보드 없으면 PlayLink 자동 폴백.
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
        // UI
        private readonly PictureBox _pictureBox;
        private readonly Button     _btnStart;
        private readonly Button     _btnStop;
        private readonly Label      _lblFrames;
        private readonly Label      _lblFps;
        private readonly Label      _lblStatus;

        // Grab 상태
        private CancellationTokenSource? _cts;
        private Task?                    _grabTask;
        private EG.EGenTL?               _gentl;
        private EG.EGrabber?             _grabber;

        private const int FpsWindow = 30;

        public MainForm()
        {
            Text          = "Euresys Grab Viewer (eGrabber + OpenCvSharp)";
            ClientSize    = new System.Drawing.Size(1000, 720);
            MinimumSize   = new System.Drawing.Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;

            _pictureBox = new PictureBox
            {
                Dock      = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode  = PictureBoxSizeMode.Zoom
            };

            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8, 6, 8, 6) };

            _btnStart  = new Button { Text = "Start", Width = 80, Left =  8, Top = 10 };
            _btnStop   = new Button { Text = "Stop",  Width = 80, Left = 96, Top = 10, Enabled = false };
            _lblFrames = new Label  { Text = "Frames: 0",  AutoSize = true, Left = 200, Top = 14, ForeColor = Color.DimGray };
            _lblFps    = new Label  { Text = "FPS: --",    AutoSize = true, Left = 310, Top = 14, ForeColor = Color.DimGray };
            _lblStatus = new Label  { Text = "Ready",      AutoSize = true, Left = 420, Top = 14, ForeColor = Color.DimGray };

            _btnStart.Click += BtnStart_Click;
            _btnStop.Click  += BtnStop_Click;

            bottomPanel.Controls.AddRange(new Control[]
                { _btnStart, _btnStop, _lblFrames, _lblFps, _lblStatus });

            Controls.Add(_pictureBox);
            Controls.Add(bottomPanel);

            FormClosing += MainForm_FormClosing;
        }

        // ─────────── Start ───────────
        private void BtnStart_Click(object? sender, EventArgs e)
        {
            _btnStart.Enabled = false;
            _btnStop.Enabled  = true;
            SetStatus("Initializing...", Color.DarkOrange);

            _cts      = new CancellationTokenSource();
            _grabTask = Task.Run(() => GrabLoop(_cts.Token));
        }

        // ─────────── Stop ───────────
        private async void BtnStop_Click(object? sender, EventArgs e)
        {
            _btnStop.Enabled = false;
            SetStatus("Stopping...", Color.DarkOrange);

            _cts?.Cancel();
            if (_grabTask != null) await _grabTask.ConfigureAwait(false);

            CleanupGrabber();

            Invoke(() =>
            {
                _btnStart.Enabled = true;
                _pictureBox.Image = null;
                SetStatus("Stopped", Color.DimGray);
            });
        }

        private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _cts?.Cancel();
            if (_grabTask != null) await _grabTask.ConfigureAwait(false);
            CleanupGrabber();
        }

        // ─────────── Grab 백그라운드 루프 ───────────
        private void GrabLoop(CancellationToken ct)
        {
            try
            {
                // 1) 하드웨어 먼저, 없으면 PlayLink
                //    → 사용자 EuresysGrab 프로젝트의 패턴을 그대로 따름.
                _gentl = new EG.EGenTL(EG.CtiPath.Coaxlink);
                using (var discovery = new EG.EGrabberDiscovery(_gentl))
                {
                    discovery.Discover(false);

                    if (discovery.GrabberCount == 0)
                    {
                        _gentl.Dispose();
                        _gentl = new EG.EGenTL(EG.CtiPath.Playlink);
                        using (var d2 = new EG.EGrabberDiscovery(_gentl))
                        {
                            d2.Discover(false);
                            if (d2.GrabberCount == 0)
                            {
                                Invoke(() => SetStatus("그래버 없음", Color.Red));
                                return;
                            }
                            _grabber = new EG.EGrabber(d2.EGrabbers[0]);
                        }
                        Invoke(() => SetStatus("Grabbing (PlayLink)", Color.DarkBlue));
                    }
                    else
                    {
                        _grabber = new EG.EGrabber(discovery.EGrabbers[0]);
                        Invoke(() => SetStatus("Grabbing", Color.Green));
                    }
                }

                // 2) (선택) runScript - 같은 폴더에 있으면 적용
                try { _grabber.RunScript("config-rg.js"); } catch { /* 무시 */ }

                _grabber.ReallocBuffers(3ul);
                _grabber.Start(ulong.MaxValue, true);

                // 3) 그랩 루프
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
                        // 500 ms 타임아웃: 취소 응답성 + 타임아웃 시 그냥 다시 시도
                        buf  = new EG.ScopedBuffer(_grabber, 500ul);

                        // BGR8 로 변환 (어떤 픽셀 포맷이든)
                        conv = buf.Convert(
                            "BGR8",
                            EG.IMAGE_CONVERT_OUTPUT_CONFIG.IMAGE_CONVERT_OUTPUT_CONFIG_DEFAULT,
                            EG.IMAGE_CONVERT_OUTPUT_OPERATION.IMAGE_CONVERT_OUTPUT_OPERATION_COPY,
                            0);

                        int    w      = (int)conv.Width;
                        int    h      = (int)conv.Height;
                        IntPtr pixels = conv.Pixels;

                        totalFrames++;
                        long now = sw.ElapsedTicks;
                        tsQueue.Enqueue(now);
                        if (tsQueue.Count > FpsWindow)
                        {
                            long oldest = tsQueue.Dequeue();
                            fps = (FpsWindow - 1) / TimeSpan.FromTicks(now - oldest).TotalSeconds;
                        }

                        using var mat = new Mat(h, w, MatType.CV_8UC3, pixels);
                        Bitmap bmp = MatToBitmap(mat);

                        long   capturedFrames = totalFrames;
                        double capturedFps    = fps;

                        Invoke(() =>
                        {
                            var old = _pictureBox.Image;
                            _pictureBox.Image = bmp;
                            old?.Dispose();

                            _lblFrames.Text = $"Frames: {capturedFrames,8}";
                            Color fpsColor =
                                  capturedFps >= 30 ? Color.Green
                                : capturedFps >= 15 ? Color.DarkOrange
                                                    : Color.Red;
                            _lblFps.ForeColor = fpsColor;
                            _lblFps.Text      = $"FPS: {capturedFps,6:F1}";
                        });
                    }
                    catch (TimeoutException)
                    {
                        // 500ms 타임아웃 -> ct 만 체크하고 다시 시도
                    }
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
                Invoke(() => SetStatus($"오류: {ex.Message}", Color.Red));
            }
        }

        // OpenCvSharp Mat(BGR8) → System.Drawing.Bitmap
        private static Bitmap MatToBitmap(Mat bgr)
        {
            int w = bgr.Width;
            int h = bgr.Height;

            // Mat 데이터를 직접 wrap (복사 없음) -> Clone 으로 독립 버퍼 확보
            var tmp = new Bitmap(w, h, (int)bgr.Step(), PixelFormat.Format24bppRgb, bgr.Data);
            var bmp = (Bitmap)tmp.Clone();
            tmp.Dispose();
            return bmp;
        }

        private void CleanupGrabber()
        {
            _grabber?.Dispose();
            _grabber = null;
            _gentl?.Dispose();
            _gentl = null;
        }

        private void SetStatus(string text, Color color)
        {
            _lblStatus.Text      = text;
            _lblStatus.ForeColor = color;
        }
    }
}
