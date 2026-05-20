// =============================================================================
//  예제 04 : Multi Camera
// =============================================================================
//  목적
//    EGrabberDiscovery.CameraCount 만큼 EGrabber 인스턴스를 만들어
//    각각 콜백 그랩을 동시에 돌린다.
//    카메라별 수신 프레임 수를 집계해 FPS 를 출력한다.
//
//  흐름 요약
//    1. CameraUnit 생성: EGrabber 연결 + 콜백 등록 + 버퍼 확보
//    2. 모든 CameraUnit.Start() 를 동시에 호출 → 병렬 그랩
//    3. GrabDurationSec 후 모든 CameraUnit.Stop()
//    4. 카메라별 FPS 출력
//
//  설계 포인트
//    - CameraUnit 마다 독립 CancellationTokenSource → 카메라별 개별 중단 가능.
//    - Count 는 public long (Interlocked 접근용) — lock 없이 원자적 증가.
//    - finally 블록으로 예외 발생 시에도 모든 EGrabber 해제 보장.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EG = Euresys.EGrabber;

namespace MultiCamera
{
    // ── CameraUnit: 카메라 한 대 단위 (EGrabber + 이벤트 루프 + 통계) ──────────────
    internal sealed class CameraUnit : IDisposable
    {
        public int    CameraIndex { get; }
        public string ModelName   { get; }
        public long   Count;           // Interlocked 로 증가

        private readonly EG.EGrabber           _grabber;
        private readonly CancellationTokenSource _cts  = new CancellationTokenSource();
        private          Task                    _task = Task.CompletedTask; // Stop() 에서 null 체크 없이 Wait() 가능

        public CameraUnit(EG.EGrabberCameraInfo info, int idx)
        {
            CameraIndex = idx;
            _grabber    = new EG.EGrabber(info);

            // 카메라 모델명 조회 (없으면 "(unknown)")
            try { ModelName = _grabber.Remote.Get<string>("DeviceModelName"); }
            catch { ModelName = "(unknown)"; }

            // 콜백 등록 : 람다 대신 별도 인스턴스 메서드 OnNewBuffer 를 등록.
            // 03_CallbackGrab 과 통일된 스타일. 인스턴스 메서드이므로 this.Count 자동 접근.
            _grabber.RegisterEventCallback<EG.NewBufferData>(OnNewBuffer);

            // 이벤트 활성화 및 버퍼 확보 (Start 전에 수행)
            _grabber.EnableEvent(EG.EventType.NewBufferData);
            _grabber.ReallocBuffers(4ul);
        }

        // ── 새 프레임 콜백 (인스턴스 메서드) ──────────────────────────────────────
        // SDK 이벤트 스레드(ProcessEventsAsync 가 만든 Task)에서 호출된다.
        // 인스턴스 메서드이므로 Count 는 자동으로 이 객체의 필드를 가리킨다 (this 생략).
        // 카메라가 N대면 N개의 CameraUnit 객체 각자의 Count 가 독립적으로 증가.
        private void OnNewBuffer(EG.EGrabber g, EG.NewBufferData data)
        {
            using (var buf = new EG.ScopedBuffer(g, data))
            {
                Interlocked.Increment(ref Count);
            }
        }

        // Start : 그랩 시작 + 이벤트 루프 가동
        public void Start()
        {
            // Start() : 인자 생략 = 무한 그랩 + AcquisitionStart 자동 실행 (유레시스 공식 권장)
            _grabber.Start();
            _task = _grabber.ProcessEventsAsync(EG.EventType.NewBufferData, _cts.Token);
        }

        // Stop : 이벤트 루프 중단 후 그랩 정지
        public void Stop()
        {
            _cts.Cancel();
            _task.Wait();
            _grabber.Stop();
        }

        public void Dispose() => _grabber.Dispose();
    }

    internal static class Program
    {
        private const int GrabDurationSec = 5;

        // ── OpenGenTL: Coaxlink → PlayLink 순서로 EGenTL 열기 ────────────────────────
        private static EG.EGenTL OpenGenTL(out string producer)
        {
            try
            {
                var g = new EG.EGenTL(EG.CtiPath.Coaxlink);
                using (var d = new EG.EGrabberDiscovery(g))
                {
                    d.Discover();
                    if (d.CameraCount > 0) { producer = "Coaxlink"; return g; }
                }
                g.Dispose();
            }
            catch { }

            producer = "PlayLink";
            return new EG.EGenTL(EG.CtiPath.Playlink);
        }

        private static int Main()
        {
            try
            {
                using (var gentl = OpenGenTL(out string producer))
                using (var discovery = new EG.EGrabberDiscovery(gentl))
                {
                    discovery.Discover();

                    int n = discovery.CameraCount;
                    Console.WriteLine($"Producer        : {producer}");
                    Console.WriteLine($"Physical boards : {discovery.InterfaceCount}");
                    Console.WriteLine($"Cameras found   : {n}");
                    if (n == 0)
                    {
                        Console.Error.WriteLine("No cameras connected.");
                        return 1;
                    }

                    var units = new List<CameraUnit>();
                    try
                    {
                        // 각 카메라에 대해 CameraUnit 생성 (콜백 등록까지)
                        for (int i = 0; i < n; ++i)
                        {
                            var u = new CameraUnit(discovery.Cameras[i], i);
                            units.Add(u);
                            Console.WriteLine($"  [cam {i}] {u.ModelName}");
                        }

                        Console.WriteLine();
                        Console.WriteLine($"Starting grab for {GrabDurationSec} seconds");

                        // 모든 카메라 동시 시작
                        foreach (var u in units) u.Start();

                        Thread.Sleep(GrabDurationSec * 1000);

                        // 모든 카메라 중단
                        foreach (var u in units) u.Stop();

                        Console.WriteLine();
                        Console.WriteLine("--- Results ---");
                        foreach (var u in units)
                        {
                            double fps = (double)u.Count / GrabDurationSec;
                            Console.WriteLine(
                                $"  cam {u.CameraIndex} ({u.ModelName,-20}) : {u.Count} frames ({fps:F1} fps)");
                        }
                    }
                    finally
                    {
                        // 예외가 발생해도 모든 CameraUnit(=EGrabber) 해제 보장
                        foreach (var u in units) u.Dispose();
                    }
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Error: {e.Message}");
                return 1;
            }
            finally
            {
                WaitForExit();
            }
            return 0;
        }

        // ── 콘솔 창이 즉시 닫히지 않도록 키 입력 대기 ─────────────────────────────
        // 입력이 리다이렉트된 경우(파이프/CI)는 hang 방지를 위해 그냥 통과한다.
        private static void WaitForExit()
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            if (!Console.IsInputRedirected) Console.ReadKey(true);
        }
    }
}
