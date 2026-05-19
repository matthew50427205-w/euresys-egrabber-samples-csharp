// =============================================================================
//  예제 04 : Multi Camera  -  여러 대 동시 그랩
// -----------------------------------------------------------------------------
//  목적
//    - EGrabberDiscovery.CameraCount 만큼 EGrabber 인스턴스를 만들고
//      각각 콜백 그랩을 동시에 돌린다.
//    - 카메라별 받은 프레임 수를 집계 후 fps 출력.
//
//  핵심 패턴
//    - 각 CameraUnit 이 RegisterEventCallback + ProcessEventsAsync 를 독립 실행.
//    - CancellationTokenSource 로 모든 카메라의 이벤트 루프를 일제히 중단.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EG = Euresys.EGrabber;

namespace MultiCamera
{
    // ------------------------------------------------------------
    //  카메라 한 대 단위 : EGrabber + 이벤트 루프 + 통계
    // ------------------------------------------------------------
    internal sealed class CameraUnit : IDisposable
    {
        public int    CameraIndex { get; }
        public string ModelName   { get; }
        public long   Count;           // Interlocked 로 증가

        private readonly EG.EGrabber           _grabber;
        private readonly CancellationTokenSource _cts  = new CancellationTokenSource();
        private          Task                    _task = Task.CompletedTask;

        public CameraUnit(EG.EGrabberCameraInfo info, int idx)
        {
            CameraIndex = idx;
            _grabber    = new EG.EGrabber(info);

            // 카메라 모델명 조회 (없으면 "(unknown)")
            try { ModelName = _grabber.Remote.Get<string>("DeviceModelName"); }
            catch { ModelName = "(unknown)"; }

            // 콜백 등록 : 프레임 수만 세고, 실 응용에서는 여기서 이미지 처리
            _grabber.RegisterEventCallback<EG.NewBufferData>((g, data) =>
            {
                using (var buf = new EG.ScopedBuffer(g, data))
                {
                    Interlocked.Increment(ref Count);
                }
            });

            // 이벤트 활성화 및 버퍼 확보 (Start 전에 수행)
            _grabber.EnableEvent(EG.EventType.NewBufferData);
            _grabber.ReallocBuffers(4ul);
        }

        // Start : 그랩 시작 + 이벤트 루프 가동
        public void Start()
        {
            // controlRemoteDevice=true : AcquisitionStart 명령 자동 실행
            _grabber.Start(ulong.MaxValue, true);
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

        // 실 Coaxlink → PlayLink 순서로 시도
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
                    Console.WriteLine($"Producer  : {producer}");
                    Console.WriteLine($"발견 카메라 : {n}");
                    if (n == 0)
                    {
                        Console.Error.WriteLine("연결된 카메라가 없습니다.");
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
                        Console.WriteLine($"{GrabDurationSec} 초간 그랩 시작");

                        // 모든 카메라 동시 시작
                        foreach (var u in units) u.Start();

                        Thread.Sleep(GrabDurationSec * 1000);

                        // 모든 카메라 중단
                        foreach (var u in units) u.Stop();

                        Console.WriteLine();
                        Console.WriteLine("--- 결과 ---");
                        foreach (var u in units)
                        {
                            double fps = (double)u.Count / GrabDurationSec;
                            Console.WriteLine(
                                $"  cam {u.CameraIndex} ({u.ModelName,-20}) : {u.Count} frames ({fps:F1} fps)");
                        }
                    }
                    finally
                    {
                        foreach (var u in units) u.Dispose();
                    }
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"오류: {e.Message}");
                return 1;
            }
            return 0;
        }
    }
}
