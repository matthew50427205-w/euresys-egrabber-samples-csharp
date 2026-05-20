// =============================================================================
//  예제 03 : Callback Grab
// =============================================================================
//  목적
//    새 프레임이 들어올 때마다 콜백이 자동 호출되도록 설정한다.
//    카메라/보드 초기 설정은 외부 .js 파일(RunScript)으로 한 번에 적용한다.
//
//  C# .NET SDK 콜백 패턴 (C++ 과의 차이 포함)
//    1. RegisterEventCallback<NewBufferData>((g, data) => {...})
//       이벤트 도착 시 호출할 람다를 등록.
//    2. EnableEvent(EventType.NewBufferData)
//       Start() 전에 이벤트 수신을 명시적으로 활성화해야 한다.
//    3. ProcessEventsAsync(EventType.NewBufferData, cts.Token)
//       별도 Task 에서 ProcessEvent 를 반복 호출 → 콜백이 실제로 실행됨.
//       C++ 의 CallbackSingleThread 는 내부 스레드가 자동으로 돌지만,
//       C# 은 이 Task 를 직접 시작해야 한다.
//    4. cts.Cancel() + task.Wait() 로 이벤트 루프 종료.
//
//  주의
//    - ScopedBuffer(g, data) : 콜백 인자(NewBufferData)로 버퍼를 RAII 래핑.
//      using 블록 종료 시 버퍼가 자동으로 큐에 반환된다.
//    - 콜백은 별도 스레드에서 실행되므로 공유 변수는 Interlocked 으로 보호.
// =============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using EG = Euresys.EGrabber;

namespace CallbackGrab
{
    internal static class Program
    {
        private const int BufferCount  = 4;
        private const int FramesToGrab = 20;

        // 콜백과 메인 루프가 공유하는 수신 카운터.
        // 람다 캡처 대신 클래스 필드로 두면 별도 메서드(OnNewBuffer)에서도 접근 가능.
        // Interlocked 로만 읽고/쓰기 — 두 스레드에서 동시 접근하므로 race condition 방지.
        private static long received = 0;

        // ── OpenGenTL: Coaxlink → PlayLink 순서로 EGenTL 열기 ────────────────────────
        // producer: 실제 사용된 Producer 이름 (로그/디버그용 out 파라미터).
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
                            // 1) RunScript : 카메라/보드 초기 설정 파일 적용.
                            //    PlayLink 환경에서는 일부 노드가 없어 예외가 날 수 있으므로
                            //    try/catch 로 무시하고 기본 설정으로 계속 진행.
                            const string script = "config-rg.js";
                            try
                            {
                                grabber.RunScript(script);
                                Console.WriteLine($"RunScript OK : {script}");
                            }
                            catch (Exception e)
                            {
                                Console.Error.WriteLine($"RunScript failed ({script}): {e.Message}");
                                Console.Error.WriteLine("  -> Falling back to default settings.");
                            }

                            received = 0;   // 매 실행마다 카운터 리셋

                            // 2) 콜백 등록 : 프레임 1장이 도착할 때마다 OnNewBuffer 가 호출됨.
                            //    람다 대신 별도 메서드를 등록 — 단위 테스트/디버깅/재사용 용이.
                            grabber.RegisterEventCallback<EG.NewBufferData>(OnNewBuffer);

                            // 3) 수신할 이벤트 종류 활성화.
                            //    Start() 호출 전에 명시적으로 지정해야 ProcessEventsAsync 가 동작한다.
                            grabber.EnableEvent(EG.EventType.NewBufferData);

                            // 4) DMA 버퍼 확보 후 무한 그랩 시작.
                            //    Start() : 인자 생략 = 무한 그랩 + AcquisitionStart 자동 실행
                            //    (유레시스 공식 권장. ulong.MaxValue 와 동일하지만 의미 명확)
                            grabber.ReallocBuffers((ulong)BufferCount);
                            grabber.Start();

                            // 5) 이벤트 루프 시작.
                            //    ProcessEventsAsync 가 내부적으로 ProcessEvent 를 반복 호출하면서
                            //    등록된 콜백을 실제로 실행해 준다.
                            //    CancellationToken 으로 종료 제어.
                            var cts  = new CancellationTokenSource();
                            var task = grabber.ProcessEventsAsync(EG.EventType.NewBufferData, cts.Token);

                            Console.WriteLine();
                            Console.WriteLine($"Waiting for {FramesToGrab} frames...");

                            // 6) N 장 수신 완료를 기다린다.
                            while (Interlocked.Read(ref received) < FramesToGrab)
                                Thread.Sleep(10);

                            // 7) 이벤트 루프 중단 후 그랩 정지.
                            cts.Cancel();
                            task.Wait();
                            grabber.Stop();

                            Console.WriteLine();
                            Console.WriteLine($"Done. Received {received} frames.");
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

        // ── 새 프레임 콜백 ────────────────────────────────────────────────────────
        // grabber.RegisterEventCallback<NewBufferData> 로 등록되어, 새 프레임이 도착할
        // 때마다 SDK 가 이벤트 스레드(ProcessEventsAsync 가 만든 Task) 에서 호출한다.
        //
        // 주의: 이 메서드는 메인 스레드가 아니라 별도 스레드에서 실행된다.
        //       공유 카운터 received 는 반드시 Interlocked 로만 접근.
        private static void OnNewBuffer(EG.EGrabber g, EG.NewBufferData data)
        {
            using (var buf = new EG.ScopedBuffer(g, data))
            {
                ulong fid  = buf.GetInfo<ulong>(EG.BUFFER_INFO_CMD.BUFFER_INFO_FRAMEID);
                ulong ts   = buf.GetInfo<ulong>(EG.BUFFER_INFO_CMD.BUFFER_INFO_TIMESTAMP);
                ulong size = buf.GetInfo<ulong>(EG.BUFFER_INFO_CMD.BUFFER_INFO_SIZE);

                long n = Interlocked.Increment(ref received);
                Console.WriteLine(
                    $"[callback] frame {n,3} | FID={fid,5} | size={size} B | ts={ts} us");
            }
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
