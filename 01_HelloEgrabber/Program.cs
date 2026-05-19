// =============================================================================
//  예제 01 : Hello eGrabber
// =============================================================================
//  목적
//    PC 에 장착된 Coaxlink 보드와 카메라를 찾아 정보를 출력한다.
//    실 보드가 없으면 PlayLink(소프트웨어 시뮬레이터)로 자동 전환되므로
//    하드웨어 없이도 동작을 확인할 수 있다.
//
//  흐름 요약
//    1. EGenTL(Coaxlink) + EGrabberDiscovery.Discover() → grabber / camera 목록 출력
//    2. grabber 없으면 EGenTL(PlayLink) 로 재시도
//    3. 첫 번째 grabber 에 연결 → Interface / Device / Remote 파라미터 출력
//
//  .NET SDK API 요점
//    - EGenTL(ctiPath)         : CtiPath.Coaxlink / CtiPath.Playlink 는 string 상수.
//    - discovery.GrabberCount  : C++ 의 egrabberCount() 에 해당.
//    - grabber.Remote.Get<T>() : 카메라(RemoteModule) 파라미터 읽기.
//    - grabber.Interface / .Device : 보드 인터페이스 / 디바이스 모듈 접근.
//
//  주의: IsRemoteAvailable
//    grabber 가 발견되어도 카메라가 연결·초기화되지 않으면 Remote 모듈이 없다.
//    info.IsRemoteAvailable 을 확인한 뒤 Remote.Get() 을 호출해야 예외를 피할 수 있다.
// =============================================================================

using System;
using EG = Euresys.EGrabber;

namespace HelloEgrabber
{
    internal static class Program
    {
        // ── DiscoverWith: 지정 Producer 로 시스템 스캔 후 grabber / camera 목록 출력 ──
        // cti: CtiPath.Coaxlink 또는 CtiPath.Playlink   반환: 발견된 grabber 수 (실패=0)
        private static int DiscoverWith(string cti, string tag)
        {
            try
            {
                using (var gentl     = new EG.EGenTL(cti))
                using (var discovery = new EG.EGrabberDiscovery(gentl))
                {
                    discovery.Discover();
                    Console.WriteLine();
                    Console.WriteLine("========================================");
                    Console.WriteLine($"  {tag} 로 스캔");
                    Console.WriteLine("========================================");
                    Console.WriteLine($"egrabbers : {discovery.GrabberCount}");
                    Console.WriteLine($"cameras   : {discovery.CameraCount}");

                    // egrabbers 목록 출력
                    for (int i = 0; i < discovery.GrabberCount; ++i)
                    {
                        var info = discovery.EGrabbers[i];
                        Console.Write($"  [egrabber #{i}] {info.DeviceID}");
                        if (info.IsRemoteAvailable)
                            Console.Write($"  ({info.DeviceModelName})");
                        Console.WriteLine();
                    }

                    // cameras 목록 출력
                    for (int i = 0; i < discovery.CameraCount; ++i)
                    {
                        var cam = discovery.Cameras[i];
                        Console.WriteLine($"  [camera #{i}]");
                    }

                    return discovery.GrabberCount;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[{tag}] 실패: {e.Message}");
                return 0;
            }
        }

        // ── InspectFirstGrabber: 첫 번째 grabber 에 연결 → 파라미터 상세 출력 ────────
        private static void InspectFirstGrabber(string cti)
        {
            using (var gentl     = new EG.EGenTL(cti))
            using (var discovery = new EG.EGrabberDiscovery(gentl))
            {
                discovery.Discover();
                if (discovery.GrabberCount == 0) return;

                using (var grabber = new EG.EGrabber(discovery.EGrabbers[0]))
                {
                    Console.WriteLine();
                    Console.WriteLine("--- 첫번째 grabber 상세 ---");

                    // InterfaceModule / DeviceModule 파라미터
                    try { Console.WriteLine($"  Interface ID  : {grabber.Interface.Get<string>("InterfaceID")}"); }
                    catch { }
                    try { Console.WriteLine($"  Device ID     : {grabber.Device.Get<string>("DeviceID")}"); }
                    catch { }

                    // RemoteModule (카메라 본체) 파라미터
                    try
                    {
                        Console.WriteLine($"  Camera        : {grabber.Remote.Get<string>("DeviceVendorName")} "
                                          + grabber.Remote.Get<string>("DeviceModelName"));
                        Console.WriteLine($"  Width x Height: {grabber.Remote.Get<long>("Width")} x "
                                          + grabber.Remote.Get<long>("Height"));
                        Console.WriteLine($"  PixelFormat   : {grabber.Remote.Get<string>("PixelFormat")}");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"  Camera        : (응답 없음 - {e.Message})");
                    }
                }
            }
        }

        private static int Main()
        {
            try
            {
                Console.WriteLine("1) 실 Coaxlink 보드 탐지 시도...");
                int n = DiscoverWith(EG.CtiPath.Coaxlink, "Coaxlink");

                if (n == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("실 보드 없음. PlayLink 로 재시도합니다.");
                    n = DiscoverWith(EG.CtiPath.Playlink, "PlayLink");
                    if (n == 0)
                    {
                        Console.Error.WriteLine("어느 Producer 로도 grabber 를 찾지 못했습니다.");
                        return 1;
                    }
                    InspectFirstGrabber(EG.CtiPath.Playlink);
                }
                else
                {
                    InspectFirstGrabber(EG.CtiPath.Coaxlink);
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
