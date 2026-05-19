// =============================================================================
//  예제 02 : Simple Grab  -  가장 단순한 동기식 그랩
// -----------------------------------------------------------------------------
//  목적
//    - 콜백 없이 N 장의 영상을 차례로 받아오는 가장 기본 패턴.
//    - 보드 없으면 PlayLink 로 자동 전환.
//
//  핵심
//    - ReallocBuffers(N)   : DMA 버퍼 N 개 미리 확보
//    - Start(M, false)     : M 장 받으면 자동 stop
//    - ScopedBuffer        : using 으로 자동 push/pop
// =============================================================================

using System;
using EG = Euresys.EGrabber;

namespace SimpleGrab
{
    internal static class Program
    {
        private const int FramesToGrab = 10;
        private const int BufferCount  = 4;

        // 실 보드 → PlayLink 순서로 시도
        private static EG.EGenTL OpenGenTL(out string producer)
        {
            try
            {
                var g = new EG.EGenTL(EG.CtiPath.Coaxlink);
                using (var d = new EG.EGrabberDiscovery(g))
                {
                    d.Discover(false);  // grabber-oriented 만 빠르게 스캔
                    if (d.GrabberCount > 0)
                    {
                        producer = "Coaxlink";
                        return g;
                    }
                }
                g.Dispose();
            }
            catch { /* fallthrough */ }

            producer = "PlayLink";
            return new EG.EGenTL(EG.CtiPath.Playlink);
        }

        private static int Main()
        {
            try
            {
                using (var gentl = OpenGenTL(out string producer))
                {
                    Console.WriteLine($"Producer : {producer}");

                    using (var discovery = new EG.EGrabberDiscovery(gentl))
                    {
                        discovery.Discover();
                        if (discovery.GrabberCount == 0)
                        {
                            Console.Error.WriteLine("사용 가능한 grabber 없음.");
                            return 1;
                        }

                        using (var grabber = new EG.EGrabber(discovery.EGrabbers[0]))
                        {
                            Console.Write("Grabber 열림 - ");
                            try
                            {
                                Console.WriteLine($"{grabber.Remote.Get<string>("DeviceModelName")} "
                                    + $"({grabber.Remote.Get<long>("Width")} x "
                                    + $"{grabber.Remote.Get<long>("Height")})");
                            }
                            catch
                            {
                                Console.WriteLine("(카메라 정보 응답 없음)");
                            }

                            // 1) DMA 버퍼 할당
                            grabber.ReallocBuffers((ulong)BufferCount);

                            // 2) Start(frameCount, controlRemoteDevice)
                            //    controlRemoteDevice=true : AcquisitionStart 명령 자동 실행.
                            //    실 카메라·PlayLink 모두 true 가 안전하다.
                            grabber.Start((ulong)FramesToGrab, true);

                            Console.WriteLine($"그랩 시작 ({FramesToGrab} frames 요청)");
                            Console.WriteLine();

                            // 3) N장 pop
                            for (int i = 0; i < FramesToGrab; ++i)
                            {
                                // 1000 ms 타임아웃으로 영원 대기 방지
                                using (var buf = new EG.ScopedBuffer(grabber, 1000ul))
                                {
                                    IntPtr base_ = buf.GetInfo<IntPtr>(EG.BUFFER_INFO_CMD.BUFFER_INFO_BASE);
                                    ulong  ts    = buf.GetInfo<ulong> (EG.BUFFER_INFO_CMD.BUFFER_INFO_TIMESTAMP);
                                    ulong  fid   = buf.GetInfo<ulong> (EG.BUFFER_INFO_CMD.BUFFER_INFO_FRAMEID);
                                    ulong  size  = buf.GetInfo<ulong> (EG.BUFFER_INFO_CMD.BUFFER_INFO_SIZE);

                                    Console.WriteLine(
                                        $"Frame {i + 1,3} | FID={fid,5} | size={size,8} B | ts={ts} us | base=0x{base_.ToInt64():X}");

                                    // === 영상 처리 ===
                                    // base_ : 원본 픽셀 데이터 시작 포인터
                                    // size  : 한 프레임 바이트 수
                                    // managed byte[] 로 복사하려면:
                                    //   byte[] pixels = new byte[(int)size];
                                    //   System.Runtime.InteropServices.Marshal.Copy(base_, pixels, 0, (int)size);
                                }
                            }
                            Console.WriteLine();
                            Console.WriteLine("그랩 완료");
                        }
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
