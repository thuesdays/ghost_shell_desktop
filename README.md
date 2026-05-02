# Ghost Shell — Desktop (C# native rewrite)

Native Windows desktop app for the Ghost Shell antidetect-browser
control plane. Pure C# / .NET 8 / WPF. No Python subprocess, no
WebView2, no web wrappers — fully native UI written in XAML with a
custom dense-IDE theme inspired by VS Code / Linear / GitHub Dark.

This is a **clean rewrite**. The legacy Flask + browser dashboard
(see `F:\projects\ghost_shell_browser\`) stays as a reference for
behavior, schema, and UX patterns — we read from it, we don't depend
on it.

## Status — Phase 3 (real browser pipeline)

- [x] Solution + 4 projects scaffolded
- [x] Core domain models (Profile, Run, Proxy, DeviceTemplate, ProxyHealthEvent…)
- [x] SQLite persistence + migrations V1-V4
- [x] WPF window, dense theme, custom chrome, app icon
- [x] Profiles + Proxy + Runs pages with full CRUD
- [x] Profile editor: name, device template (~25 presets), language, proxy, enrich
- [x] Proxy editor: URL paste, rotation block, default flag, notes, auto-test
- [x] Bulk import (8 paste formats: URL / host:port / user:pass@... / 4-part / IPv6)
- [x] Test all (concurrent, configurable parallelism)
- [x] Health timeline widget per proxy
- [x] Notifications drawer + InfoBanner / Pill style system
- [x] Serilog: file rotation, console, debug, global crash handlers
- [x] **IChromiumLocator** — finds patched Chromium build on disk
- [x] **BrowserLauncher** — Selenium 4 + ChromeDriver, per-profile user-data-dir
- [x] **RealProfileRunner** — replaces stub, manages session lifecycle + watchdog
- [x] Settings page shows browser engine status (success / error banner)
- [x] Action runner — full port (Phase 12-14)
- [x] Vault — encrypted SQLite + master password (Phase 24-26)
- [x] Browser extensions library (Phase 27)
- [x] Traffic / Notifications / Settings / Resource blocking (Phases 28-30)
- [x] Overview redesign + external fingerprint testers (Phases 31-33)
- [x] Advertisement section: Domains + Competitors + Ad density (Phase 34)
- [x] **Inno Setup installer + dual-build pipeline** (Phase 35)
- [ ] Auto-update — Velopack (deferred)

## Logs

Every run writes to `%LocalAppData%\GhostShell\logs\app-YYYY-MM-DD.log`.
Daily rotation, 14-day retention, 50 MB cap per file. The Settings
page has an "Open logs folder" button for quick triage. Full pipeline
description in `docs/architecture.md`.

## Build

Requires .NET 8 SDK. From the repo root:

```powershell
dotnet build GhostShell.sln
dotnet run --project src/GhostShell.App
```

In Visual Studio 2022 17.8+: just open `GhostShell.sln` and F5.

## Producing an installer

The Inno Setup pipeline lives in a sibling private repo, `ghost_shell_browser_inno`. From there:

```bat
cd F:\projects\ghost_shell_browser_inno
build.bat                  REM builds BOTH installers (web + desktop)
build.bat --desktop-only   REM just the desktop one
```

The desktop pipeline (`build_desktop.bat` in the installer repo):

1. Runs `sync_chromium.bat` to refresh `..\ghost_shell_browser\chrome_win64\` from the patched Chromium build dir.
2. Runs `dotnet publish src/GhostShell.App/GhostShell.App.csproj -c Release -r win-x64 --self-contained true` against this repo. Output lands in `publish\desktop\` (gitignored).
3. Bundles both into `output\GhostShellDesktopSetup.exe` (~250-300 MB total).

What the installer ships:

- `GhostShell.exe` and the .NET 8 self-contained runtime (no separate runtime install needed by the user)
- The whole `Assets\` tree (icons, fonts, themes)
- `chrome_win64\` — the patched Chromium binary that `ChromiumLocator` finds at startup
- A stop-app helper (`installer-tools\stop_desktop.ps1`) used during update / uninstall

Default install location: `%LocalAppData%\GhostShellDesktop\`.
User data (DB, profiles, vault, logs): `%LocalAppData%\GhostShell\` (controlled by `AppPaths.DataDir`).

The installer offers three modes when an existing install is detected: **Update** (replace program files, keep data), **Repair** (re-extract same version, keep data), and **Reinstall fresh** (back up `ghost_shell.db` to `%LocalAppData%\GhostShellDesktop\backup\` then wipe profiles/DB/vault before laying down the new build).

## Solution layout

```
GhostShell.sln
├── src/
│   ├── GhostShell.Core/        Domain models + service interfaces.
│   │                            No third-party deps. Pure POCO.
│   ├── GhostShell.Data/        SQLite + Dapper. Repositories +
│   │                            migration runner + service implementations.
│   ├── GhostShell.Runtime/     Browser orchestration. Stub for now;
│   │                            will own runner / scheduler / process_reaper.
│   └── GhostShell.App/         WPF UI. Custom title bar, sidebar,
│                                navigation, six page stubs.
└── docs/
    └── architecture.md         How the layers fit together.
```

## Design philosophy

- **Single process.** No HTTP, no IPC, no subprocess. Backend is a
  library reference from the UI project. ViewModels call services
  directly through DI.
- **Dense IDE aesthetic.** Default font 12px, secondary 11px,
  monospace for tech values. Tight padding (4–8px). Sharp corners
  (≤4px radius). Slate background, indigo accent, semantic colors
  for ok / warn / err.
- **MVVM lite.** CommunityToolkit.Mvvm gives `ObservableObject` and
  `[RelayCommand]`; everything else is hand-rolled to keep
  dependencies minimal.
- **No global state.** Services registered via Microsoft.Extensions.DependencyInjection,
  resolved through constructor injection.

## Where the legacy project lives

`F:\projects\ghost_shell_browser\` — original Python + Flask
dashboard. Read-only reference. We DO NOT modify it from here.

When we need to know "how does the legacy dashboard render the runs
filter?" or "what columns does the profiles table have?", we read
the source there. The behavior we want here matches it; the
implementation here is independent.
