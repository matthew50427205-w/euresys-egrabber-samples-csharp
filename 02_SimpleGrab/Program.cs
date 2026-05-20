// =============================================================================
//  예제 02 : Simple Grab
// =============================================================================
//  목적
//    콜백 없이 N 장의 영상을 동기식으로 차례로 받아오는 가장 기본 패턴.
//    보드 없으면 PlayLink 로 자동 전환.
//
//  흐름 요약
//    1. EGenTL(Coaxlink) → grabber 없으면 EGenTL(PlayLink) 로 재시도
//    2. ReallocBuffers(N) : DMA 버퍼 미리 확보 (Start 전에 반드시 호출)
//    3. Start(M, true)    : M 장 수신 후 자동 Stop.
//                          controlRemoteDevice=true 로 AcquisitionStart 자동 전송.
//    4. ScopedBuffer 루프 : using 블록 종료 시 버퍼가 자동으로 큐에 반환됨.
// =============================================================================

using System;
using EG = Euresys.EGrabber;

namespace SimpleGrab
{
    internal static class Program
    {
        private const int FramesToGrab = 10;
        private const int BufferCount  = 4;

        // ── OpenGenTL: Coaxlink → PlayLink 순서로 EGenTL 열기 ────────────────────────
        // Discover(false): grabber-oriented 스캔만 수행 (카메라 열거 생략, 속도 우선).
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
                            Console.Error.WriteLine("No grabber available.");
                            return 1;
                        }

                        using (var grabber = new EG.EGrabber(discovery.EGrabbers[0]))
                        {
                            Console.Write("Grabber opened - ");
                            try
                            {
                                Console.WriteLine($"{grabber.Remote.Get<string>("DeviceModelName")} "
                                    + $"({grabber.Remote.Get<long>("Width")} x "
                                    + $"{grabber.Remote.Get<long>("Height")})");
                            }
                            catch
                            {
                                Console.WriteLine("(no camera info)");
                            }

                            // 1) DMA 버퍼 할당
                            grabber.ReallocBuffers((ulong)BufferCount);

                            // 2) Start(frameCount, controlRemoteDevice)
                            //    controlRemoteDevice=true : AcquisitionStart 명령 자동 실행.
                            //    실 카메라·PlayLink 모두 true 가 안전하다.
                            grabber.Start((ulong)FramesToGrab, true);

                            Console.WriteLine($"Grab started (requesting {FramesToGrab} frames)");
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
                            Console.WriteLine("Grab complete");
                        }
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
