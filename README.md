# Euresys eGrabber C# Samples

Seven progressively-complex samples for learning the Euresys eGrabber .NET SDK on **.NET 6 / Windows x64**.

Each sample isolates one concept and builds on the previous, ending with a production-grade inspection pipeline pattern.

## Samples

| # | Project | Type | Pattern | What you learn |
|---|---------|------|---------|----------------|
| 01 | [HelloEgrabber](01_HelloEgrabber) | Console | Discovery | `InterfaceCount` / `GrabberCount` / `CameraCount` — telling physical boards apart from channels |
| 02 | [SimpleGrab](02_SimpleGrab) | Console | Polling | Synchronous N-frame grab via `ScopedBuffer` |
| 03 | [CallbackGrab](03_CallbackGrab) | Console | Callback | `RegisterEventCallback` + `ProcessEventsAsync` with named method (not lambda) for easier debugging |
| 04 | [MultiCamera](04_MultiCamera) | Console | Multi-camera | Per-camera class with instance-method callback — each camera owns its own state |
| 05 | [CameraParameters](05_CameraParameters) | Console | GenICam | `Remote.Get/Set<T>()` with `TrySet*` helper pattern |
| 06 | [DisplayWithOpenCV](06_DisplayWithOpenCV) | WinForms | Display | Real-time grab + display using polling + 30 Hz timer + bitmap pool + LockBits direct write. Applies all seven patterns from [HighSpeedDisplay_Guide.md](HighSpeedDisplay_Guide.md) |
| 07 | [InspectionPipeline](07_InspectionPipeline) | WinForms | Production | Callback + dedicated inspection thread + display worker thread + 30 Hz UI timer + live histogram. The pattern most production inspection systems use |

## Requirements

- **Windows 10 / 11 (x64)**
- **.NET 6 SDK**
- **Euresys eGrabber** installed (provides `EGrabber.NET.dll`)
- A Coaxlink / Grablink board, **or** the PlayLink simulator (auto-fallback — no hardware required to test)

## Build

```bash
dotnet build EuresysSamples.sln -c Release
```

The shared [Directory.Build.props](Directory.Build.props) auto-locates the SDK in priority order:

1. `EGrabberDir` MSBuild property
2. `EGRABBER_DIR` environment variable (set by the Euresys installer)
3. Default path `C:\Program Files\Euresys\eGrabber`

Override at build time if installed elsewhere:

```bash
dotnet build /p:EGrabberDir=D:\custom\eGrabber
```

## Run

Each sample produces an executable under `<project>/bin/Release/net6.0(-windows)/`.

- **Console samples (01–05)**: pause for a keypress before exiting (skipped in piped / CI contexts via `Console.IsInputRedirected`).
- **WinForms samples (06, 07)**: open a window with Start / Stop controls.

If no Coaxlink board is detected, samples automatically fall back to PlayLink — Euresys' software simulator that lets you exercise the full SDK without hardware.

## Documentation

- **[HighSpeedDisplay_Guide.md](HighSpeedDisplay_Guide.md)** — portable guide documenting seven display patterns (single-slot handoff, backpressure, bitmap pool, downscale, `LockBits`, `OptimizedDoubleBuffer`, 30 Hz timer) for porting to other camera SDK projects.

## Conventions

- **Console / WinForms output**: English.
- **Source comments**: Korean (preserved for documentation continuity).
- **Solution target**: `x64` Windows. Required by SDK DMA-buffer alignment.
- **Build artifacts** (`bin/`, `obj/`) ignored via [.gitignore](.gitignore).

## Project structure

```
EuresysSamples.sln
Directory.Build.props          # Shared MSBuild config (SDK paths, common props)
HighSpeedDisplay_Guide.md      # Display optimization patterns
01_HelloEgrabber/
02_SimpleGrab/
03_CallbackGrab/
04_MultiCamera/
05_CameraParameters/
06_DisplayWithOpenCV/          # Polling-based display
07_InspectionPipeline/         # Callback + worker threads + histogram
```

## Branches

- `main` — current samples (this README)
- `backup-old` — earlier history snapshot kept for reference

## License

Samples are provided as learning material. The Euresys eGrabber SDK itself is licensed separately by Euresys s.a.
