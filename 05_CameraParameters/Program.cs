// =============================================================================
//  예제 05 : Camera Parameters
// =============================================================================
//  목적
//    Remote(카메라) / Interface / Device 모듈의 파라미터 읽기·쓰기 방법을 익힌다.
//    노출/게인/픽셀포맷/ROI/트리거 모드를 변경한 뒤 짧게 그랩해 효과를 확인한다.
//
//  흐름 요약
//    1. DumpCurrent()   : 현재 설정 출력 (string → long → double 순으로 타입 자동 탐지)
//    2. ApplySettings() : Width/Height/PixelFormat 등 변경 (Start 전에만 가능)
//    3. DumpCurrent()   : 변경 후 설정 재출력
//    4. ShortGrab()     : 변경된 설정으로 N 장 동기 그랩
//
//  .NET SDK Get/Set 패턴
//    grabber.Remote.Get<string>("PixelFormat")         // string/enum 노드 읽기
//    grabber.Remote.Set<long>("Width", 640)            // integer 노드 쓰기
//    grabber.Remote.Set<double>("ExposureTime", 5000.0)// float 노드 쓰기
//    grabber.Remote.Set<string>("TriggerMode", "Off")  // enum 노드는 string 으로 전달
// =============================================================================

using System;
using EG = Euresys.EGrabber;

namespace CameraParameters
{
    internal static class Program
    {
        // ── TrySet 헬퍼: 노드가 없거나 읽기 전용인 경우 예외를 삼키고 결과를 출력 ──────
        private static void TrySetString(EG.EGrabber g, string name, string v)
        {
            try { g.Remote.Set<string>(name, v); Console.WriteLine($"  setString  {name} = \"{v}\"  OK"); }
            catch (Exception e) { Console.WriteLine($"  setString  {name} = \"{v}\"  FAIL ({e.Message})"); }
        }
        private static void TrySetInteger(EG.EGrabber g, string name, long v)
        {
            try { g.Remote.Set<long>(name, v); Console.WriteLine($"  setInteger {name} = {v}  OK"); }
            catch (Exception e) { Console.WriteLine($"  setInteger {name} = {v}  FAIL ({e.Message})"); }
        }
        private static void TrySetFloat(EG.EGrabber g, string name, double v)
        {
            try { g.Remote.Set<double>(name, v); Console.WriteLine($"  setFloat   {name} = {v}  OK"); }
            catch (Exception e) { Console.WriteLine($"  setFloat   {name} = {v}  FAIL ({e.Message})"); }
        }

        // ── Dump: RemoteModule 노드 하나를 string → long → double 순으로 읽기 시도 ────
        // GenAPI 노드는 대부분 string 으로도 읽히므로 string 을 먼저 시도한다.
        private static void Dump(EG.EGrabber g, string name)
        {
            try { Console.WriteLine($"  {name,-22} = {g.Remote.Get<string>(name)}"); return; }  catch {}
            try { Console.WriteLine($"  {name,-22} = {g.Remote.Get<long>(name)}"); return; } catch {}
            try { Console.WriteLine($"  {name,-22} = {g.Remote.Get<double>(name)}"); return; }   catch {}
            Console.WriteLine($"  {name,-22} = (n/a)");
        }

        // ── DumpCurrent: 카메라의 주요 파라미터를 현재 값으로 출력 ───────────────────
        private static void DumpCurrent(EG.EGrabber g)
        {
            Console.WriteLine();
            Console.WriteLine("--- Current camera settings ---");
            Dump(g, "DeviceVendorName");
            Dump(g, "DeviceModelName");
            Dump(g, "Width");
            Dump(g, "Height");
            Dump(g, "OffsetX");
            Dump(g, "OffsetY");
            Dump(g, "PixelFormat");
            Dump(g, "AcquisitionFrameRate");
            Dump(g, "ExposureTime");
            Dump(g, "Gain");
            Dump(g, "TriggerMode");
        }

        // ── ApplySettings: ROI / 픽셀포맷 / 노출 / 트리거 설정 변경 ──────────────────
        // ROI(Width·Height·Offset) 변경은 AcquisitionStart 전에만 허용하는 카메라가 많다.
        private static void ApplySettings(EG.EGrabber g)
        {
            Console.WriteLine();
            Console.WriteLine("--- Applying camera settings ---");

            TrySetString(g, "PixelFormat", "Mono8");
            TrySetInteger(g, "OffsetX", 0);
            TrySetInteger(g, "OffsetY", 0);

            // Width/Height: 기존 값을 읽어 그대로 다시 set (값 변경 없음).
            // 라인스캔/고정 ROI 카메라에서 임의 해상도를 거부하는 경우를 회피.
            try
            {
                long curWidth  = g.Remote.Get<long>("Width");
                long curHeight = g.Remote.Get<long>("Height");
                TrySetInteger(g, "Width",  curWidth);
                TrySetInteger(g, "Height", curHeight);
            }
            catch (Exception e)
            {
                Console.WriteLine($"  Width/Height read failed: {e.Message}");
            }

            TrySetFloat  (g, "ExposureTime", 5000.0);
            TrySetFloat  (g, "Gain",         1.0);
            TrySetString (g, "TriggerMode",  "Off");
        }

        // ── ShortGrab: 변경된 설정으로 n 장 동기 그랩 ──────────────────────────────
        // Start(n, true) 는 n 장 수신 후 자동으로 AcquisitionStop 및 Stop 을 호출한다.
        private static void ShortGrab(EG.EGrabber g, int n)
        {
            g.ReallocBuffers(4ul);
            g.Start((ulong)n, true);

            Console.WriteLine();
            Console.WriteLine($"--- Short grab of {n} frames with new settings ---");
            for (int i = 0; i < n; ++i)
            {
                using (var buf = new EG.ScopedBuffer(g, 1000ul))
                {
                    ulong fid  = buf.GetInfo<ulong>(EG.BUFFER_INFO_CMD.BUFFER_INFO_FRAMEID);
                    ulong size = buf.GetInfo<ulong>(EG.BUFFER_INFO_CMD.BUFFER_INFO_SIZE);
                    Console.WriteLine($"  frame {i + 1}  FID={fid}  size={size} B");
                }
            }
        }

        // ── OpenGenTL: Coaxlink → PlayLink 순서로 EGenTL 열기 ────────────────────────
        private static EG.EGenTL OpenGenTL(out string producer)
        {
            try
            {
                var g = new EG.EGenTL(EG.CtiPath.Coaxlink);
                using (var d = new EG.EGrabberDiscovery(g))
                {
                    d.Discover(false);
                    if (d.GrabberCount > 0) { producer = "Coaxlink"; return g; }
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
                    Console.WriteLine($"Producer : {producer}");
                    if (discovery.GrabberCount == 0)
                    {
                        Console.Error.WriteLine("No grabber available.");
                        return 1;
                    }

                    using (var grabber = new EG.EGrabber(discovery.EGrabbers[0]))
                    {
                        DumpCurrent(grabber);
                        ApplySettings(grabber);
                        DumpCurrent(grabber);
                        ShortGrab(grabber, 5);
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
