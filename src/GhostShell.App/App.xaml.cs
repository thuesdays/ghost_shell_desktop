// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.IO;
using System.Threading;
using System.Windows;
using GhostShell.App.Dialogs;
using GhostShell.App.Lifecycle;
using GhostShell.App.Logging;
using GhostShell.App.Navigation;
using GhostShell.App.Tray;
using GhostShell.App.ViewModels;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Data;
using GhostShell.Data.Database;
using GhostShell.Core.Services;
using GhostShell.Runtime;
using GhostShell.Runtime.Browser;
using GhostShell.Runtime.Diagnostics;
using GhostShell.Runtime.ProxyAuth;
using GhostShell.Runtime.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostShell.App;

/// <summary>
/// Application entry. Microsoft.Extensions.Hosting owns lifecycle —
/// Serilog (via UseGhostShellLogging) handles every ILogger&lt;T&gt;
/// resolved from DI, so file/console/debug logging is uniform across
/// all four projects without each one knowing about Serilog.
/// </summary>
public partial class App : Application
{
    public IHost? Host { get; private set; }

    // Single-instance enforcement
    private Mutex? _singletonMutex;
    private EventWaitHandle? _showWindowEvent;
    private volatile bool _appShuttingDown;

    /// <summary>
    /// Phase 71 — process-wide cancellation token source. The periodic
    /// update-check loop subscribes to this Token so it bails cleanly
    /// when the app exits (otherwise the loop's <c>Task.Delay(6h)</c>
    /// would hold a thread-pool worker alive past Application.Shutdown
    /// and block ProcessExit handlers from firing). Created in
    /// OnStartup, Cancelled + Disposed in OnExit.
    /// </summary>
    private CancellationTokenSource? _appShutdownCts;

    // Phase 38: Static flag to signal shutdown from tray's Quit handler
    public static bool AllowingShutdown { get; set; }

    /// <summary>
    /// Phase 38 — set the Windows AppUserModelID for this process.
    /// Without it, NotifyIcon balloons / toast headers attribute to
    /// the apphost's FileDescription, which on dev builds (and even
    /// some publish modes) reads as ".NET Host" instead of our
    /// product name. The AUMID also drives taskbar grouping + jump
    /// list scoping — set it BEFORE any windows are created.
    ///
    /// shell32!SetCurrentProcessExplicitAppUserModelID is in every
    /// Windows since Vista. Returns an HRESULT — we don't fail
    /// startup on a non-zero return; absolute worst case the title
    /// reverts to the FileDescription set in Directory.Build.props,
    /// which we also fixed in the same phase.
    /// </summary>
    private const string AppUserModelId = "GhostShell.Desktop";

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string AppID);

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Phase 38: Set AppUserModelID FIRST so any subsequent
        // notification / window has correct attribution.
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
            // Pre-Vista or sandboxed envs — fall through; balloon
            // attribution falls back to FileDescription metadata.
        }

        // Phase 38: Single-instance enforcement — early, before base.OnStartup
        const string MutexName = "Local\\GhostShellDesktop_Singleton";
        const string EventName = "Local\\GhostShellDesktop_ShowWindow";

        bool createdNew = false;
        try
        {
            _singletonMutex = new Mutex(true, MutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed; we own the mutex now
            _singletonMutex = new Mutex(true, MutexName, out createdNew);
            createdNew = true;
        }

        if (!createdNew)
        {
            // Another instance is running; signal it to show its window and exit.
            try
            {
                if (EventWaitHandle.TryOpenExisting(EventName, out var ev))
                    ev.Set();
            }
            catch { /* best-effort */ }

            Shutdown(0);
            return;
        }

        // Listen for wake-event from future instances (background thread)
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var t = new Thread(() =>
        {
            while (!_appShuttingDown)
            {
                if (_showWindowEvent.WaitOne(1000))
                {
                    var app = Application.Current;
                    if (app is null) continue;
                    app.Dispatcher.InvokeAsync(() => TrayIconHost.ShowAndActivateMainWindow());
                }
            }
        })
        {
            IsBackground = true,
            Name = "ShowWindowListener",
        };
        t.Start();
        // Diagnostic boot trace. Writes to a fixed path under
        // %LocalAppData%\GhostShell\ BEFORE Serilog is wired up, so
        // a crash in App.xaml parsing / Host.Build() / DI registration
        // still leaves a footprint. Each line is "[HH:mm:ss.fff] msg".
        // Catch + ignore to keep this section bullet-proof: we'd
        // rather fail to write a diag line than mask a real error.
        BootTrace("OnStartup begin");

        try
        {
            base.OnStartup(e);
            BootTrace("base.OnStartup ok");
        }
        catch (Exception ex)
        {
            BootTrace("base.OnStartup THREW: " + ex);
            MessageBox.Show(
                $"Startup failure (base.OnStartup):\n\n{ex}",
                "Ghost Shell — fatal", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(10);
            return;
        }

        // Surface ANY exception from here on as a MessageBox + boot
        // trace, otherwise an async-void OnStartup exception is
        // swallowed and the user sees "no window, no log".
        try
        {
            await StartUpInternalAsync();
        }
        catch (Exception ex)
        {
            BootTrace("OnStartup THREW: " + ex);
            try { Serilog.Log.Logger?.Fatal(ex, "OnStartup unhandled"); } catch { }
            MessageBox.Show(
                $"Startup failure:\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                $"Boot trace: {BootTracePath}\n\nFull stack in the trace file.",
                "Ghost Shell — fatal", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(11);
        }
    }

    /// <summary>
    /// Path to the always-on boot trace file. Written even if Serilog
    /// fails to initialise — this is the file to read first when the
    /// app silently refuses to launch.
    /// </summary>
    private static string BootTracePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GhostShell");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "boot-trace.log");
        }
    }

    private static void BootTrace(string msg)
    {
        try
        {
            File.AppendAllText(
                BootTracePath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { /* never fail because of diag */ }
    }

    private async Task StartUpInternalAsync()
    {
        BootTrace("Building host");

        // Phase 71 — process-wide cancellation token so the periodic
        // update-check loop can be killed cleanly on app exit. Created
        // here (before the Task.Run that uses it) and cancelled in
        // OnExit. Without this, the 6-hour Task.Delay would pin a
        // worker thread past process-exit handlers.
        _appShutdownCts = new CancellationTokenSource();

        // Show splash screen before anything else. It will remain visible
        // and update with progress as we initialize services and the DB.
        var splash = new Views.SplashWindow();
        splash.Show();
        splash.SetProgress(5, "Initializing…");

        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseGhostShellLogging()
            .ConfigureServices((_, s) =>
            {
                // Persistence
                s.AddGhostShellData();

                // Phase 38: Tray icon (singleton + hosted service)
                s.AddSingleton<TrayIconHost>();
                s.AddHostedService<TrayIconHost>(sp => sp.GetRequiredService<TrayIconHost>());

                // Phase 71: Orphan-run sweep (must run BEFORE RunnerHost)
                s.AddHostedService<StartupRunSweeper>();

                // Background runner (Phase 1 stub)
                s.AddHostedService<RunnerHost>();

                // Diagnostics: Phase 61 — real proxy tester with TCP
                // reachability + HTTP/SOCKS5/SOCKS4 auto-detect against
                // ip-api.com. Replaced the deterministic StubProxyTester
                // which always returned "ok" with random metadata, masking
                // dead/wrong-protocol proxies until the actual browser
                // launch failed with ERR_PROXY_CONNECTION_FAILED. The new
                // probe mirrors what the browser will do at launch time
                // and surfaces per-scheme attempts in the diagnostic log.
                s.AddSingleton<IProxyTester, HttpProxyTester>();

                // ─── Phase 3: real browser pipeline ──────────────
                // ChromiumLocator finds the patched build on disk;
                // BrowserLauncher composes ChromeOptions + spawns
                // chromedriver via Selenium 4; RealProfileRunner
                // owns the active session lifecycle.
                s.AddSingleton<IChromiumLocator, ChromiumLocator>();
                s.AddSingleton<IBrowserLauncher, BrowserLauncher>();
                s.AddSingleton<IProfileRunner,   RealProfileRunner>();

                // ─── Phase 4: auth-proxy sidecar ────────────────
                // Local HTTP-CONNECT forwarder per browser session
                // — injects Proxy-Authorization upstream so we can
                // hand Chromium a credential-free --proxy-server URL.
                s.AddSingleton<IProxyAuthForwarderFactory, HttpConnectForwarderFactory>();

                // ─── Phase 4.2: Sessions & Cookies ──────────────
                // SessionLifecycle is the runtime hook the runner
                // calls at start (auto-restore latest snapshot) and
                // before clean-stop dispose (auto-save snapshot).
                // ISessionService / ICookiePackService come from
                // AddGhostShellData above.
                s.AddSingleton<SessionLifecycle>();

                // ─── Phase 6: Warmup robot ──────────────────────
                // WarmupService orchestrates: launch browser via
                // IBrowserLauncher, walk the preset, save a snapshot.
                // Persistence (warmup_runs SQL) is in IWarmupHistoryService
                // which AddGhostShellData registers.
                s.AddSingleton<IWarmupService, WarmupService>();

                // ─── Phase 7: Chrome importer + retention sweeper ──
                // ChromeImporter is Windows-only (DPAPI). The
                // QualityMonitor watches captcha rates and fires
                // auto_quality warmups; the SnapshotRetention sweeper
                // prunes old snapshots so the DB doesn't grow forever.
                s.AddSingleton<IChromeImporter, ChromeImporter>();
                s.AddHostedService<WarmupQualityMonitor>();
                s.AddHostedService<SnapshotRetentionService>();

                // ─── Phase 9: Fingerprint orchestration ─────────
                s.AddSingleton<IFingerprintService, GhostShell.Runtime.Fingerprint.FingerprintService>();

                // ─── Phase 11: Self-check probes + FP auto-regen ─
                s.AddSingleton<ISelfCheckService, SelfCheckService>();
                s.AddHostedService<GhostShell.Runtime.Fingerprint.FingerprintQualityMonitor>();

                // ─── Phase 12: Scripts (model + runner) ─────────
                s.AddSingleton<IScriptRunner, GhostShell.Runtime.Scripts.ScriptRunner>();

                // ─── Phase 63: Browser Action Recorder ──────────
                // Singleton — only one recording at a time across the
                // app. ScriptRecorder hooks into a live IBrowserSession
                // via JS injection + polling, generates ScriptStep[]
                // from user gestures.
                s.AddSingleton<IScriptRecorder, GhostShell.Runtime.Recording.ScriptRecorder>();

                // ─── Phase 64: Run Queue ────────────────────────
                // In-memory dispatcher for staggered bulk starts.
                // Registered as both IRunQueueService (for VMs) and
                // IHostedService (so the dispatcher loop runs).
                s.AddSingleton<RunQueueService>();
                s.AddSingleton<IRunQueueService>(sp =>
                    sp.GetRequiredService<RunQueueService>());
                s.AddHostedService(sp =>
                    sp.GetRequiredService<RunQueueService>());

                // ─── Phase 13F: Captcha auto-solve (manual default) ───
                // Phase 14: 2captcha integration available — set
                // TwoCaptchaConfig.ApiKey via Settings to switch.
                // For now register the manual solver as the default;
                // users with a paid API key can re-bind in their own
                // composition root.
                s.AddSingleton<ICaptchaSolver, GhostShell.Runtime.Scripts.ManualCaptchaSolver>();
                s.AddSingleton(new GhostShell.Runtime.Scripts.TwoCaptchaConfig());
                s.AddHttpClient<GhostShell.Runtime.Scripts.TwoCaptchaSolver>();

                // Navigation + dialogs
                s.AddSingleton<INavigationService, NavigationService>();
                s.AddSingleton<IDialogService, DialogService>();

                // Log file tailer — fed into LogsViewModel for the
                // live-tail page. One singleton instance feeds the
                // single Logs page; LogTail handles day-rollover
                // internally so the app doesn't need to recreate it.
                s.AddSingleton<GhostShell.Core.Logging.LogTail>();

                // ViewModels — singletons so navigation back to a page
                // restores its state (filter values, scroll position, etc.).
                s.AddSingleton<MainViewModel>();
                s.AddSingleton<OverviewViewModel>();
                s.AddSingleton<ProfilesViewModel>();
                s.AddSingleton<GroupsViewModel>();
                s.AddSingleton<SchedulerViewModel>();
                s.AddSingleton<RunsViewModel>();
                s.AddSingleton<ProxyViewModel>();
                s.AddSingleton<SessionsViewModel>();
                s.AddSingleton<CookiePacksViewModel>();
                s.AddSingleton<LogsViewModel>();
                s.AddSingleton<SettingsViewModel>();
                s.AddSingleton<FingerprintViewModel>();
                s.AddSingleton<ScriptsViewModel>();
                s.AddSingleton<QueueViewModel>();   // Phase 64
                s.AddSingleton<VaultViewModel>();
                s.AddSingleton<ExtensionsViewModel>();
                s.AddSingleton<DomainsViewModel>();
                s.AddSingleton<CompetitorsViewModel>();
                s.AddSingleton<TrafficViewModel>();
                s.AddSingleton<NotificationsViewModel>();

                // Phase 26 — vault idle watcher. Singleton because it
                // owns a DispatcherTimer + class-level WPF input handlers
                // that are created on first Start(); allowing GC would
                // make those handlers go silently dark.
                s.AddSingleton<GhostShell.App.Vault.VaultIdleWatcher>();
            })
            .Build();
        BootTrace("Host.Build ok");

        // Global exception logging — install before anything that could
        // throw, so even a migration failure leaves a trace on disk.
        var bootLogger = Host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("GhostShell.Bootstrap");

        GlobalExceptionHandler.Install(this, bootLogger);

        // Backstop cleanup hook for paths that bypass OnExit:
        //   • Process killed via Task Manager / kill -TERM
        //   • Windows logoff / reboot
        //   • Unhandled-exception → process termination
        // The handler runs the same async shutdown sequence as the
        // normal exit path; OnExit unhooks it so we don't double-
        // dispose under the regular X-button path.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        bootLogger.LogInformation("");
        bootLogger.LogInformation("════════════════════════════════════════════════════════════");
        bootLogger.LogInformation(" Ghost Shell starting (v{Version})",
            typeof(App).Assembly.GetName().Version?.ToString() ?? "?");
        bootLogger.LogInformation(" Data dir: {DataDir}", AppPaths.DataDir);
        bootLogger.LogInformation(" Database: {Db}",      AppPaths.DatabasePath);
        bootLogger.LogInformation(" Log file: {Log}",     LoggingSetup.CurrentLogPath);

        // Phase 37 audit fix #5: Log all GhostShell.* assembly versions to detect drift.
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name?.StartsWith("GhostShell.", StringComparison.Ordinal) == true)
                    .OrderBy(a => a.GetName().Name))
        {
            bootLogger.LogInformation(" {Asm} {Ver}", asm.GetName().Name, asm.GetName().Version);
        }

        bootLogger.LogInformation("════════════════════════════════════════════════════════════");

        // Update splash: migrations about to start.
        splash.SetProgress(20, "Opening database…");

        try
        {
            // Apply migrations BEFORE the first VM resolves a service
            // that touches the DB — otherwise OverviewViewModel's
            // OnNavigatedToAsync would race against schema creation.
            splash.SetProgress(30, "Migrating schema…");
            Host.Services.GetRequiredService<MigrationRunner>().Run();

            // Phase 31 hot-fix — repair stale ext_id values left over
            // from the old UTF-8 path-hash algorithm. Idempotent: rows
            // that already match are skipped. Runs after MigrationRunner
            // so the extensions table exists.
            try
            {
                Host.Services.GetRequiredService<ExtensionIdMigrator>().Run();
            }
            catch (Exception ex)
            {
                bootLogger.LogWarning(ex,
                    "ExtensionIdMigrator failed — extensions may still be pinned with the wrong id");
            }
        }
        catch (Exception ex)
        {
            bootLogger.LogCritical(ex, "Migration failed at startup — aborting");
            MessageBox.Show(
                $"Database migration failed:\n\n{ex.Message}\n\n" +
                $"Logs: {LoggingSetup.CurrentLogPath}",
                "Ghost Shell — startup error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Update splash: services loading, about to start host.
        splash.SetProgress(50, "Loading services…");

        await Host.StartAsync();

        // Update splash: host started, building UI.
        splash.SetProgress(70, "Initializing UI…");

        // ─── Window creation guard ──────────────────────────────────
        // OnStartup is `async void`, so an exception inside MainWindow
        // ctor (XAML parse error, resource resolution failure, VM ctor
        // throw) is routed to DispatcherUnhandledException — which our
        // handler logs and marks Handled, leaving the process alive
        // with NO visible window. The user sees "dotnet run returns,
        // nothing appears, no error". The wrap below catches that path
        // explicitly so the failure surfaces as a MessageBox + the
        // process exits cleanly.
        try
        {
            // Update splash: almost ready.
            splash.SetProgress(90, "Almost ready…");

            var window = new MainWindow
            {
                DataContext = Host.Services.GetRequiredService<MainViewModel>(),
            };
            MainWindow = window;

            // Close splash once the main window is shown.
            //
            // Phase 69b — the previous Loaded-based approach was unreliable.
            // Two chronic failure modes we observed in the wild:
            //
            //   1. After ShowInTaskbar toggling on tray-minimize, WPF would
            //      recreate the HWND and re-fire Window.Loaded. The replayed
            //      handler hit splash.BeginAnimation on a long-Closed Window
            //      and (perversely) resurrected its visual.
            //   2. On some setups the fade animation never fired Completed
            //      (e.g. when the dispatcher was busy during boot), leaving
            //      the splash Window alive indefinitely. As soon as the user
            //      hid the MainWindow to tray, the splash — which was never
            //      Close()d — became the only visible app window.
            //
            // The new path is paranoid:
            //   • Subscribe to ContentRendered BEFORE Show() — that's the
            //     most reliable "first paint complete" signal in WPF and
            //     fires after Show() schedules the first frame.
            //   • A single-fire CloseSplashOnce() helper guarded by a flag,
            //     so re-entries from any source (re-fired event, race with
            //     fallback timer, exception path) become no-ops.
            //   • A 5-second DispatcherTimer fallback that force-closes the
            //     splash even if ContentRendered never arrives. We'd rather
            //     show a momentarily empty desktop than a stuck splash.
            //   • No animation, no Opacity tween — Close() runs synchronously
            //     after a tiny perceptual beat at 100%.
            //   • All references (handler delegate, timer) are nulled after
            //     the close so nothing hangs onto the splash Window.
            var splashClosed = false;
            EventHandler? contentRenderedHandler = null;
            System.Windows.Threading.DispatcherTimer? splashFallback = null;

            void CloseSplashOnce()
            {
                if (splashClosed) return;
                splashClosed = true;
                try
                {
                    if (contentRenderedHandler is not null)
                        window.ContentRendered -= contentRenderedHandler;
                }
                catch { /* unsubscribe failures don't matter — flag is the real guard */ }
                contentRenderedHandler = null;

                try
                {
                    splashFallback?.Stop();
                }
                catch { /* timer may already be torn down */ }
                splashFallback = null;

                try
                {
                    splash.SetProgress(100, "Ready");
                    splash.Hide();
                    splash.Close();
                }
                catch (Exception splashEx)
                {
                    bootLogger.LogWarning(splashEx, "Splash close failed (non-fatal)");
                }
            }

            contentRenderedHandler = (_, _) => CloseSplashOnce();
            window.ContentRendered += contentRenderedHandler;

            // Belt-and-braces: even if ContentRendered never fires (rare
            // dispatcher-starvation cases observed during slow startups),
            // force-close after 5s. Any subsequent ContentRendered will
            // hit splashClosed=true and no-op.
            splashFallback = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            splashFallback.Tick += (_, _) =>
            {
                bootLogger.LogWarning("Splash close fallback timer fired — ContentRendered didn't arrive in 5s");
                CloseSplashOnce();
            };
            splashFallback.Start();

            // NOW show the window — by this point ContentRendered has a
            // subscriber, the fallback timer is armed, and the splash will
            // definitely close one way or another.
            window.Show();

            // Phase 26 — start the auto-lock idle watcher only after
            // the main window is up. The watcher hooks class-level
            // input events on Window so it must run after at least one
            // Window has been created (the EventManager call is fine
            // earlier, but starting the timer here keeps everything in
            // one obvious "post-show wiring" block).
            try
            {
                Host.Services.GetRequiredService<GhostShell.App.Vault.VaultIdleWatcher>().Start();
            }
            catch (Exception ex)
            {
                bootLogger.LogWarning(ex, "VaultIdleWatcher failed to start — auto-lock disabled");
            }

            bootLogger.LogInformation("Main window shown — startup complete");

            // Phase 36 — wire the updater's "shutdown please" signal
            // BEFORE the first ApplyAsync can fire. The Data layer
            // raises this on the thread that called ApplyAsync, so
            // we marshal back to the dispatcher for the actual
            // Shutdown call (which is dispatcher-affine).
            try
            {
                var updater = Host.Services.GetRequiredService<IUpdateService>();
                updater.ShutdownRequested += (_, _) =>
                {
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        bootLogger.LogInformation("Updater asked for shutdown — closing for binary swap");
                        // Phase 69c — match the tray "Quit" path: flag the
                        // shutdown as allowed AND let every MainWindow know
                        // it can really close. Without this, MainWindow.OnClosing
                        // hits its tray-hide branch (e.Cancel=true; Hide())
                        // which makes the window flash to tray during the
                        // update; Application.Shutdown still completes
                        // (it overrides Cancel) but the brief Hide() before
                        // teardown is what the user sees as "minimised to
                        // tray then everything closed".
                        AllowingShutdown = true;
                        try
                        {
                            foreach (Window w in Application.Current!.Windows)
                            {
                                if (w is MainWindow mw) mw.AllowClose();
                            }
                        }
                        catch (Exception allowEx)
                        {
                            bootLogger.LogWarning(allowEx,
                                "AllowClose pass before update-shutdown failed (continuing)");
                        }
                        Application.Current?.Shutdown(0);
                    });
                };
            }
            catch (Exception ex)
            {
                bootLogger.LogWarning(ex, "Couldn't subscribe ShutdownRequested handler");
            }

            // Phase 71 — periodic update check every 6 hours. The first check
            // runs after a 30-second startup grace period (lets splash close +
            // main window render cleanly). Then repeats every 6 hours thereafter.
            var _dialogShownForVersion = (Version?)null; // Track to skip re-dialog on same version
            _ = Task.Run(async () =>
            {
                var ct = _appShutdownCts?.Token ?? CancellationToken.None;
                // First check after a 30-second startup grace period (lets the
                // splash close + main window render cleanly).
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { return; }

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var svc = Host.Services.GetRequiredService<IUpdateService>();
                        var info = await svc.CheckAsync();
                        if (info is not null && svc.UpdateAvailable)
                        {
                            // Phase 69c — also persist as a bell-drawer
                            // notification so the user can re-open the
                            // dialog any time, not just on this startup.
                            // Dedupes against existing ACTIVE rows for the
                            // same target version: if the user already has
                            // a pending update notification, we don't add a
                            // second one. Dismissed rows DON'T count, so
                            // dismiss-then-restart re-surfaces the badge.
                            try
                            {
                                var notifSvc = Host.Services.GetRequiredService<INotificationService>();
                                var src = $"update:{info.LatestVersion}";
                                var active = await notifSvc.ListActiveAsync(200);
                                var alreadyPresent = active.Any(n =>
                                    string.Equals(n.Source, src, StringComparison.Ordinal));
                                if (!alreadyPresent)
                                {
                                    var title = $"Update available — v{info.LatestVersion}";
                                    var body  = string.IsNullOrWhiteSpace(info.ReleaseName)
                                        ? $"You're on v{info.CurrentVersion}. Click to install."
                                        : $"You're on v{info.CurrentVersion}. {info.ReleaseName}. Click to install.";
                                    await notifSvc.AddAsync(
                                        severity: NotificationSeverity.Info,
                                        title:    title,
                                        body:     body,
                                        action:   "show_update",
                                        actionArg: info.LatestVersion.ToString(),
                                        source:   src);
                                }
                            }
                            catch (Exception nx)
                            {
                                bootLogger.LogWarning(nx,
                                    "Couldn't persist update-available notification (non-fatal)");
                            }

                            // Phase 71 — only show the dialog once per discovered version.
                            // The notification bell already handles repeated notifications,
                            // but the modal dialog should surface only once so the user
                            // isn't pestered repeatedly.
                            if (_dialogShownForVersion != info.LatestVersion)
                            {
                                _dialogShownForVersion = info.LatestVersion;
                                // [Phase 37 fix:] Use InvokeAsync + Task to avoid deadlock
                                // and check that Application.Current still exists.
                                // The previous `await … ?? Task.CompletedTask` form
                                // doesn't compile: DispatcherOperation isn't a Task
                                // and `??` can't mix with `await` on a void-typed
                                // chain. Guard explicitly.
                                try
                                {
                                    var app = Application.Current;
                                    if (app is not null)
                                    {
                                        await app.Dispatcher.InvokeAsync(() =>
                                        {
                                            if (Application.Current is not null)
                                            {
                                                UpdateAvailableDialog.ShowFor(MainWindow, svc, info);
                                            }
                                        });
                                    }
                                }
                                catch (TaskCanceledException)
                                {
                                    // Dispatcher was destroyed during shutdown; ignore
                                    bootLogger.LogDebug("Update dialog skipped — app shutting down");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        bootLogger.LogWarning(ex, "Periodic update check failed");
                    }

                    // Wait 6 hours before the next check
                    try { await Task.Delay(TimeSpan.FromHours(6), ct); }
                    catch (OperationCanceledException) { break; }
                }
            });
        }
        catch (Exception ex)
        {
            bootLogger.LogCritical(ex, "MainWindow construction failed — aborting");
            MessageBox.Show(
                "Could not open the main window:\n\n" +
                $"{ex.GetType().Name}: {ex.Message}\n\n" +
                $"Logs: {LoggingSetup.CurrentLogPath}",
                "Ghost Shell — startup error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    /// <summary>
    /// Backstop for crash paths and OS-initiated exit (logoff,
    /// reboot, killed-by-task-manager). The dispatcher is not
    /// guaranteed to be alive here, so we run a synchronous best-
    /// effort orphan sweep — kill any chrome.exe / chromedriver.exe
    /// holding our profiles dir. The OnExit override below handles
    /// the normal "user clicks X" path with the full async cleanup.
    /// </summary>
    private void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            if (OperatingSystem.IsWindows() && Host is not null)
            {
                var log = Host.Services
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("GhostShell.Bootstrap");
                log.LogInformation("ProcessExit handler firing (backstop cleanup)");
                // Reuse the async runner if we still can, then sweep.
                AppShutdown.RunAsync(Host, log).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Process is already going down. Anything we throw here
            // surfaces as a generic AppDomain unhandled-exception
            // event nobody can act on. Swallow.
        }
        finally
        {
            try { Serilog.Log.CloseAndFlush(); } catch { /* swallow */ }
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is logging off or shutting down. Set flag so OnClosing
        // doesn't hijack it back into a hide.
        AllowingShutdown = true;
        base.OnSessionEnding(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Phase 38: Signal shutdown to the wake-event listener thread
        _appShuttingDown = true;

        // Phase 71 — cancel the periodic update-check loop's token. The
        // loop sits in `await Task.Delay(TimeSpan.FromHours(6), ct)` and
        // would otherwise pin a thread-pool worker until the wait
        // expired naturally — past process-exit handler completion.
        // Cancelling lets the loop observe OperationCanceledException
        // and unwind cleanly during the OnExit window.
        try
        {
            _appShutdownCts?.Cancel();
            _appShutdownCts?.Dispose();
            _appShutdownCts = null;
        }
        catch { /* best-effort cleanup */ }

        // Phase 38: Tell every Window the close is real this time so
        // the OnClosing override doesn't hijack it back into a hide.
        // Without this, calling Application.Current.Shutdown from the
        // tray's "Quit" menu item would: tray click → Shutdown() →
        // each Window's Close() → OnClosing(e) → e.Cancel=true → window
        // never closes → Shutdown loops or hangs on the dispatcher.
        try
        {
            foreach (Window w in Windows)
            {
                if (w is MainWindow mw) mw.AllowClose();
            }
        }
        catch { /* best-effort */ }

        if (Host is not null)
        {
            var logger = Host.Services
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("GhostShell.Bootstrap");
            logger.LogInformation(
                "Ghost Shell shutting down (exit_code={Code})", e.ApplicationExitCode);

            // Centralised, deterministic teardown. Each step has its
            // own timeout so a hung subsystem can't pin the whole
            // shutdown forever; a global cap (20s) hard-stops on
            // pathological cases. AppShutdown.RunAsync internally
            // runs the orphan-chrome sweep as the final step.
            if (OperatingSystem.IsWindows())
            {
                await AppShutdown.RunAsync(Host, logger);
            }
            else
            {
                // Non-Windows fallback (test runners, design-time):
                // just stop the host. Production is Windows-only.
                await Host.StopAsync();
                Host.Dispose();
            }
        }

        // ProcessExit handler is no longer needed — we just ran the
        // full cleanup. Unhook it so the GC can reclaim the closure.
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        // Phase 38: Release and dispose singleton mutex and event
        try
        {
            if (_singletonMutex != null)
            {
                try
                {
                    _singletonMutex.ReleaseMutex();
                }
                catch { /* mutex already released or not owned */ }
                _singletonMutex.Dispose();
            }
            _showWindowEvent?.Dispose();
        }
        catch { /* best-effort */ }

        // Flush every Serilog sink before the process actually ends —
        // otherwise the last few writes to the daily file may be lost.
        Serilog.Log.CloseAndFlush();

        base.OnExit(e);
    }
}
