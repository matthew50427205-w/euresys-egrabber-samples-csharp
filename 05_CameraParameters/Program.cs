// =============================================================================
//  예제 05 : Camera Parameters  -  파라미터 다루기 (.NET 26.04 SDK)
// -----------------------------------------------------------------------------
//  목적
//    - 모듈 별 Get/Set 메서드(PascalCase) 사용법 익히기.
//    - 노출/게인/픽셀포맷/ROI/트리거 모드 변경 후 짧게 그랩.
//
//  C# 메서드 명명 규칙 (.NET 26.04)
//    Get/Set + Type + Module + Module(접미사)
//    예 :
//      GetStringRemoteModule("PixelFormat")
//      SetIntegerRemoteModule("Width", 640)
//      SetFloatRemoteModule("ExposureTime", 5000.0)
//      ExecuteCommandRemoteModule("AcquisitionStart")
// =============================================================================

using System;
using EG = Euresys.EGrabber;

namespace CameraParameters
{
    internal static class Program
    {
        // 노드가 없을 수도 있으므로 try/catch 로 안전하게
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

        // RemoteModule 의 노드 1개를 안전하게 dump
        private static void Dump(EG.EGrabber g, string name)
        {
            try { Console.WriteLine($"  {name,-22} = {g.Remote.Get<string>(name)}"); return; }  catch {}
            try { Console.WriteLine($"  {name,-22} = {g.Remote.Get<long>(name)}"); return; } catch {}
            try { Console.WriteLine($"  {name,-22} = {g.Remote.Get<double>(name)}"); return; }   catch {}
            Console.WriteLine($"  {name,-22} = (n/a)");
        }

        private static void DumpCurrent(EG.EGrabber g)
        {
            Console.WriteLine();
            Console.WriteLine("--- 현재 카메라 설정 ---");
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

        private static void ApplySettings(EG.EGrabber g)
        {
            Console.WriteLine();
            Console.WriteLine("--- 카메라 설정 변경 ---");

            TrySetString(g, "PixelFormat", "Mono8");
            TrySetInteger(g, "OffsetX", 0);
            TrySetInteger(g, "OffsetY", 0);
            TrySetInteger(g, "Width",   640);
            TrySetInteger(g, "Height",  480);
            TrySetFloat  (g, "ExposureTime", 5000.0);
            TrySetFloat  (g, "Gain",         1.0);
            TrySetString (g, "TriggerMode",  "Off");
        }

        private static void ShortGrab(EG.EGrabber g, int n)
        {
            g.ReallocBuffers(4ul);
            g.Start((ulong)n, true);

            Console.WriteLine();
            Console.WriteLine($"--- 변경된 설정으로 {n}장 그랩 ---");
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
                        Console.Error.WriteLine("사용 가능한 grabber 없음.");
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
                Console.Error.WriteLine($"오류: {e.Message}");
                return 1;
            }
            return 0;
        }
    }
}
