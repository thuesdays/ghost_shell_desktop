// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.IO;
using System.Windows;
using GhostShell.App.Dialogs;
using GhostShell.App.Lifecycle;
using GhostShell.App.Logging;
using GhostShell.App.Navigation;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
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
            typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?");
        bootLogger.LogInformation(" Data dir: {DataDir}", AppPaths.DataDir);
        bootLogger.LogInformation(" Database: {Db}",      AppPaths.DatabasePath);
        bootLogger.LogInformation(" Log file: {Log}",     LoggingSetup.CurrentLogPath);
        bootLogger.LogInformation("════════════════════════════════════════════════════════════");

        try
        {
            // Apply migrations BEFORE the first VM resolves a service
            // that touches the DB — otherwise OverviewViewModel's
            // OnNavigatedToAsync would race against schema creation.
            Host.Services.GetRequiredService<MigrationRunner>().Run();
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

            bootLogger.LogInformation("Main window shown — startup complete");
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

    protected override async void OnExit(ExitEventArgs e)
    {
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

        // Flush every Serilog sink before the process actually ends —
        // otherwise the last few writes to the daily file may be lost.
        Serilog.Log.CloseAndFlush();

        base.OnExit(e);
    }
}
