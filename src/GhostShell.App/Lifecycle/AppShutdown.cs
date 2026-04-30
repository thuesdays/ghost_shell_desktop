// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using System.Runtime.Versioning;
using GhostShell.Core.Common;
using GhostShell.Core.Logging;
using GhostShell.Core.Services;
using GhostShell.Runtime.Browser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.Lifecycle;

/// <summary>
/// Centralised shutdown sequence. WPF's default Application.Exit
/// path calls <see cref="IHost.Dispose"/> synchronously, which does
/// NOT await IAsyncDisposable singletons — meaning live browser
/// sessions, the log tail, the auth-proxy forwarders, and any open
/// SQLite handle keep running on a daemon thread while the process
/// is being torn down. The user sees this as "many dotnet.exe (or
/// child chrome.exe / chromedriver.exe) processes still in Task
/// Manager" after closing the window.
///
/// This module runs the cleanup explicitly, in dependency order,
/// with a generous timeout per step so a hung subsystem can't pin
/// the whole shutdown forever.
///
/// Order matters:
///   1. Stop the IProfileRunner — disposes every active browser
///      session (driver.Quit + chromedriver service stop + auth-
///      proxy forwarder dispose + watchdog stop). Per-session
///      DisposeAsync internally reaps any chrome.exe PIDs we know
///      about.
///   2. Stop the LogTail — closes the polling loop's file handle
///      so the day-rolled log isn't held open.
///   3. <see cref="IHost.StopAsync"/> — fires StopAsync on every
///      IHostedService (RunnerHost etc).
///   4. <see cref="IServiceProvider"/>'s async-dispose chain —
///      catches everything else (DatabaseConnection, etc).
///   5. Best-effort orphan sweep — final WMI pass for any
///      chromedriver.exe / chrome.exe still tagged with our
///      user-data-dirs in case step 1 didn't reach them (e.g.
///      crash mid-shutdown).
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppShutdown
{
    /// <summary>Per-step budget. A wedged subsystem won't pin
    /// shutdown for more than this much.</summary>
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Whole-shutdown budget — after this we hard-exit
    /// no matter what's still pending.</summary>
    public static readonly TimeSpan GlobalTimeout = TimeSpan.FromSeconds(20);

    public static async Task RunAsync(IHost host, ILogger log)
    {
        var sw = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + GlobalTimeout;

        // ── 1. Stop active browser sessions ─────────────────────
        await SafeStepAsync(log, "stop-runner", deadline, async () =>
        {
            var runner = host.Services.GetService<IProfileRunner>();
            if (runner is null) return;
            await runner.StopAllAsync();
            // RealProfileRunner is registered as singleton via the
            // IProfileRunner interface; cast to IAsyncDisposable
            // to reach DisposeAsync. The implementation chains
            // into StopAllAsync internally, so this is idempotent.
            if (runner is IAsyncDisposable d)
                await d.DisposeAsync();
        });

        // ── 2. Stop the log tail ────────────────────────────────
        await SafeStepAsync(log, "stop-log-tail", deadline, async () =>
        {
            var tail = host.Services.GetService<LogTail>();
            if (tail is null) return;
            await tail.DisposeAsync();
        });

        // ── 3. IHost.StopAsync — drains hosted services ─────────
        await SafeStepAsync(log, "host-stop", deadline,
            () => host.StopAsync(CancellationToken.None));

        // ── 4. ServiceProvider async-dispose chain ──────────────
        await SafeStepAsync(log, "host-async-dispose", deadline, async () =>
        {
            // Cast goes via the host's services so EVERY registered
            // IAsyncDisposable singleton (DatabaseConnection,
            // factories holding sockets, anything we didn't reach
            // explicitly above) gets its DisposeAsync awaited.
            if (host is IAsyncDisposable hd)
                await hd.DisposeAsync();
            else
                host.Dispose();
        });

        // ── 5. Final orphan sweep — best-effort backstop ────────
        await SafeStepAsync(log, "orphan-sweep", deadline, () =>
        {
            ReapOrphanChromeProcesses(log);
            return Task.CompletedTask;
        });

        log.LogInformation(
            "Shutdown complete in {Ms}ms (deadline budget {Budget}s)",
            sw.ElapsedMilliseconds, GlobalTimeout.TotalSeconds);
    }

    /// <summary>
    /// Run one step under the per-step timeout AND the remaining
    /// global budget, whichever is shorter. Logs but never throws —
    /// shutdown must always proceed to the next step.
    /// </summary>
    private static async Task SafeStepAsync(
        ILogger log, string name, DateTime deadline,
        Func<Task> work)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            log.LogWarning("Shutdown step '{Step}' skipped — global deadline exceeded", name);
            return;
        }
        var budget = remaining < StepTimeout ? remaining : StepTimeout;

        var sw = Stopwatch.StartNew();
        try
        {
            await work().WaitAsync(budget);
            log.LogInformation("Shutdown step '{Step}' done in {Ms}ms",
                name, sw.ElapsedMilliseconds);
        }
        catch (TimeoutException)
        {
            log.LogWarning(
                "Shutdown step '{Step}' timed out after {Ms}ms — abandoning",
                name, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Shutdown step '{Step}' threw after {Ms}ms — continuing",
                name, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// WMI sweep: kill any chrome.exe / chromedriver.exe whose
    /// command line references one of our profile user-data-dirs.
    /// This is a backstop for crash paths where step 1 didn't run
    /// — under normal shutdown the runner's StopAllAsync already
    /// reaped these, so this finds nothing.
    /// </summary>
    private static void ReapOrphanChromeProcesses(ILogger log)
    {
        // Only kill chrome.exe / chromedriver.exe whose command
        // line contains our profiles directory — prevents wiping
        // out the user's normal Chrome.
        var profilesRoot = AppPaths.ProfilesDir;
        if (string.IsNullOrEmpty(profilesRoot)) return;

        var killed = 0;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, Name, CommandLine FROM Win32_Process " +
                "WHERE Name = 'chrome.exe' OR Name = 'chromedriver.exe'");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                using var mo = (System.Management.ManagementObject)obj;
                var cmd = mo["CommandLine"] as string ?? "";
                if (!cmd.Contains(profilesRoot, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (mo["ProcessId"] is not { } pidObj) continue;

                try
                {
                    var pid = Convert.ToInt32(pidObj);
                    using var proc = Process.GetProcessById(pid);
                    if (proc.HasExited) continue;
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1500);
                    killed++;
                }
                catch (ArgumentException) { /* process already gone */ }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Could not reap orphan PID");
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Orphan sweep WMI query failed (non-fatal)");
        }

        if (killed > 0)
            log.LogWarning(
                "Orphan-sweep killed {Count} stray chrome/chromedriver process(es) " +
                "— these should have been reaped by the runner stop step",
                killed);
    }
}
