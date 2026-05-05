// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime;

/// <summary>
/// Phase 71 — startup orphan-run sweep. On app boot, checks for any
/// runs still marked as "running" (finished_at IS NULL) from a
/// previous session that crashed without cleanup. Marks them as
/// "interrupted" with exit_code=130 (SIGINT-equivalent).
///
/// This service must run BEFORE RunnerHost starts so the scheduler
/// doesn't see stale "running" rows as still active.
/// </summary>
// Phase 71 — public so App.xaml.cs (a different assembly) can register
// it via AddHostedService<StartupRunSweeper>(). The original `internal`
// visibility hid the type from GhostShell.App and broke DI registration
// at compile time (CS0122 'StartupRunSweeper' is inaccessible).
public sealed class StartupRunSweeper : IHostedService
{
    private readonly IRunService _runService;
    private readonly ILogger<StartupRunSweeper> _log;

    public StartupRunSweeper(IRunService runService, ILogger<StartupRunSweeper> log)
    {
        _runService = runService;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Query for all "running" runs (finished_at IS NULL, exit_code IS NULL).
            var orphans = await _runService.ListAsync(
                limit: 1000,
                status: RunStatusFilter.Running,
                ct: cancellationToken);

            if (orphans.Count == 0)
            {
                _log.LogDebug("Startup sweep: no orphan runs to clean");
                return;
            }

            _log.LogInformation("Startup sweep: found {N} orphan run(s), marking as interrupted", orphans.Count);

            // Mark each orphan as interrupted with exit_code=130 (SIGINT-equivalent)
            // and stop_reason="interrupted_by_restart".
            foreach (var run in orphans)
            {
                try
                {
                    await _runService.FinishAsync(
                        run.Id,
                        exitCode: 130,
                        lastError: "Interrupted by app restart",
                        stopReason: "interrupted_by_restart",
                        ct: cancellationToken);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Sweep: failed to mark run {Id} as interrupted", run.Id);
                }
            }

            _log.LogInformation("Startup sweep: marked {N} orphan run(s) as interrupted", orphans.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Startup sweep failed (non-fatal, continuing startup)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // No-op for now; the sweep runs once at startup.
        return Task.CompletedTask;
    }
}
