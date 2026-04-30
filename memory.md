# Ghost Shell Desktop — Session Memory

> Snapshot for resuming the chat in a new session. Use this to rebuild context fast.

## Project at a glance

- **Location**: `F:\projects\ghost_shell_desktop\` (the C# port; legacy web at `F:\projects\ghost_shell_browser\`)
- **Stack**: C# / .NET 8 / WPF, MVVM via CommunityToolkit.Mvvm, SQLite via Microsoft.Data.Sqlite + Dapper, Serilog, Microsoft.Extensions.Hosting (DI + hosted services)
- **Solution**: `GhostShell.sln` with four projects:
  - `GhostShell.Core` — POCOs + service interfaces + cron parser + LogTail/LogFilter
  - `GhostShell.Data` — SQLite migrations, Dapper-backed services
  - `GhostShell.Runtime` — Selenium-based browser launcher, watchdog, RunnerHost (the scheduler-tick hosted service)
  - `GhostShell.App` — WPF shell, viewmodels, dialogs, themes, navigation
- **Tests**: `tests\GhostShell.Tests` (xUnit). Cover cron, log filter, services, watchdog state machine, etc.
- **Version**: `0.2.0`, `<InformationalVersion>0.2.0-phase4.4</InformationalVersion>` (worth bumping to phase5 — scheduler is in)
- **License**: MIT, SPDX headers on every .cs / .xaml. Author: Mykola Kovhanko `<thuesdays@gmail.com>`
- **Build/run**:
  ```
  cd F:\projects\ghost_shell_desktop
  dotnet build GhostShell.sln
  src\GhostShell.App\bin\Debug\net8.0-windows\GhostShell.exe
  ```
  WPF binary is `net8.0-windows`, NOT `net8.0`. WinExe — `dotnet run` won't show stderr; run the exe directly from cmd to see early-startup errors.

## Goal

Migrate the legacy Python/Flask antidetect-browser control plane to a fully native WPF desktop app. UX targets: same feature set as legacy, **better looking** desktop UI. Patched Chromium 149.0.7805.0 lives at `F:\projects\ghost_shell_browser\chrome_win64\` (not in this repo).

## Current state — what's working

All ported / shipped:

- **Core CRUD**: profiles, proxies, runs, sessions/cookies, cookie packs, profile groups, schedules
- **Profiles page** with cards/table toggle, bulk-select column, bulk start/delete, **Bulk-create modal** (prefix + count + start index + language + FP template + proxy round-robin pool + enrich)
- **Groups page** (Phase 4.5) — port of the legacy Groups UI; cards with Start group / Stop N + member count + cap. `GroupEditorDialog` with member checkbox-list + filter
- **Scheduler page** (Phase 5) — cards with live "next fire in Xm Ys" countdown (DispatcherTimer 1Hz), stat boxes (NEXT FIRE / LAST FIRED / FIRES/FAILS), Run-now button, pause/edit/delete. `ScheduleEditorDialog` with Interval/Cron tabs, cron preview (next 5 fires), day toggle chips (Mon..Sun), active-hours window with wrap-around support
- **Runs page** — bulk-fetch + client-side filter, status pill column, "View logs for this run" → navigates to Logs with profile + time-window prefilter
- **Logs page v2** — file-tail buffer (2000 cap), level/source/profile/time/regex filters, run-context banner, fixed-width grid columns ([105 ts] [36 lvl] [170 source] [: ] [* msg], all top-aligned so multi-line messages stay aligned), Pause/Resume/Copy/Download (with SaveFileDialog picker) buttons, ListBox virtualization
- **Sessions / Cookie packs** — full CRUD ported, gzipped pack BLOBs in SQLite, per-origin storage dump via CDP `Network.getAllCookies`/`setCookies`
- **Proxy page** — bulk-import with live-preview parser, per-row Test/Edit/Delete, health timeline at the bottom
- **Theme**: dark, ContextMenu / MenuItem / ToolTip / ToggleButton implicit styles applied. Per-page sidebar icon hues (HueBlue/Green/Orange/Teal/Violet/Pink/Amber/Indigo/Slate)
- **Inter font** — embedded via `tools\copy-fonts.ps1` + MSBuild `<Target Name="CopyInterFont">` that auto-copies from `F:\projects\inter_font\Inter-VariableFont_opsz,wght.ttf`. `FontUi` resource = `/Assets/Fonts/#Inter, Inter, Segoe UI Variable Text, Segoe UI, Arial`

## Architecture / key invariants

- **All DB writes go through `DatabaseConnection.QueueAsync`** — a single SemaphoreSlim serializes every read/write because `SqliteConnection` is not thread-safe and we share one connection process-wide. Transactions still fit because the gate is held for the entire lambda
- **Migrations** are linear SQL strings in `Migrations_V1..V8.cs`, applied by `MigrationRunner.Run()` recording in `__schema_version`. Currently up to V8 (schedules)
- **DateTime.Kind contract** for schedules: everything stored AS UTC in SQLite TEXT format, `ScheduleService.AsUtc()` enforces on writes, `ForceUtc()` re-tags `Kind=Utc` on reads. Cron parser operates on local time — RunnerHost converts via `.ToLocalTime()` / `.ToUniversalTime()` at the boundary
- **Concurrency cap** is `RunnerHost.MaxParallelLaunches=4` (TODO: surface in Settings page). `IProfileRunner.ActiveProfileNames` is the authority for "is profile X live"
- **Navigation lifecycle**: `BaseViewModel.OnNavigatedToAsync` + `OnNavigatedFromAsync`. `NavigationService.NavigateTo` calls Navigated-From on the outgoing VM, then Navigated-To on the new one. Used by `SchedulerViewModel` to start/stop its 1Hz countdown timer so it doesn't tick off-screen
- **Schedule fire outcomes**: `FireOutcome.Launched | Failed | Deferred`. Deferral (active-window guard, runner-cap raced) routes through `IScheduleService.RecordDeferralAsync` which only updates `next_fire_at` — does NOT bump `fail_count`. Real failure routes through `RecordFailureAsync` with exponential back-off `30 * (1 << min(fail_count, 7))` capped at 1h
- **Watchdog** (per-session): `SessionWatchdog` heartbeats every 3s, detects external close (driver throws on `currentWindowHandle` access), pause/resume hook, fires `Faulted` event with structured stop reason → DB run-history write via `RealProfileRunner.StopInternalAsync`
- **App shutdown**: `AppShutdown.RunAsync` runs a 5-step ordered teardown (stop scheduler / drain sessions / dispose runner / kill orphan chrome.exe / dispose host). `App.OnExit` calls it; `AppDomain.ProcessExit` is a backstop
- **Logging**: Serilog via `LoggingSetup.UseGhostShellLogging` in `App.OnStartup`. Files at `%LocalAppData%\GhostShell\logs\app-YYYY-MM-DD.log` (daily rolling, 14-day retention)
- **Boot trace**: `App.BootTracePath` writes to `%LocalAppData%\GhostShell\boot-trace.log` BEFORE Serilog initializes — used to diagnose silent-startup failures (XAML parse errors, DI registration crashes)

## Recently completed work in this chat

(in chronological order, all files committed but unbuilt)

1. **Runs page redesign** — view-logs button per row, status pill column via `RunToPillStyleConverter`/`RunToPillForegroundConverter`, ContextMenu "View logs for this run"
2. **Logs row redesign** — replaced inline Run-flow with fixed-width Grid columns; added run-context banner (`HasRunContext`/`PageTitle`/`PageSubtitle` computed properties on `LogsViewModel`); `FilterForRun(Run)` method routes from Runs page
3. **Bulk create profiles** — `BulkCreateProfilesDialog` + `IProfileService.BulkCreateAsync` with single-transaction insert, collision auto-skip with `long probeIdx` (overflow-safe)
4. **Profiles cards/table toggle** — Cards (default) WrapPanel of cards; Table = original DataGrid. `IsCardsMode`/`IsTableMode` MultiDataTriggers. ContextMenu shared between both modes
5. **Profiles bulk-select** — checkbox column + checkbox in cards, accent-strip "N selected · Bulk start · Bulk delete" appears when `HasSelection`. Bulk start awaits sequentially with stagger
6. **Groups page** — Migration V7, `IProfileGroupService` with diff-based `SetMembersAsync`, `GroupsViewModel` + `GroupsView` (cards with running-count chip), `GroupEditorDialog` with member picker
7. **Sidebar coloured icons** — `HueXxx` palette in `Colors.xaml`, `SidebarRow.IconBrush` carries the brush, `MainWindow.xaml` binds `Foreground="{Binding IconBrush}"`
8. **ContextMenu/MenuItem/ToolTip styles** — color-only overrides in DarkTheme.xaml. Earlier custom Templates broke `ResourceDictionary.DeferrableContent` — REMOVED. **Critical lesson**: the file already had ContextMenu+MenuItem+Separator implicit styles around line 539; I duplicated them at the end. Duplicate `TargetType` keys throw `ArgumentException` at App.xaml parse → silent crash before Serilog. NEVER add a second implicit style for the same TargetType in one ResourceDictionary
9. **Tooltip dark style** — implicit ToolTip style with HasDropShadow=True
10. **Inter font** — `Assets/Fonts/Inter-Variable.ttf` via `<Resource>` + MSBuild `CopyInterFont` target auto-copies from `F:\projects\inter_font\`
11. **Runner work loop** — `RunnerHost` rewritten from stub to full scheduler tick (every 30s). `TickAsync(ct)` snapshots `utcNow`+`localNow` once, calls `_schedules.GetDueAsync(utcNow, ct)`, iterates each schedule via `FireAsync`. Active-window check uses `localNow`, cron evaluation converts UTC→local. Cap re-checked inside `FireProfileAsync`/`FireGroupAsync` to close TOCTOU. Group fan-out staggers 150ms between launches
12. **Scheduler page + dialog** — Migration V8, `Schedule` model with `TargetKind` (Profile/Group) and `TriggerKind` (Cron/Interval), `CronExpression` parser in Core (5-field, Vixie semantics, 366-day cap), `IScheduleService` with `RecordFiredAsync`/`RecordFailureAsync`/`RecordDeferralAsync`, `SchedulerViewModel` with 1Hz `DispatcherTimer` for countdown ticking. `ScheduleEditorDialog` with Interval/Cron tabs and live cron-preview
13. **Audit + fixes** — RecordDeferralAsync (deferral != failure), DateTime.Kind enforced as UTC, cap TOCTOU re-check, navigation timer stop/start, probeIdx → long, Math.Pow → bit shift, ActiveChanged race guard, log filter snapshot
14. **Last visual polish in this chat**:
    - Dark `ToggleButton` implicit style with retemplate (Active days chips were rendering white)
    - Proxy actions column — third attempt: `Width=210`, `CellStyle Padding=0 BorderThickness=0`, `HorizontalAlignment=Center` (icons kept clipping; root cause was DataGridCell focus-visual chrome eating ~15px on right)

## Known issues / pending items

- **Scheduler page Active days chips were rendering with default WPF (white) toggle style** — fixed by adding implicit ToggleButton style
- **Proxy page actions clipped (third report)** — fixed via wider column + zeroed cell padding
- **NOT YET TESTED in latest build** — user needs to `dotnet build` + run after the ToggleButton + Proxy fixes
- **Settings page is still mostly empty** — runner.max_parallel + scheduler defaults need to surface there
- **No tests yet** for Schedule, ScheduleService, RunnerHost.TickAsync, CronExpression
- Tests project (`GhostShell.Tests`) does NOT reference anything from new scheduler/runner work
- **Group "parallel/serial" launch mode** from legacy NOT ported — current behaviour fires up to cap, leaves the rest queued
- **Density mode** from legacy scheduler (target_runs_per_day with jitter) NOT ported — design decision to keep cron+interval only
- **Recovery hooks** (canary, drift detection, IP rotation pause/resume) — NOT in scope yet, will be a future phase
- Audit subagent flagged 13 items, fixed 8 (the high-impact ones). Remaining: tick-loop pinning by slow Fire (needs Task.WhenAny + timeout), SetMembersAsync diff under transaction, run-now vs scheduled-fire row-level race (mostly mitigated by SQLite atomicity), double-dispose on abnormal shutdown (logged but not catastrophic)

## Files of interest (quick lookup)

- **Schema**: `src\GhostShell.Data\Database\Migrations_V1..V8.cs` + `MigrationRunner.cs`
- **Services**: `src\GhostShell.Data\Services\*` (one file per entity)
- **Service interfaces**: `src\GhostShell.Core\Services\I*Service.cs`
- **Models (POCOs)**: `src\GhostShell.Core\Models\*.cs`
- **Cron parser**: `src\GhostShell.Core\Common\CronExpression.cs`
- **Runner**: `src\GhostShell.Runtime\RunnerHost.cs` (full scheduler tick), `src\GhostShell.Runtime\Browser\RealProfileRunner.cs` (the actual launch glue)
- **Browser launch**: `src\GhostShell.Runtime\Browser\BrowserLauncher.cs` + `ChromeOptionsBuilder.cs` + `LaunchPreflight.cs` (orphan sweep)
- **Watchdog**: `src\GhostShell.Runtime\Browser\SessionWatchdog.cs`
- **Theme**: `src\GhostShell.App\Resources\Themes\Colors.xaml` (palette + fonts) + `DarkTheme.xaml` (every implicit style)
- **App lifecycle**: `src\GhostShell.App\App.xaml.cs` (DI bootstrap + boot trace + window guard)
- **Navigation**: `src\GhostShell.App\Navigation\NavigationService.cs` + `BaseViewModel.cs` (with OnNavigatedTo/From hooks)
- **Dialogs**: `src\GhostShell.App\Dialogs\*` (each dialog has its own .xaml + .xaml.cs)
- **VMs**: `src\GhostShell.App\ViewModels\*ViewModel.cs`
- **Views**: `src\GhostShell.App\Views\*View.xaml`

## Current task list status

The latest tasks (#229-231) are about polish: ToggleButton style done, Proxy clip fixed (third attempt), this memory.md being saved.

Earlier audit fix tasks (#221-228) — all complete.

Open earlier tasks (legacy from prior sessions, not actively worked):
- #107 Synthesize master audit report
- #109 Investigate "captcha showing" report
These predate the desktop port and are about the legacy web project — ignore for the desktop work.

## What to ask the user when picking up

Probably:
1. "Did the latest build run cleanly?" (ToggleButton + Proxy fixes)
2. "Any visible regressions?"
3. "What's next?" Options: Settings page (surface runner cap + scheduler defaults), Recovery hooks (canary etc), Group parallel/serial mode, OR new feature.

## Working with the legacy Python project

For exploration of legacy features (when porting): always delegate to a general-purpose subagent with concrete file paths under `F:\projects\ghost_shell_browser\dashboard\`, `\ghost_shell\scheduler\`, `\ghost_shell\db\database.py` etc. Subagent reports back with HTML/JS/Python excerpts that I mirror in C#.

## Code style / conventions

- Comments are tutorial-style, explain *why* (not *what*) — short paragraphs prefixed with `//`
- SPDX headers on every file
- `partial` classes for ObservableObject + RelayCommand source generators
- Implicit styles in DarkTheme.xaml — never duplicate a TargetType across the merged dictionaries
- File-scoped namespaces, primary ctors where it improves clarity, init-only properties on records
- xUnit tests with descriptive test names; no MSpec / FluentAssertions yet

---

**End of session memory.** Pick up from here in a new chat by reading this file first.
