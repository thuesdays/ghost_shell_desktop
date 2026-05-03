// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Queue;

/// <summary>
/// Phase 64 — Run Queue dispatcher.
///
/// Architecture:
///   1. Public API enqueues jobs (single or batch) and returns Ids.
///   2. A background loop ticks every <see cref="TickMs"/> ms:
///      - Counts currently-running jobs by querying
///        <see cref="IProfileRunner.ActiveProfileNames"/>.
///      - For each Pending job whose <see cref="QueuedRun.ScheduledAt"/>
///        has passed AND active &lt; concurrency-cap, marks it Running
///        and kicks <see cref="IProfileRunner.StartAsync"/>.
///      - Polls active jobs against ActiveProfileNames; when a profile
///        disappears (script finished or user closed browser) the queue
///        marks the job Done.
///   3. Old Done/Failed/Cancelled entries past <see cref="HistoryRetention"/>
///      are pruned on each tick to keep the queue page lightweight.
///
/// Concurrency: the queue uses ConcurrentDictionary for the storage
/// and a single dispatcher loop — no locks needed for individual
/// status writes. The Tick handler runs single-threaded so we never
/// see a torn read of "active count".
/// </summary>
public sealed class RunQueueService : BackgroundService, IRunQueueService
{
    private readonly IProfileRunner _runner;
    private readonly IProfileService _profiles;
    private readonly ILogger<RunQueueService> _log;

    private readonly ConcurrentDictionary<Guid, QueuedRun> _jobs = new();
    private int _maxConcurrent = 4;

    /// <summary>Dispatcher tick — fast enough that staggered launches
    /// fire within ~1s of their scheduled time, slow enough to not
    /// thrash the runner's internal locks. 500ms is the sweet spot.</summary>
    private const int TickMs = 500;

    /// <summary>Keep the most recent N finished jobs visible so the
    /// queue page can show "what just ran". Older jobs are pruned.</summary>
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromMinutes(30);

    public event EventHandler? QueueChanged;

    public RunQueueService(
        IProfileRunner runner,
        IProfileService profiles,
        ILogger<RunQueueService> log)
    {
        _runner   = runner;
        _profiles = profiles;
        _log      = log;
    }

    public Guid Enqueue(QueuedRun job)
    {
        _jobs[job.Id] = job;
        _log.LogInformation(
            "RunQueue: enqueued '{Profile}' at {When} (id={Id}, source={Source})",
            job.ProfileName, job.ScheduledAt, job.Id, job.Source);
        QueueChanged?.Invoke(this, EventArgs.Empty);
        return job.Id;
    }

    public IReadOnlyList<Guid> EnqueueBatch(
        IEnumerable<string> profileNames, int staggerSeconds,
        int maxConcurrent, string source = "bulk", bool probeOnly = false)
    {
        var names = profileNames.ToList();
        // Update the cap if the caller asked for something different
        // — last-writer-wins. (Simpler than per-batch caps; the queue
        // is a global facility.) Use Volatile.Write for safe publication
        // to the dispatcher loop's read.
        System.Threading.Volatile.Write(ref _maxConcurrent, Math.Max(1, maxConcurrent));

        var ids = new List<Guid>(names.Count);
        var now = DateTime.UtcNow;
        for (int i = 0; i < names.Count; i++)
        {
            var job = new QueuedRun
            {
                ProfileName = names[i],
                ScheduledAt = now.AddSeconds(staggerSeconds * i),
                Source      = source,
                ProbeOnly   = probeOnly,
            };
            ids.Add(Enqueue(job));
        }
        _log.LogInformation(
            "RunQueue: batch enqueued {N} job(s), stagger={Stagger}s cap={Cap} probeOnly={Probe}",
            names.Count, staggerSeconds, maxConcurrent, probeOnly);
        return ids;
    }

    public bool Cancel(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        if (job.Status != QueuedRunStatus.Pending) return false;
        job.Status = QueuedRunStatus.Cancelled;
        job.FinishedAt = DateTime.UtcNow;
        _log.LogInformation("RunQueue: cancelled '{Profile}' (id={Id})",
            job.ProfileName, job.Id);
        QueueChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public IReadOnlyList<QueuedRun> Snapshot()
    {
        // Newest-first order so the queue page shows pending at the
        // top + recent-finished below them. Tie-broken by enqueue time.
        return _jobs.Values
            .OrderByDescending(j => j.Status == QueuedRunStatus.Running ? 2
                                   : j.Status == QueuedRunStatus.Pending ? 1 : 0)
            .ThenByDescending(j => j.EnqueuedAt)
            .ToList();
    }

    // ── Dispatcher loop ──────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("RunQueue dispatcher started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "RunQueue tick crashed (continuing)");
            }
            try { await Task.Delay(TickMs, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _log.LogInformation("RunQueue dispatcher stopped");
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var snapshot = _jobs.Values.ToList();

        // 1) Mark Running jobs as Done if their profile is no longer
        //    active. The runner is single source of truth.
        var active = _runner.ActiveProfileNames;
        var anyChange = false;
        foreach (var job in snapshot.Where(j => j.Status == QueuedRunStatus.Running))
        {
            if (!active.Contains(job.ProfileName))
            {
                job.Status = QueuedRunStatus.Done;
                job.FinishedAt = now;
                anyChange = true;
                _log.LogInformation(
                    "RunQueue: '{Profile}' finished (id={Id}, ran for {Sec}s)",
                    job.ProfileName, job.Id,
                    job.StartedAt.HasValue ? (now - job.StartedAt.Value).TotalSeconds : 0);
            }
        }

        // 2) Start Pending jobs whose ScheduledAt has arrived if we
        //    have headroom under the concurrency cap.
        var runningCount = snapshot.Count(j => j.Status == QueuedRunStatus.Running);
        var due = snapshot
            .Where(j => j.Status == QueuedRunStatus.Pending && j.ScheduledAt <= now)
            .OrderBy(j => j.ScheduledAt)
            .ToList();
        foreach (var job in due)
        {
            if (runningCount >= _maxConcurrent) break;
            // Skip if this profile already has a different active run
            // (e.g. the user manually started it). Keep the job Pending
            // and let it fire on a later tick once that finishes.
            if (active.Contains(job.ProfileName)) continue;

            try
            {
                var profile = await _profiles.GetAsync(job.ProfileName, ct);
                if (profile is null)
                {
                    job.Status = QueuedRunStatus.Failed;
                    job.ErrorMessage = "profile not found";
                    job.FinishedAt = now;
                    anyChange = true;
                    continue;
                }
                job.Status = QueuedRunStatus.Running;
                job.StartedAt = now;
                runningCount++;
                anyChange = true;
                // Fire-and-forget — the runner has its own teardown
                // path; we just need the kick. Errors land in the
                // catch below + mark Failed. Phase 65 — pass through
                // ProbeOnly: when set, runAssignedScript=false and
                // restoreSession=false so Bulk Self-Check doesn't fire
                // the user's automation script.
                var runScript = !job.ProbeOnly;
                var restoreSession = !job.ProbeOnly;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var runId = await _runner.StartAsync(
                            profile, ct,
                            runAssignedScript: runScript,
                            restoreSession:    restoreSession);
                        job.RunId = runId;
                    }
                    catch (Exception startEx)
                    {
                        _log.LogWarning(startEx,
                            "RunQueue: start of '{Profile}' (id={Id}) failed",
                            job.ProfileName, job.Id);
                        job.Status = QueuedRunStatus.Failed;
                        job.ErrorMessage = startEx.Message;
                        job.FinishedAt = DateTime.UtcNow;
                        QueueChanged?.Invoke(this, EventArgs.Empty);
                    }
                }, ct);
                _log.LogInformation(
                    "RunQueue: started '{Profile}' (id={Id}, queue position={Pos})",
                    job.ProfileName, job.Id, runningCount);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "RunQueue: could not load profile '{Profile}'", job.ProfileName);
                job.Status = QueuedRunStatus.Failed;
                job.ErrorMessage = ex.Message;
                job.FinishedAt = now;
                anyChange = true;
            }
        }

        // 3) Prune ancient finished jobs.
        foreach (var job in snapshot)
        {
            if (job.Status is QueuedRunStatus.Done or QueuedRunStatus.Failed or QueuedRunStatus.Cancelled
                && job.FinishedAt.HasValue
                && now - job.FinishedAt.Value > HistoryRetention)
            {
                if (_jobs.TryRemove(job.Id, out _))
                    anyChange = true;
            }
        }

        if (anyChange) QueueChanged?.Invoke(this, EventArgs.Empty);
    }
}
