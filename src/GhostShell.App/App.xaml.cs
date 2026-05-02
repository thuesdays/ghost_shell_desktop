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
using GhostShell.Data;
using GhostShell.Data.Database;
using GhostShell.Core.Services;
using GhostShell.Runtime;
using GhostShell.Runtime.Browser;
using GhostShell.Runtime.Diagnostics;
using GhostShell.Runtime.ProxyAuth;
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

        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseGhostShellLogging()
            .ConfigureServices((_, s) =>
            {
                // Persistence
                s.AddGhostShellData();

                // Phase 38: Tray icon (singleton + hosted service)
                s.AddSingleton<TrayIconHost>();
                s.AddHostedService<TrayIconHost>(sp => sp.GetRequiredService<TrayIconHost>());

                // Background runner (Phase 1 stub)
                s.AddHostedService<RunnerHost>();

                // Diagnostics: Phase 2 ships a deterministic stub.
                // Phase 3 swaps in a real HttpClient-based probe.
                s.AddSingleton<IProxyTester, StubProxyTester>();

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

        try
        {
            // Apply migrations BEFORE the first VM resolves a service
            // that touches the DB — otherwise OverviewViewModel's
            // OnNavigatedToAsync would race against schema creation.
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

        await Host.StartAsync();

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
            var window = new MainWindow
            {
                DataContext = Host.Services.GetRequiredService<MainViewModel>(),
            };
            MainWindow = window;
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
                        Application.Current?.Shutdown(0);
                    });
                };
            }
            catch (Exception ex)
            {
                bootLogger.LogWarning(ex, "Couldn't subscribe ShutdownRequested handler");
            }

            // [Phase 37 fix:] Fire-and-forget update check on a background task
            // Use InvokeAsync to avoid dispatcher deadlock, and guard against
            // dispatcher being destroyed during shutdown
            _ = Task.Run(async () =>
            {
                try
                {
                    var svc = Host.Services.GetRequiredService<IUpdateService>();
                    var info = await svc.CheckAsync();
                    if (info is not null && svc.UpdateAvailable)
                    {
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
                catch (Exception ex)
                {
                    bootLogger.LogWarning(ex, "Update check failed at startup");
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
