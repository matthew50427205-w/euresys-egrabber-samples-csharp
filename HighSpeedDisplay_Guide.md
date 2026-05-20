# 고속 그랩 → 안정적 디스플레이 적용 가이드

다른 C# / WinForms 영상 프로젝트에 그대로 이식할 수 있도록 정리한 노하우 모음.
원본 구현은 EuresysGrab(8채널 100G CoaXPress 동시 그랩) 프로젝트.

## 핵심 원칙 (한 줄)

> **그랩은 최대한 빨리, 디스플레이는 일정하게, 둘 사이는 최신 1프레임 슬롯으로만 연결한다. 못 그릴 프레임은 버린다.**

## 아키텍처 다이어그램

```
┌─────────────────┐    Interlocked.Exchange     ┌─────────────────┐
│ Grab Thread     │ ─────────────────────────▶ │ UI Timer 30 Hz  │
│ (per camera)    │   _latestBitmap (1-slot)    │ DisplayTimer    │
│                 │                              │                 │
│ • SDK buffer    │   ◀── ReturnToPool ──       │ • DrawImage     │
│ • BGR 변환      │       (used bitmap)         │ • Label 갱신    │
│ • Downscale     │                              │ • Sparkline     │
│ • LockBits write│                              │                 │
└─────────────────┘                              └─────────────────┘
        │                                                 ▲
        │            ConcurrentQueue<Bitmap>              │
        └──────────────▶  Bitmap Pool  ◀──────────────────┘
                          (3장 사전 할당)
```

## 적용 체크리스트

| # | 항목 | 권장 구현 |
|---|---|---|
| 1 | 디스플레이 주기 | UI Timer 30 Hz (33 ms) 고정, 카메라 fps와 분리 |
| 2 | 프레임 핸드오프 | 큐 사용 금지 → 단일 슬롯 + `Interlocked.Exchange` |
| 3 | 과부하 시 동작 | 큐잉 금지 → 드롭 (백프레셔) |
| 4 | 비트맵 관리 | 매번 new 금지 → 풀 + 반환, 사이즈 변경 시만 재구축 |
| 5 | 픽셀 변환 위치 | 그랩 스레드에서 끝내기, UI는 `DrawImage`만 |
| 6 | 메모리 복사 | `LockBits` + 외부 라이브러리(OpenCV/IPP)가 직접 쓰기 |
| 7 | 디스플레이 해상도 | 셀 크기에 맞춰 다운스케일, **통계는 원본 기준** |
| 8 | Paint 플래그 | `OptimizedDoubleBuffer` + Low 보간 + `SourceCopy` |
| 9 | 픽셀 폭 정렬 | `& ~1` 짝수 정렬 (스트라이드/SIMD 안전) |
| 10 | 스레드 마샬링 | 상태 변경은 `BeginInvoke`, 프레임은 폴링 |

---

## 1. 디스플레이 타이머는 고정 30 Hz, 그랩과 완전 비동기

### 왜?
- 카메라 fps와 사람 눈의 perceptual rate는 무관하다. 30 Hz면 충분하고 그 이상은 낭비.
- 그랩 콜백에서 직접 `Invalidate()`/`Invoke()`를 호출하면 UI 스레드가 폭주해서 **오히려 더 느려지고 깜빡인다**.
- "그랩 = 생산, 타이머 = 소비" 구조로 둘을 완전히 분리.

### 코드
```csharp
public class MainForm : Form
{
    private readonly System.Windows.Forms.Timer _displayTimer;

    public MainForm()
    {
        // … UI 구성 …
        _displayTimer = new System.Windows.Forms.Timer { Interval = 33 };  // ~30 fps
        _displayTimer.Tick += DisplayTimer_Tick;
        _displayTimer.Start();
    }

    private void DisplayTimer_Tick(object? sender, EventArgs e)
    {
        foreach (var node in _nodes)
        {
            var (bmp, stats) = node.TakePending();
            node.Control.UpdateFrame(bmp, stats);
        }
    }
}
```

### 주의
- `System.Threading.Timer`나 `System.Timers.Timer`가 아닌 **`System.Windows.Forms.Timer`** 를 써야 UI 스레드에서 Tick이 호출된다.
- 60 Hz가 필요하면 16 ms로 줄여도 되지만, 카메라 수 × 비트맵 페인트 비용을 먼저 측정할 것.

---

## 2. 단일 슬롯 + Interlocked 핸드오프 (락 없이 백프레셔)

### 왜?
- 락(`lock`/`Mutex`)은 그랩 스레드가 UI 스레드를 기다리게 만든다 → 그랩 jitter 발생.
- `ConcurrentQueue`도 무한히 쌓이면 메모리 폭증 + 화면 지연.
- **슬롯 1칸 + `Interlocked.Exchange`** 면 lock-free + bounded.

### 코드
```csharp
public sealed class CameraNode
{
    private Bitmap?    _latestBitmap;
    private NodeStats? _latestStats;

    // 그랩 스레드: 새 비트맵을 슬롯에 밀어넣고, 밀려난 이전 비트맵은 풀에 반환
    private void PublishFrame(Bitmap newBmp, NodeStats stats)
    {
        var old = Interlocked.Exchange(ref _latestBitmap, newBmp);
        if (old != null) ReturnToPool(old);
        Interlocked.Exchange(ref _latestStats, stats);
    }

    // UI 스레드 (디스플레이 타이머): bitmap은 consume, stats는 peek
    public (Bitmap? Bmp, NodeStats? Stats) TakePending() =>
        (Interlocked.Exchange(ref _latestBitmap, null),
         Volatile.Read(ref _latestStats));

    public double CurrentMbps => Volatile.Read(ref _latestStats)?.Mbps ?? 0;
}
```

### 미묘하지만 중요한 차이: consume vs peek
- **Bitmap은 consume** (꺼내면 null): 다음 Tick 전까지 그랩 스레드가 백프레셔를 알 수 있음.
- **Stats는 peek** (Volatile.Read만): 여러 곳에서 합산/표시할 수 있어야 하므로 consume하면 안 됨.
  - 예: 8대 합산 `TotalMbps`를 계산할 때 stats를 consume하면 한 카메라만 값을 보고 나머지는 0 → 총합이 들쭉날쭉.

---

## 3. 백프레셔: UI가 안 가져갔으면 변환 자체를 스킵

### 왜?
- 큐잉을 하면:
  1. 메모리 폭증
  2. 화면이 "과거"를 보여주는 지연(latency drift)
  3. 결국 한꺼번에 풀려서 깜빡임(overlap처럼 보이는 현상)
- "최신 1프레임"만 살리면 항상 실시간에 가깝고 부드럽다.

### 코드 (그랩 루프 안쪽)
```csharp
private void GrabLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        using var buf = AcquireFrame();          // SDK 호출
        int w = buf.Width, h = buf.Height;

        // 통계는 항상 갱신 (싸다)
        var stats = ComputeStats(buf);

        // ✨ 백프레셔: UI가 이전 프레임을 아직 안 가져갔으면 변환 스킵
        if (w > 0 && h > 0 && Volatile.Read(ref _latestBitmap) == null)
        {
            var bmp = ConvertAndDownscale(buf);   // 비싼 작업
            PublishFrame(bmp, stats);
        }
        else
        {
            // 비트맵은 못 만들지만 stats는 갱신 (대역폭 측정 손실 방지)
            Interlocked.Exchange(ref _latestStats, stats);
        }
    }
}
```

### 핵심
- 비싼 작업(Convert / Resize / Bitmap 복사)을 **조건부**로 실행.
- `Volatile.Read`로 슬롯이 비어 있는지 먼저 체크 → 비어 있을 때만 비트맵 생성.
- 스킵되더라도 통계는 갱신해서 FPS/MB·s 표시는 정확하게 유지.

---

## 4. Bitmap 풀 — GC를 그랩 루프 밖으로

### 왜?
- 4K BGR24 비트맵 = 약 24 MB. 8대 × 100 fps × 24 MB = **초당 19 GB 할당**.
- LOH(Large Object Heap) 단편화 → Gen2 GC → 프레임 stutter.
- 비트맵을 3장만 미리 만들어 두고 돌려쓰면 할당이 0에 수렴.

### 코드
```csharp
public sealed class BitmapPool
{
    private readonly ConcurrentQueue<Bitmap> _free = new();
    private volatile int _w, _h;

    public void Init(int w, int h)
    {
        while (_free.TryDequeue(out var b)) b.Dispose();
        _w = w; _h = h;
        for (int i = 0; i < 3; i++)
            _free.Enqueue(new Bitmap(w, h, PixelFormat.Format24bppRgb));
    }

    public Bitmap Rent(int w, int h)
    {
        if (w != _w || h != _h) Init(w, h);                // 사이즈 바뀌면 풀 재구축
        return _free.TryDequeue(out var b)
            ? b
            : new Bitmap(w, h, PixelFormat.Format24bppRgb); // 풀 비어 있으면 일회용 생성
    }

    public void Return(Bitmap bmp)
    {
        if (bmp.Width == _w && bmp.Height == _h) _free.Enqueue(bmp);
        else bmp.Dispose();   // 사이즈 안 맞으면 그냥 버림
    }

    public void Drain()
    {
        while (_free.TryDequeue(out var b)) b.Dispose();
    }
}
```

### UI 측에서 반환하는 패턴
```csharp
// UserControl 안
public void UpdateFrame(Bitmap? bmp, NodeStats? stats)
{
    if (bmp != null)
    {
        var old = _displayBitmap;
        _displayBitmap = bmp;
        _videoPanel.SetImage(bmp);
        if (old != null) _pool.Return(old);   // ✨ 이전 비트맵을 풀에 반환
    }
    // …
}
```

### 주의
- 카메라 해상도가 바뀌면(`AcquisitionFrameRate` 외 ROI 변경 등) 풀의 width/height가 안 맞음 → 자동 재구축.
- 풀이 비었을 때는 일회용 비트맵 생성으로 fallback (그랩이 잠시 빨라진 경우).

---

## 5. 다운스케일 — 통계는 원본, 디스플레이는 화면 크기에 맞춰

### 왜?
- 4K(3840×2160) 카메라라도 화면의 카메라 셀은 ~300~400 px.
- GDI+가 매 프레임 4K → 400 px로 리샘플하면 페인트 비용 폭증 → 다른 카메라가 못 그려져서 잔상/오버랩처럼 보임.
- **미리 다운스케일된 비트맵을 UI에 전달**하면 GDI+는 1:1에 가깝게 그리기만 하면 됨.

### 코드
```csharp
private const int MaxDisplayDim = 960;   // 화면 셀이 충분히 작으면 더 줄여도 OK

private static (int w, int h) ComputeDisplaySize(int srcW, int srcH)
{
    int m = Math.Max(srcW, srcH);
    if (m <= MaxDisplayDim) return (srcW, srcH);
    int dw = (int)((long)srcW * MaxDisplayDim / m) & ~1;   // 짝수 정렬
    int dh = (int)((long)srcH * MaxDisplayDim / m) & ~1;
    return (Math.Max(dw, 2), Math.Max(dh, 2));
}

// 그랩 루프
var (dw, dh) = ComputeDisplaySize(srcW, srcH);
var bmp = _pool.Rent(dw, dh);

using var src = new Mat(srcH, srcW, MatType.CV_8UC3, sdkBuffer);
using var dst = new Mat(dh, dw, MatType.CV_8UC3, bmpData.Scan0, bmpData.Stride);
if (dw == srcW && dh == srcH)
    src.CopyTo(dst);
else
    Cv2.Resize(src, dst, new Size(dw, dh), 0, 0, InterpolationFlags.Area);
```

### 핵심
- **통계는 원본 해상도 기준으로 계산**: `mbps = srcW * srcH * (bpp/8) * fps / 1_000_000`.
  - 다운스케일된 비트맵 크기로 계산하면 실제 SDK 전송량과 달라져서 대역폭 측정이 무의미해짐.
- 리사이즈는 **`INTER_AREA`** — 축소에는 INTER_AREA가 화질·속도 모두 정답. INTER_LINEAR은 확대용.
- `& ~1`로 짝수 정렬 — OpenCV/SIMD 안전, 일부 디코더가 홀수 stride에서 충돌.

---

## 6. LockBits + OpenCV Mat을 비트맵 메모리에 직접 쓰기

### 왜?
- `Bitmap.SetPixel` = 픽셀당 함수 호출 → 4K 한 장에 수십 ms.
- 중간 `byte[]` 버퍼 거치면 메모리 복사 2회.
- **LockBits로 비트맵 메모리 포인터를 얻은 뒤 OpenCV Mat을 그 위에 만들면 SIMD 한 번에 끝**.

### 코드
```csharp
var (dw, dh) = ComputeDisplaySize(srcW, srcH);
var bmp = _pool.Rent(dw, dh);

var bmpData = bmp.LockBits(
    new Rectangle(0, 0, dw, dh),
    ImageLockMode.WriteOnly,
    PixelFormat.Format24bppRgb);
try
{
    // SDK 출력 픽셀 (BGR8 변환 완료)
    using var src = new Mat(srcH, srcW, MatType.CV_8UC3, sdkConvertedPtr);

    // 비트맵 메모리 위에 Mat을 직접 매핑 (복사 없음)
    using var dst = new Mat(dh, dw, MatType.CV_8UC3, bmpData.Scan0, bmpData.Stride);

    if (dw == srcW && dh == srcH)
        src.CopyTo(dst);                                       // memcpy 1회
    else
        Cv2.Resize(src, dst, new Size(dw, dh), 0, 0,
                   InterpolationFlags.Area);                   // SIMD 리사이즈
}
finally { bmp.UnlockBits(bmpData); }   // ✨ finally 필수
```

### 주의
- `PixelFormat.Format24bppRgb`는 실제 메모리 순서가 **BGR**. SDK 출력도 BGR이어야 함 (또는 RGB↔BGR 변환).
- `bmpData.Stride`는 width × 3과 다를 수 있다 (4바이트 정렬). OpenCV `Mat` 생성 시 반드시 stride 전달.
- `UnlockBits`는 `finally`에서 — 빠뜨리면 비트맵이 영구 lock 상태.

---

## 7. Paint 측 최적화 — OptimizedDoubleBuffer + Low-quality 보간

### 왜?
- WinForms 깜빡임의 본질: **WM_ERASEBKGND** (배경 검정 칠) → **WM_PAINT** (실제 그림). 두 단계 사이의 짧은 검정 프레임이 보임.
- `OptimizedDoubleBuffer` 플래그가 자동 더블버퍼링을 활성화해서 두 단계를 한 번에 합침.
- 라이브 영상은 30 Hz로 새로 칠해지므로 보간 품질은 Low로 충분. High는 한 셀당 ms 단위로 비용 증가.

### 코드 (영상 표시용 Control)
```csharp
public sealed class VideoPanel : Control
{
    private Bitmap? _image;

    public VideoPanel()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |        // ✨ ERASEBKGND 무력화
            ControlStyles.OptimizedDoubleBuffer,         // ✨ 자동 더블버퍼
            true);
        BackColor = Color.Black;
    }

    public void SetImage(Bitmap? bmp) { _image = bmp; Refresh(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var img = _image;
        if (img == null) { g.Clear(BackColor); return; }

        g.CompositingMode   = CompositingMode.SourceCopy;     // ✨ 알파 블렌딩 스킵
        g.InterpolationMode = InterpolationMode.Low;          // ✨ 빠른 보간
        g.PixelOffsetMode   = PixelOffsetMode.HighSpeed;
        g.Clear(BackColor);

        // Fit 모드: 비율 유지하며 화면에 꽉 채움
        float scale = Math.Min((float)ClientSize.Width  / img.Width,
                               (float)ClientSize.Height / img.Height);
        int dw = (int)(img.Width  * scale);
        int dh = (int)(img.Height * scale);
        g.DrawImage(img, (ClientSize.Width - dw) / 2, (ClientSize.Height - dh) / 2, dw, dh);
    }

    // OnPaintBackground 오버라이드해서 비워두는 것도 깜빡임 방지에 도움
    protected override void OnPaintBackground(PaintEventArgs pevent) { /* skip */ }
}
```

### 추가 팁
- Form 자체에도 `SetStyle(ControlStyles.OptimizedDoubleBuffer, true)`를 켜면 전체 깜빡임이 줄어듦.
- `TableLayoutPanel` 같은 컨테이너는 더블버퍼가 꺼져 있을 수 있다 — 리플렉션으로 `DoubleBuffered` 강제 활성화 가능:
  ```csharp
  typeof(Control).GetProperty("DoubleBuffered",
      BindingFlags.Instance | BindingFlags.NonPublic)
      ?.SetValue(panel, true);
  ```

---

## 8. 부가 패턴

### 상태 변경 콜백은 BeginInvoke
```csharp
public event Action<string, bool>? StatusChanged;   // 그랩 스레드에서 발생

// UI 측 핸들러
private void OnStatusChanged(string msg, bool isError)
{
    if (InvokeRequired) { BeginInvoke(() => OnStatusChanged(msg, isError)); return; }
    _statusLabel.Text = msg;
}
```
- 프레임은 폴링(타이머)으로, **상태 변경 같은 이벤트만 마샬링**. 둘을 섞지 말 것.

### 카메라당 그랩 태스크 1개 + CancellationToken
```csharp
public void Start()
{
    _cts = new CancellationTokenSource();
    _grabTask = Task.Run(() => GrabLoop(_cts.Token));
}

public async Task StopAsync()
{
    _cts?.Cancel();
    if (_grabTask != null)
        try { await _grabTask; } catch { }
}
```
- `Thread`보다 `Task.Run` — 스레드풀 사용으로 8대 동시 시작/정지가 빠름.
- Form Closing에서 모든 노드의 `StopAsync`를 `Task.WhenAll`로 대기 후 dispose.

### SDK 버퍼는 using으로 즉시 반환
```csharp
using var buf = new SdkBuffer(grabber, timeout);   // ScopedBuffer 패턴
// … buf 사용 …
// using 끝나면 SDK에 즉시 반환 → SDK 버퍼풀 고갈 방지
```

---

## 트러블슈팅

| 증상 | 원인 / 해결 |
|---|---|
| 화면이 깜빡인다 | `OptimizedDoubleBuffer` 미설정 / `OnPaintBackground` 미오버라이드 |
| 카메라 1대만 잘 나오고 나머지는 멈춤 | 그랩 콜백에서 직접 `Invoke()` 호출 중 → UI 스레드 폭주. 폴링 모델로 변경 |
| 메모리 사용량이 계속 증가 | 비트맵 풀에 반환 누락 / 큐잉 백프레셔 부재 |
| FPS는 높은데 화면이 끊겨 보임 | 다운스케일 미적용으로 GDI+ 페인트가 병목 |
| 화면 지연(latency drift) | 큐 사용 중 → 단일 슬롯으로 변경, 드롭 허용 |
| Bitmap is locked 예외 | `LockBits` 후 `UnlockBits` 누락 — `finally`로 보호 |
| 총 대역폭이 0으로 깜빡임 | stats를 consume하고 있음 → peek(`Volatile.Read`)으로 변경 |
| 일부 프레임이 찢어져 보임 | bmpData.Stride 무시하고 width*bpp로 Mat 만듦 — stride 사용 |

---

## 최소 이식 템플릿 (단일 카메라용)

기존 프로젝트에 빠르게 붙이려면 아래 3개 클래스부터 시작:

1. **`BitmapPool`** — Section 4 코드 그대로.
2. **`FrameProducer`** — Section 2(슬롯) + Section 3(백프레셔) + Section 5(다운스케일) + Section 6(LockBits) 결합.
3. **`VideoPanel : Control`** — Section 7 그대로.

그리고 Form에:
- `System.Windows.Forms.Timer { Interval = 33 }` 추가
- Tick에서 `producer.TakePending()` → `videoPanel.SetImage(bmp)` → `pool.Return(oldBmp)`

이 4단계만 지키면 다른 SDK(Basler Pylon, IDS peak, MVS, FLIR Spinnaker 등)에서도 동일한 안정성을 얻을 수 있다.
