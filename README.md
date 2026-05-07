# Ghost Shell — Desktop (C# native rewrite)

Native Windows desktop app for the Ghost Shell antidetect-browser
control plane. Pure C# / .NET 8 / WPF. No Python subprocess, no
WebView2, no web wrappers — fully native UI written in XAML with a
custom dense-IDE theme inspired by VS Code / Linear / GitHub Dark.

This is a **clean rewrite**. The legacy Flask + browser dashboard
(see `F:\projects\ghost_shell_browser\`) stays as a reference for
behavior, schema, and UX patterns — we read from it, we don't depend
on it.

## Status — Phase 71cc (UI redesign + scheduler rewrite + theming)

- [x] Solution + 4 projects scaffolded
- [x] Core domain models (Profile, Run, Proxy, DeviceTemplate, ProxyHealthEvent…)
- [x] SQLite persistence + migrations V1-V28
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
- [x] Tray icon + background mode + GitHub auto-update (Phases 36-38)
- [x] Real proxy tester + run queue + bulk profile ops (Phases 61-64)
- [x] **Browser action recorder** — capture clicks/typing into ScriptStep[] (Phase 63)
- [x] Wallet import templates (OKX / MetaMask / Phantom) (Phase 67)
- [x] **Extension popup recorder** — multi-window drain via Selenium handles (Phase 68)
- [x] **Profile-scoped vault aliases** — `{{vault.SEED}}`, `{{vault.PASSWORD}}`, `{{vault.TOTP}}` resolve through profile-bound credentials, no numeric IDs (Phase 69)
- [x] **Bulk vault import** — paste CSV / load file / fetch Google Sheet, map columns to fields, auto-bind rows to profiles by name (Phase 69)
- [x] **Auto-rotate IP on launch** — per-profile checkbox, triggers proxy rotation URL pre-launch (Phase 71)
- [x] **Theming** — Dark (Linear/Vercel) + Light (Solar) palettes, Settings → Appearance picker (Phase 71aa)
- [x] **Scheduler rewrite** — auto-jitter from window/runs_per_day, persistent daily counter, no more "150 fires in 4 hours" bug (Phase 71cc)
- [x] **Run History counters** — REQ / ADS / CAPTCHA columns now reflect real per-run numbers (Phase 71dd)
- [x] **Navigation back-stack** — deep-link clicks (Overview tiles) leave a Back chip; sidebar resets it (Phase 71v)
- [ ] Auto-update — Velopack (deferred)

### v0.0.3.0 — UI redesign + scheduler rewrite + theming + Run History fixes (this release)

A wide-front polish pass touching nearly every page plus two correctness fixes that meaningfully change runtime behaviour. Six broad areas:

**1. Scheduler rewrite (correctness — biggest user-facing fix).**
The "Simple" trigger no longer takes independent min/max-jitter pairs. The runner now derives the gap automatically from `(active_window_seconds / runs_per_day)`, with a single new `UseJitter` checkbox that either uniformly spaces fires or randomises ±50% around the computed mean. Old behaviour: 150 runs in a 7am-9pm window with default 20-180s jitter fired the entire daily quota in the first ~4 hours then went silent. New behaviour: 14h × 150 → 336s mean (~5.6 min between fires), spread evenly across the window. Migration V28 adds three columns to `schedules`: `use_jitter`, `fires_today`, `last_fire_day`. Daily counter is now persistent (survives app restarts — pre-fix, relaunching at noon let the user blow past the cap and fire another 150 runs the same day). Outside-active-window deferrals jump straight to next-window-start instead of writing +1-minute updates every 30 seconds overnight. Profile-already-running fires return `Deferred` (no fire_count bump, no log spam) instead of `Launched`. Legacy MinJitter/MaxJitter columns kept for back-compat — pre-V28 rows still work until they're re-edited, after which jitter goes auto-computed.

**2. Theming infrastructure (Settings → Appearance).**
Two themes shipped: **Dark — Linear/Vercel** (high-contrast inkblot, near-black canvas, big delta between BgBase and BgRaised so cards lift visibly) and **Light — Solar** (near-white canvas, pure-white cards, soft gray sidebar, hue palette punched up to tailwind-500 weights for legibility on white). Picker is a new tab in Settings with two click-to-select tiles showing colour-swatch previews. Selection persists via `SettingsKeys.UiTheme`; runtime applies the saved theme at startup BEFORE MainWindow's XAML parses (StaticResource brushes bake at parse time, so live-swap isn't possible without a full DynamicResource migration). Theme change prompts a restart; the restart is wrapped in `cmd /c timeout /t 1 && start "" "exe"` so the singleton mutex can release before the new instance starts.

**3. Profiles redesign.**
Card action strip rebuilt: Start button is right-aligned, lime-green (`ButtonOk`), MinWidth ≈ 92px (was full-width primary blue). Stop slot uses `ButtonDanger` red. Edit icon stays compact at 36px. Status pill is a real chip with coloured dot + text ("idle" / "ready" / "starting" / "running") instead of the previous em-dash that read as a stretched-icon glitch. Bulk-action strip cleaned: Test-proxies and Self-check moved to the row context menu; the strip now shows only Delete (muted-red `ButtonDangerSoft` so the user doesn't reflexively click) and Start (lime green). Table view gets a tri-state header checkbox for select-all/none with `IsAllSelected` on the VM and a `_suppressSelectionBubble` guard so 500-row bulk selects aren't O(N²).

**4. Overview dashboard redesign.**
Five hero stat tiles redrawn as a layered template: top-of-card 3px accent bar in the category hue, soft-tinted Hue*Soft background (~8% alpha), large faded MDL2 watermark glyph in the bottom-right corner, and a 14×12px-padded body. Per-tile colours: Total Runs (Accent blue), Success Rate (orange), Profiles (green), Vault (amber), Traffic 24h (violet). Hover lifts via DropShadow. Bottom Proxies / Unique Domains tiles get the same treatment. **"View all runs"** button on Recent Activity card now navigates to the **Runs** page (not Logs — that was the previous binding, an obvious bug). Total Runs tile is now clickable and routes to Runs.

**5. Navigation back-stack + Back chip.**
`INavigationService` gains `pushHistory: bool` parameter, `CanGoBack`, `GoBack()`. Sidebar/footer clicks pass `pushHistory=false` (clears the stack — sidebar is "root nav"); Overview tile clicks pass `pushHistory=true` (pushes current onto stack). MainWindow's title bar shows a small **"⟨ Back"** chip when `CanGoBack=true`. Click → returns to whichever page the user came from. Sidebar nav from a deep-linked page resets the stack so the chip disappears — sidebar should never leave a Back arrow pointing at wherever you happened to be.

**6. Window-level loading overlay.**
`LoadingOverlay` moved out of individual pages (ProfilesView, GroupsView) into MainWindow at the body-Grid level. `MainViewModel.IsBusy` bubbles `Current?.IsBusy` via `INotifyPropertyChanged` subscription (with `partial void OnCurrentChanged(old, new)` to swap subscriptions without leaking). Body Grid gets a `Style.Trigger`-driven `BlurEffect` (Radius=8 Gaussian) when IsBusy=true, so the sidebar gets dimmed alongside the content. The overlay sibling sits on top and shows the spinner + "Loading data…" caption.

**Run History counters fix.** Columns REQ / ADS / CAPTCHA in the Runs page were always 0 because nothing ever wrote to `runs.total_queries / total_ads / captchas` — `RunService.UpdateCountersAsync` simply didn't exist. Added it; `ScriptRunner.RunCounters` now also tracks `QueriesExecuted` (incremented on `search_query`, +N on `commercial_inflate`) and `CaptchasSolved` (on `solve_captcha` detect). RealProfileRunner stamps the totals onto the runs row right after `ExecuteAsync` returns. Historical runs stay 0; new runs surface real numbers.

**Bonus fixes**:
- Self-check `tz=?` everywhere — variable was never assigned from probe results despite the JS capturing it. Fixed: now logs `tz=Europe/Kyiv` (or wherever) and persists to DB column.
- `[RAW]` log lines with `[00:00:00]` timestamp — `LogParser` regex expected `WAR` but Serilog emits `WRN` for warnings. Every WARN line silently fell to the RAW path. Fixed regex + added secondary embedded-timestamp extraction so partial RAW lines still get a usable time.
- Cancellation wording: `"Script ... cancelled (0/2 steps failed)"` (which read as "everything succeeded") replaced with `"cancelled after N step(s) (browser closed externally or user stopped)"`.
- Status badge on profile cards: idle state now reads "idle" instead of em-dash; `Run History` exit-130 / external_close runs render with proper status pills.

Schema bump: **Migration V28** (schedules: `use_jitter`, `fires_today`, `last_fire_day`).

### v0.0.2.8 — auto-rotate IP per profile

Single focused feature: **Per-profile "Auto-rotate IP on launch" toggle.** When enabled AND the assigned proxy has `IsRotating=true` + a non-empty rotation URL, the runtime hits that rotation URL with a simple HTTP GET right BEFORE launching the browser, getting a fresh IP for each session. Best-effort design: if the rotation request times out or fails, the runner logs a warning and proceeds with the existing IP (launch isn't aborted). Default OFF for existing profiles (preserves behaviour). 

Changes:
- **Profile model:** New `AutoRotateIp` bool field (default false).
- **Database:** Migration V26 adds `auto_rotate_ip INTEGER NOT NULL DEFAULT 0` column to `profiles` table.
- **ProfileEditorDialog:** New checkbox "Auto-rotate IP on launch (if proxy supports it)" with tooltip explaining the feature is best-effort and only works when the proxy has a rotation URL configured.
- **RealProfileRunner:** Phase 71 checks the toggle on every `StartAsync`; if true + proxy is configured + has rotation enabled, fetches the proxy record, extracts the `RotationApiUrl`, and hits it with `HttpClient.GetAsync` before `_launcher.LaunchAsync`. 2-second settle after rotation so the upstream proxy has time to register the new exit IP.

### v0.0.2.7 — graceful self-update: active-runs drain, scheduler pause, orphan sweep

Four coordinated changes ensure self-updates don't kill active scripts mid-execution:

1. **`IsUpdatePending` flag + scheduler pause.** When `ApplyAsync` starts, it sets `IsUpdatePending = true`. Every scheduler tick at the TOP of `TickAsync` checks this flag and returns early (skips firing any new schedules). This stops the scheduler from launching fresh runs while an update is preparing.
2. **Active-runs drain loop.** After extraction completes but BEFORE spawning PowerShell, `ApplyAsync` polls `IRunService.ListAsync(status: Running)` every 3 seconds, capped at 5 minutes. When the count hits zero, the drain is complete and the file swap can proceed. If 5 minutes elapse with stale runs still active, we log a warning and continue anyway so a hung browser session doesn't block updates indefinitely. Progress jumps from 91 → 95 during the drain.
3. **Startup orphan-run sweep.** New `StartupRunSweeper` IHostedService runs BEFORE `RunnerHost` at app boot. It queries for any runs with `finished_at IS NULL` from the previous session (crash without cleanup) and marks them as "interrupted" with exit_code=130 + stop_reason="interrupted_by_restart". Reconciles the state so orphan rows don't confuse the runner.
4. **Periodic update re-check every 6 hours.** Replaced the one-shot startup check with a loop that fires every 6 hours (first check after 30s startup grace, then every 6h thereafter). Dialog only surfaces once per discovered version (deduped by `LatestVersion`), so the user isn't pestered if they dismiss it and an app restart happens before the next check. Notification bell still dedupes per-version separately via the `source = "update:<version>"` key.

The update can still fail (network timeout, checksum mismatch, locked files) but those failures no longer leave zombie "running" rows behind, and the scheduler never races against the drain window.

### v0.0.2.6 — UX polish: import progress, multi-delete proxies, universal step probability

Three quality-of-life improvements stacked together:

1. **Chrome import no longer freezes the UI for 5 seconds.** The import call ran on the dispatcher thread with synchronous SQLite + DPAPI work inside, so the page locked up while it ran. Now the import is wrapped in `Task.Run` and a `DispatcherTimer` ticks every 500 ms to update the status with elapsed seconds + a stage hint ("Importing… 2s · decrypting cookie values"). The status reads as a live progress indicator instead of a frozen "Importing…" string.
2. **Bulk delete on the Proxies page.** `SelectionMode="Extended"` on the data grid + a new "🗑 Delete selected" button + `BulkDeleteAsync` command. Ctrl-click / Shift-click rows to multi-select, hit the button, and N proxies vanish in one confirmation prompt. Per-row failures are tracked separately so a single bad row doesn't abort the rest of the pass.
3. **Universal `probability` per-step gate.** The existing `ScriptStep.Probability` field (0..1) is now consistently applied at the top of step dispatch in BOTH list and graph mode. Default 1.0 = always run (existing scripts unchanged). Set to e.g. 0.7 via the Step Flags dialog's slider → step fires on ~7 of 10 invocations. Works for ANY action — `click_ad`, `commercial_inflate`, `search_query`, `save_var`, anything inside a `foreach` body — so you can build varied per-iteration behaviour without writing if-conditions. The previous attempt to ship a `probability` param specifically on `commercial_inflate` was reverted in favour of this consolidated approach (one slider in one place, applies universally).

### v0.0.2.5 — Chrome import: 3-tier file-acquisition + v20-prefix diagnostics

The previous import path failed against running Chrome with `SQLite Error 14: 'unable to open database file'` because both the file copy AND the source-direct fallback hit Chrome's exclusive lock. Replaced with a three-tier strategy:

1. **`File.Copy` with `FileShare.ReadWrite | FileShare.Delete`** — works for the common case where Chrome opened the file with normal sharing.
2. **Hand-rolled `FileStream` copy** — same share flags but routed through user-mode stream API instead of Win32 `CopyFileEx`. Sometimes succeeds where `File.Copy` doesn't (memory-mapped regions, AV file-system filters that block kernel-mode copy).
3. **SQLite-direct with `immutable=1` URI flag** — last-resort lock bypass. The `?immutable=1` query parameter tells SQLite "this database file will not change while you have it open" — SQLite then skips ALL `LockFileEx` calls and journal-file probing, which lets us open the file even when Chrome holds an exclusive write lock. Trade-off: any rows still in the live WAL aren't visible (we see whatever was last checkpointed into the main file, typically minutes-old at worst), but we read SOMETHING instead of erroring out.

`BuildSqliteConnString` now takes a `sourceIsLocked` flag and switches between the two URI forms. The pathway is logged so you can see which tier won (e.g. "stream-copy succeeded for Cookies (File.Copy was blocked)" or "reading source DB directly via SQLite immutable=1").

**Diagnostic for "0 cookies, 62 undecryptable".** The previous version silently lumped every unreadable cookie into a single counter. Now the import tracks the encryption-prefix histogram (`v10`, `v11`, `v20`, `(empty)`, …) and surfaces an actionable warning when undecryptable cookies are detected:
- `v20` cookies (Chrome v127+ App-Bound encryption) → "X cookies use Chrome v127+ App-Bound encryption (prefix 'v20') which can only be decrypted from inside an authenticated chrome.exe process. Workaround: launch Chrome with `--disable-features=LockProfileCookieDatabase`, or use a different browser profile (Edge / Brave / older Chrome) for the source."
- Unknown prefixes (future Chrome versions) → listed verbatim with counts.

This is the typical cause of the user-visible "62 undecryptable" pattern in the user's log.

### v0.0.2.4 — script-runner abort propagation + clearer Chrome import errors

**`ScriptAbortException` was being absorbed by the foreach handler.** When a step inside a `foreach` body died on a closed browser session, the inner `ExecuteStepsAsync` correctly raised `ScriptAbortException("browser session closed mid-run")`. But the OUTER `ExecuteStepsAsync` (one level up, processing the foreach as a top-level step) caught it as a generic `Exception`, marked the foreach as a failed step, and continued — letting `ExecuteAsync` return "partial" status without re-throwing. RealProfileRunner saw a clean return and logged "Script ... finished cleanly", which contradicted the warning lines right above showing the script had aborted with a dead session. Fix: both list-mode and graph-mode catch handlers now check `if (ex is ScriptAbortException) throw;` BEFORE the generic-error branch, so explicit aborts propagate strict.

**`RealProfileRunner` now logs the actual run status.** Previously every script return path logged "finished cleanly" regardless of whether `ScriptRun.Status` was `ok`, `partial`, or `failed`. Now the verb depends on status (`finished cleanly` only for `ok`; `finished WITH FAILURES (X/Y steps failed)` for `partial`/`failed`) and the severity is `Warning` instead of `Information` for non-clean runs.

**Quieter dead-session logging.** Selenium's `NoSuchWindowException` ("target window already closed") was dumped with a full stack trace whenever the user closed the browser mid-run or the watchdog tore down the session. Both ScriptRunner (step-level) and SeleniumBrowserSession.GetCookies (cookie-snapshot path) now recognize `NoSuchWindowException` + the related `invalid session id` / `session deleted` / `not connected to DevTools` messages, log them at info-level with a one-line message, and skip the stack trace.

**Chrome import — actionable error message.** SQLite Error 14 ("unable to open database file") happens when Chrome is running and holds an exclusive lock on `Cookies` / `History`. The dialog used to surface the bare exception text; now if the message contains `unable to open database file` or `database is locked`, the dialog says "Chrome is running and has its data files locked. Close ALL Chrome windows (also check Task Manager for stray chrome.exe processes) and retry the import." Original error text is still appended for debugging.

### v0.0.2.3 — self-update fixes + persistent update notification

**Self-update never restarted.** Three coordinated bugs caused self-update to "minimise to tray and close everything" without ever swapping in the new build:

1. **`Application.Shutdown(0)` ran without setting `App.AllowingShutdown = true`.** That made `MainWindow.OnClosing` hit its tray-hide branch (`e.Cancel = true; Hide()`) before WPF overrode the cancel and tore the app down. The brief Hide() is what the user saw as "minimise to tray". Fixed by mirroring the tray-Quit path: set `AllowingShutdown = true` and call `MainWindow.AllowClose()` on every open MainWindow before `Shutdown(0)`.
2. **PowerShell helper bailed out on a path-mismatch false-positive.** `Assembly.GetExecutingAssembly().Location` returns the path to the managed DLL (`GhostShell.dll`) for self-contained .NET 8 publishes, but the helper compared it against `$parentProc.MainModule.FileName` which surfaces the apphost path (`GhostShell.exe`). The strings always differed → "parent PID recycled" → `exit 1` → file swap never ran. C# now uses `Process.GetCurrentProcess().MainModule.FileName` (with a `.dll → sibling .exe` fallback), and the PowerShell side compares by **directory** rather than full path so a stale staged update from the previous build can't brick the swap.
3. **`Start-Process -FilePath "GhostShell.exe"`** at the end of the helper resolved relative to PowerShell's CWD (not `-WorkingDirectory`), so even when the swap succeeded the restart could miss the binary. Now joins the bare filename to `$Target` to get a full absolute path before launching.

Helper script also gets a proper `Log` function (timestamped lines), more breadcrumbs (every input arg + dir comparison), and explicit error-stacktrace dump on the catch path. If a future update fails, `%LocalAppData%\GhostShellDesktop\update.log` will tell you exactly where.

**"Update available" is now a persistent notification.** Previously the update only appeared as a one-shot dialog at startup — once dismissed, the user had no way to get back to it. Now an `info` notification is added to the bell drawer (`source = "update:<version>"`, action = `show_update`) the first time a new version is detected. Click the row in the drawer any time to re-open the install dialog. The notification stays in the active list until the user explicitly dismisses it, and dedup is per-target-version so a future v0.0.3.0 release will surface fresh.

### v0.0.2.2 — splash window stays visible after minimising to tray

The previous patch (`v0.0.2.1`) only addressed one half of the bug. Some users still saw the splash hanging on screen when the main window was minimised to tray. This release rewrites the splash close path to be paranoid:

- **`App.OnStartup`** — replaced the `Loaded` + fade-animation close path with a `ContentRendered` + synchronous-`Close()` path. `ContentRendered` fires after the first paint of the main window (more reliable than `Loaded` during slow boots) and the subscription is moved to BEFORE `Show()` so we never miss it. A dispatcher-timer fallback (5s) force-closes the splash even if `ContentRendered` never arrives. The handler is single-fire (guarded by a `splashClosed` flag) and unsubscribes itself + nulls the timer to drop every reference to the splash Window — nothing can resurrect it.
- **`MainWindow.OnClosing`** — removed the `ShowInTaskbar = false` toggle. `Hide()` already takes the window out of the taskbar (WPF: `Visibility = Hidden` removes from taskbar regardless of the flag). Toggling `ShowInTaskbar` on a visible window forces WPF to tear down and recreate the native HWND, which re-fires `Window.Loaded` and was the upstream trigger for the splash visual to flash back into view.
- **`TrayIconHost.ShowAndActivateMainWindow` / `HideMainWindow`** — same simplification: no `ShowInTaskbar` manipulation on either side. `Show()` / `Hide()` alone handle taskbar membership cleanly.

End result: minimise to tray, restore from tray, repeat — no splash ever reappears.

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
