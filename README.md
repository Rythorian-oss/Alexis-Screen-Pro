# Alexis Screen Pro

**Cobalt Glassmorphism screen recording for Windows.** A full-screen H.264 capture studio that lives in the notification area, records system audio and microphone in sync, auto-stops on a timer, and saves every MP4 directly to `Desktop\ASP`.

Built with C# 13 on **.NET 9** and WPF, with a custom acrylic window host, a hand-built cobalt design system, and a background capture engine that never blocks the UI thread.

<p align="center">
  <img src="docs/widget-preview.png" alt="Alexis Screen Pro cobalt glass recording widget" width="380" />
  <img src=""C:\Users\Drukaz\Desktop\AlexisScreenPro-v1.0.0-source\docs\settings-preview.png"" alt="Alexis Screen Pro Studio Settings" width="560" />
</p>

---

## Highlights

- **Premium Cobalt Glass widget** — deep cobalt glass, white hairline edges, animated recording orb, live pulse, hover micro-interactions, and an acrylic DWM backdrop.
- **True background operation** — closing or minimizing the widget tucks it into the system tray; recording continues uninterrupted.
- **One-call tray workflow** — left-click the tray icon to open the widget, right-click for a styled menu with Start/Stop, Open recordings, Settings, and Exit.
- **Synchronized screen + audio capture** — full primary display, system loopback audio, and microphone, muxed into a single H.264/AAC MP4.
- **Automatic encoder fallback** — if the GPU H.264 encoder rejects a frame, the session transparently restarts on the Media Foundation software encoder.
- **Timed recordings** — choose an auto-stop duration from the widget or Studio Settings; the remaining time is shown live with a countdown, warning color, and progress bar.
- **Studio Settings** — frame rate, bitrate, cursor capture, hardware encoding, audio source/device selection, timer default, start-minimized, notifications, Windows startup, and storage.
- **One-instance activation** — launching the app again signals the running instance and brings the widget forward.
- **Self-excluding UI** — the widget and settings window are excluded from the app's own captures with `SetWindowDisplayAffinity`.
- **Crash-aware diagnostics** — every service logs to `Desktop\ASP\alexis-screen-pro.log`; early startup failures have a last-resort logger.
- **Zero network calls** — recordings and settings never leave the machine.

---

## Requirements

| Requirement | Notes |
|---|---|
| **Windows** | Windows 10 version 2004 (build 19041) or newer, or Windows 11 |
| **Architecture** | x64 only (ScreenRecorderLib ships an x64 mixed-mode capture engine) |
| **Runtime** | .NET 9 Desktop Runtime for running a build |
| **SDK** | .NET 9 SDK or newer for building (`LangVersion 13`) |
| **Native runtime** | [Microsoft Visual C++ 2015–2022 Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe) |
| **Media** | Windows Media Foundation (included in standard Windows; Windows N editions need the Media Feature Pack) |

> The application intentionally captures the **primary display**. System audio is captured through WASAPI loopback; microphone capture uses the selected Windows capture endpoint.

---

## Build & Run

Open `AlexisScreenPro.sln` in Visual Studio 2022/2026 and run the **x64** configuration, or use the CLI:

```powershell
dotnet restore .\AlexisScreenPro.sln

dotnet build .\AlexisScreenPro.sln -c Release

dotnet run --project .\src\AlexisScreenPro\AlexisScreenPro.csproj `
  -c Release -p:Platform=x64
```

The platform must be **x64** because the capture package injects its x64 native assembly.

### Publish

Framework-dependent (smallest output; .NET 9 Desktop Runtime required):

```powershell
dotnet publish .\src\AlexisScreenPro\AlexisScreenPro.csproj -c Release `
  -p:Platform=x64 -r win-x64 --self-contained false -o .\publish
```

Self-contained:

```powershell
dotnet publish .\src\AlexisScreenPro\AlexisScreenPro.csproj -c Release `
  -p:Platform=x64 -r win-x64 --self-contained true -o .\publish
```

---

## Using the App

1. Launch `AlexisScreenPro.exe`. The cobalt widget opens and the icon appears in the notification area.
2. Pick an auto-stop duration (optional), choose audio sources, and press the recording orb or **F9**.
3. Press the orb again to stop and save. When the timer reaches zero, the app stops and saves automatically.
4. Close or minimize the widget to keep recording in the background. Click the tray icon to reopen it.
5. Use **Studio settings** to select audio devices, frame rate, bitrate, hardware encoding, startup behavior, and notification preferences.
6. Every capture lands in `Desktop\ASP` with a timestamped filename; use **Open ASP** to open the folder.

### Tray menu

| Item | Action |
|---|---|
| Open dashboard | Reveals and activates the widget |
| Start/Stop recording | Controls capture without showing the dashboard |
| Open recordings folder | Opens `Desktop\ASP` in Explorer |
| Studio settings | Opens the settings studio |
| Exit Alexis Screen Pro | Stops/finalizes any active capture, flushes settings, and exits |

---

## Output & Data Locations

| Data | Location |
|---|---|
| Recordings | `%USERPROFILE%\Desktop\ASP\AlexisScreenPro_yyyy-MM-dd_HH-mm-ss.mp4` |
| Application log | `%USERPROFILE%\Desktop\ASP\alexis-screen-pro.log` |
| Capture engine log | `%USERPROFILE%\Desktop\ASP\capture-engine.log` |
| Settings | `%LOCALAPPDATA%\AlexisScreenPro\settings.json` |

The `ASP` folder is created on startup, before the tray icon is registered. Sub-16 KB encoder fragments are filtered from the recent list and cleaned up automatically.

---

## Architecture

```
AlexisScreenPro.sln
└── src/AlexisScreenPro
    ├── App.xaml / App.xaml.cs          Composition root, single-instance, crash guards, shutdown
    ├── Core/                           Constants, paths, disposal base, observable base, commands
    ├── Models/                         Settings, recording request/result/progress, events
    ├── Interfaces/                     Service contracts
    ├── Services/
    │   ├── ScreenRecorderService.cs    Capture engine, audio graph, auto-stop clock, HW→SW fallback
    │   ├── TrayService.cs              H.NotifyIcon tray icon, menu, state, balloons
    │   ├── FileManagementService.cs    Desktop\ASP, unique names, recent captures, shell opens
    │   ├── SettingsService.cs          Atomic JSON persistence
    │   ├── LogService.cs               Non-blocking queued text logger + rotation
    │   ├── StartupService.cs           HKCU Run registration
    │   └── SingleInstanceService.cs    Named mutex + activation event
    ├── ViewModels/                     Widget and Studio Settings presentation logic
    ├── Views/                          MainWindow, SettingsWindow, custom win32 glass host
    ├── Converters/                     Small, focused UI value converters
    ├── Helpers/                        DWM/acrylic/native interop
    └── Themes/                         Cobalt color system and the complete control library
```

### Capture pipeline

```
Display Duplication / Windows Graphics Capture
                    │
             H.264 Media Foundation encoder
                    │
WASAPI loopback ────┼──── AAC audio track ────► MP4 (Desktop\ASP)
Microphone     ─────┘
```

`ScreenRecorderService` owns the native `Recorder`. The UI never touches it directly; it receives immutable `RecordingProgress` objects through `IProgress<T>`.

### Threading contract

- `StartAsync`, `StopAsync`, device enumeration, and finalization are asynchronous.
- The 250 ms progress clock runs on a background task and reports via `IProgress<RecordingProgress>`.
- Hardware failure handling and recorder cleanup run on the thread pool.
- All service events are marshalled to the WPF dispatcher by the view model before touching UI properties.
- The log writer is a single background consumer, so disk I/O cannot stall capture or the UI.

### Lifetime contract

- Capture sources, audio sources, the recorder, cancellation sources, app log queue, tray icon, and view-model subscriptions are deterministically released.
- The standard `DisposableBase` pattern is used throughout, including `GC.SuppressFinalize` on normal disposal.
- Recorder events are detached before disposal to prevent late callbacks from touching retired state.
- `SettingsService` writes atomically via a temp file and rename.
- UI windows cancel close and hide to tray unless the application is genuinely shutting down.

See [AUDIT.md](AUDIT.md) for the full post-generation audit, findings, corrections, and validation record.

---

## Design System

| Token | Value |
|---|---|
| Night 950 | `#030A17` |
| Night 850 | `#081A33` |
| Cobalt 500 | `#256BFF` |
| Cyan 400 | `#3EDBFF` |
| Record | `#FF3B61` |
| Success | `#45E0B5` |
| Primary text | `#F4F8FF` |
| Secondary text | `#A9BEDB` |

The window host uses a layered WPF window plus:

- DWM dark chrome and rounded-corner preferences.
- `SetWindowCompositionAttribute` acrylic blur with a cobalt tint.
- White hairline glass borders (`#33FFFFFF`), layered translucent cobalt gradients, and soft depth shadows.
- Segoe UI Variable typography with Segoe MDL2 Assets for icons.
- Hover, press, pulse, toast, and open/close animations.

---

## Configuration

`settings.json` is human-readable and normalized on load. Invalid or obsolete values fall back safely.

```json
{
  "SchemaVersion": 1,
  "CaptureAudio": true,
  "CaptureSystemAudio": true,
  "CaptureMicrophone": false,
  "SystemAudioDeviceName": null,
  "MicrophoneDeviceName": null,
  "FrameRate": 30,
  "BitrateMbps": 8,
  "UseHardwareEncoding": true,
  "CaptureCursor": true,
  "AutoStopMinutes": 0,
  "StartMinimized": false,
  "ShowCompletionNotifications": true,
  "LaunchOnStartup": false
}
```

`launchOnStartup` uses the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key and works from the built `AlexisScreenPro.exe` (not from `dotnet.exe`).

---

## Troubleshooting

| Symptom | Resolution |
|---|---|
| `The hardware encoder failed; retrying…` | Expected on some GPUs/drivers. The software encoder takes over automatically. Disable hardware encoding in Settings to skip the first attempt. |
| No audio in a recording | Check Settings → Audio, confirm the device is online, and verify Windows microphone privacy permissions. System audio requires an active playback endpoint. |
| Capture won't start | Check the ASP log and `capture-engine.log`. Ensure the VC++ x64 redistributable and Media Foundation are available. |
| Tray icon not visible | Windows 11 may place new tray icons in the overflow flyout. Open the tray chevron and pin Alexis Screen Pro. |
| `Start with Windows must be enabled from the built exe` | Startup registration is intentionally disabled when the app is launched through `dotnet run`/`dotnet exec`. Use the published `AlexisScreenPro.exe`. |
| GPU/DRM content appears black | Protected content and some exclusive-fullscreen surfaces cannot be captured by Windows desktop duplication. |

---

## Known Limitations

- Primary monitor only; per-monitor/window-region selection is not part of v1.0.0.
- Pause/resume is not exposed in the UI.
- x64 Windows only.
- Capture compatibility depends on Windows Graphics Capture / Desktop Duplication and the installed Media Foundation H.264 encoders.

---

## Third-Party Components

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

- [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) 7.0.1 — MIT — Media Foundation screen/audio capture.
- [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon) 2.3.2 — MIT — native Windows notification-area integration.

---

**Alexis Screen Pro 1.0.0** · Cobalt Edition · Built for Windows.
