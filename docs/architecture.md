# Architecture — Ghost Shell Desktop

## Layer cake

```
┌─────────────────────────────────────────────────────────────┐
│  GhostShell.App                                             │
│  WPF UI — main window, sidebar, navigation, view models,    │
│  XAML pages, dense-IDE theme.                               │
└──────────────────────┬──────────────────────────────────────┘
                       │ depends on
                       ▼
┌─────────────────────────────────────────────────────────────┐
│  GhostShell.Runtime                                         │
│  Background work: future browser launcher / runner /        │
│  scheduler / process reaper. Phase 1 = empty hosted service.│
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│  GhostShell.Data                                            │
│  SQLite + Dapper. DatabaseConnection, MigrationRunner,      │
│  service implementations (Profile, Run, Proxy).             │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│  GhostShell.Core                                            │
│  Pure POCO domain models + service interfaces.              │
│  No third-party deps. Sits at the bottom — every other      │
│  project references it.                                     │
└─────────────────────────────────────────────────────────────┘
```

Dependency rule: an arrow from A → B means A references B. There are
no upward arrows, no skipped layers, no circular references.

## Why these specific projects

- **Core** is intentionally tiny and dep-free so unit-testing models
  doesn't drag in SQLite or WPF.
- **Data** owns the SQLite handle and is the only place that knows
  about Dapper / SQL syntax. Swapping persistence (PostgreSQL,
  in-memory) means changing only this project.
- **Runtime** is split off from Data because it owns processes
  (Chromium child processes, future scheduler timers). Mixing those
  with persistence makes lifecycle reasoning harder.
- **App** is WPF-only and depends on everything below. ViewModels
  receive services through DI; views never resolve services
  themselves.

## DI bootstrap

`App.xaml.cs` builds a `Microsoft.Extensions.Hosting.IHost`:

1. Logging providers (Debug + Console).
2. `AddGhostShellData()` — extension method in
   `GhostShell.Data.ServiceCollectionExtensions` registers the
   `DatabaseConnection`, `MigrationRunner`, and the three Phase-1
   services as singletons.
3. `AddHostedService<RunnerHost>()` — empty stub for now, runs as
   `IHostedService` so Phase 3 work can subclass without changing
   bootstrap.
4. View models registered as singletons so navigation back to a page
   restores its state.

Migrations run synchronously *before* the first window appears, so
the very first VM call to `IRunService.GetStatsAsync` doesn't race
with schema creation.

## Navigation

`INavigationService` keeps a single dictionary of `string → VM type`.
`MainViewModel` listens to its `CurrentChanged` event and updates the
selected sidebar row. The content area is a plain `ContentControl`
bound to `Current` — the view chosen by the matching `DataTemplate`
in `App.xaml` swaps in.

This is deliberately not a fancy `Frame`/`Page` setup. WPF's `Page`
class adds journaling and cross-page navigation we don't need.

## Theme

`Resources/Themes/Colors.xaml` holds raw colors and brushes.
`DarkTheme.xaml` builds on top with implicit styles for the common
controls (`Window`, `TextBlock`, `Button`, `DataGrid`, `TextBox`,
`ScrollBar`). Page XAML never sets colors directly — it uses
`{StaticResource Accent}`, `{StaticResource OkBrush}`, etc.

## What lives where (Phase 1)

| File                                          | Role                              |
|-----------------------------------------------|-----------------------------------|
| `App.xaml.cs`                                 | DI bootstrap, migrations          |
| `MainWindow.xaml(.cs)`                        | Window chrome, sidebar, content   |
| `Resources/Themes/Colors.xaml`                | Color palette                     |
| `Resources/Themes/DarkTheme.xaml`             | Implicit styles                   |
| `Navigation/NavigationService.cs`             | Page key → VM resolution          |
| `ViewModels/MainViewModel.cs`                 | Sidebar items + active page       |
| `Views/*.xaml`                                | Per-page UI                       |

Every page has a 1:1 view-model. If a VM is missing the page won't
load — the resolver throws. That's intentional: missing wiring shows
up at first navigation, not at runtime when data is requested.

## Logging

We use **Serilog** behind `Microsoft.Extensions.Logging`. Every
`ILogger<T>` resolved from DI funnels through the same pipeline, so
any project that takes a logger via constructor injection
automatically writes to all three sinks below — no per-project
configuration needed.

**Sinks (configured in `LoggingSetup.cs`):**

| Sink     | Purpose                                                     |
|----------|-------------------------------------------------------------|
| File     | `%LocalAppData%\GhostShell\logs\app-YYYY-MM-DD.log`. Daily roll, 50 MB hard cap per file (rolls earlier if hit), 14-day retention. Shared so multiple instances can append safely. |
| Console  | Visible when started via `dotnet run`. Condensed format.    |
| Debug    | Visual Studio Debug Output during F5.                        |

**Defaults:**

- App messages: `Information` and above.
- `Microsoft.*`: `Warning` (filters framework noise).
- Output template (file): `[2026-04-29 17:42:11.123 +03:00 INF] [pid:1234] GhostShell.Data.Services.ProfileService: Created profile 'foo' (...)`.

**Global exception handlers** (installed in `App.OnStartup` via
`GlobalExceptionHandler.Install`):

| Source                                | What we do                                            |
|---------------------------------------|-------------------------------------------------------|
| `AppDomain.UnhandledException`        | Log `Critical`. Process is going to die — last words. |
| `Application.DispatcherUnhandledException` | Log `Error`. Mark `Handled = true` so the UI thread doesn't tear down the app for recoverable issues. |
| `TaskScheduler.UnobservedTaskException` | Log `Error`. Useful for catching async paths whose Task was never awaited. |

**Convention going forward:**

- Every service, repository, and view-model takes `ILogger<T>` in
  its constructor.
- Information-level for regular state changes (created profile,
  loaded N rows, navigated to page).
- Debug-level for low-level traces (SQL prepared, pragma applied).
- Error/Critical only for actual problems — not for "user typed
  invalid input" cases.
- Structured properties (`{ProfileName}` not string concatenation)
  so we can grep / filter later.

**Where to look when something breaks:**

1. Settings page → "Open logs folder" — direct path.
2. Or open `%LocalAppData%\GhostShell\logs\` in Explorer.
3. Today's file is `app-YYYY-MM-DD.log`. Older files use the same
   pattern with progressively older dates.

## Phase roadmap (placement, not timing)

| Phase | What goes where                                            |
|-------|------------------------------------------------------------|
| 1     | This document. Foundation visible.                         |
| 2     | Phase-2 work expands `IProfileService` and adds Create/Edit dialogs. |
| 3     | `BrowserLauncher` and `IRunnerService` land in Runtime.    |
| 4     | Action runner: 49+ handlers ported from `actions/runner.py`. |
| 5     | Scheduler with cron + race-condition guards.               |
| 6     | Vault (Windows Credential Manager / DPAPI).                |
| 7     | Captcha + recorder + behavioral persona.                   |
| 8     | Auto-update via Velopack.                                  |
| 9     | Installer (WiX) + code signing.                            |
