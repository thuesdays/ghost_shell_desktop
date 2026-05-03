// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 64 — Run queue. A small in-memory scheduler that lets the UI
/// enqueue N profile launches at staggered intervals with a hard
/// concurrency cap. The Profiles bulk-start path drops jobs here; a
/// background dispatcher pulls them in order, waits the per-job
/// stagger, and calls <see cref="IProfileRunner.StartAsync"/> when
/// the active count is under the cap.
///
/// In-memory only (no DB persistence) — restart wipes the queue. That's
/// intentional: a queued run is a near-term commitment, not a schedule.
/// Use <see cref="IScheduleService"/> for recurring runs.
/// </summary>
public interface IRunQueueService
{
    /// <summary>Enqueue a single profile launch. Returns the assigned
    /// queue id for tracking + cancellation.</summary>
    Guid Enqueue(QueuedRun job);

    /// <summary>Enqueue a batch of profile launches with a per-pair
    /// stagger gap and a max-concurrent cap. The first profile starts
    /// immediately; the second after <paramref name="staggerSeconds"/>;
    /// and so on. The dispatcher additionally enforces the hard cap.
    /// Phase 65 — <paramref name="probeOnly"/> launches each profile
    /// without its assigned script + without session restore (used by
    /// Bulk Self-Check).</summary>
    IReadOnlyList<Guid> EnqueueBatch(
        IEnumerable<string> profileNames,
        int staggerSeconds,
        int maxConcurrent,
        string source = "bulk",
        bool probeOnly = false);

    /// <summary>Remove a still-queued job. No-op if it already started
    /// or completed.</summary>
    bool Cancel(Guid id);

    /// <summary>Snapshot of pending + running + recently-finished jobs,
    /// newest first. Bounded — old finished entries are pruned to keep
    /// the queue page lightweight.</summary>
    IReadOnlyList<QueuedRun> Snapshot();

    /// <summary>Fired whenever the queue contents or any job's status
    /// changes. UI subscribers marshal to dispatcher.</summary>
    event EventHandler? QueueChanged;
}

/// <summary>One entry in the run queue. Immutable after enqueue except
/// for the <see cref="Status"/> + <see cref="StartedAt"/> + <see cref="FinishedAt"/>
/// + <see cref="ErrorMessage"/> fields which the dispatcher updates.</summary>
public sealed class QueuedRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ProfileName { get; init; }

    /// <summary>UTC time at which the dispatcher is allowed to start
    /// this run. Set during EnqueueBatch by accumulating staggerSeconds
    /// from the batch start.</summary>
    public DateTime ScheduledAt { get; init; } = DateTime.UtcNow;

    /// <summary>Free-form tag for grouping in the UI ("bulk", "schedule",
    /// "manual"). Affects nothing in dispatch, only display.</summary>
    public string Source { get; init; } = "manual";

    /// <summary>
    /// Phase 65 — probe-only launch (no assigned script, no session
    /// restore). Used by Bulk Self-Check so each profile launches just
    /// long enough for the self-check probe to fire (~5s) without
    /// kicking off the user's Goodmedika / SERP-engagement automation.
    /// </summary>
    public bool ProbeOnly { get; init; }

    public QueuedRunStatus Status { get; set; } = QueuedRunStatus.Pending;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public long? RunId { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime EnqueuedAt { get; init; } = DateTime.UtcNow;
}

public enum QueuedRunStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Cancelled,
}
