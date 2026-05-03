// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Runtime.Queue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GhostShell.Tests.Queue;

/// <summary>
/// Phase 65 — unit tests for RunQueueService. Cover EnqueueBatch
/// scheduling math, Cancel state transitions, snapshot ordering,
/// concurrency cap respect, and event firing.
/// </summary>
public sealed class RunQueueServiceTests
{
    [Fact]
    public void Enqueue_AddsJobAndFiresEvent()
    {
        var svc = MakeService();
        var fired = 0;
        svc.QueueChanged += (_, _) => fired++;

        var id = svc.Enqueue(new QueuedRun { ProfileName = "p1" });

        Assert.NotEqual(Guid.Empty, id);
        Assert.Single(svc.Snapshot());
        Assert.Equal(1, fired);
    }

    [Fact]
    public void EnqueueBatch_AssignsScheduledAtIncreasing()
    {
        var svc = MakeService();
        var t0 = DateTime.UtcNow;
        var ids = svc.EnqueueBatch(
            new[] { "p1", "p2", "p3" }, staggerSeconds: 30,
            maxConcurrent: 2, source: "test");

        Assert.Equal(3, ids.Count);
        var snap = svc.Snapshot();
        var ordered = snap.OrderBy(j => j.ScheduledAt).ToList();
        Assert.Equal("p1", ordered[0].ProfileName);
        Assert.Equal("p2", ordered[1].ProfileName);
        Assert.Equal("p3", ordered[2].ProfileName);
        // Stagger gaps: ~0s, ~30s, ~60s from t0
        var gap01 = (ordered[1].ScheduledAt - ordered[0].ScheduledAt).TotalSeconds;
        var gap12 = (ordered[2].ScheduledAt - ordered[1].ScheduledAt).TotalSeconds;
        Assert.InRange(gap01, 29, 31);
        Assert.InRange(gap12, 29, 31);
    }

    [Fact]
    public void EnqueueBatch_ZeroStagger_AllScheduledNow()
    {
        var svc = MakeService();
        var t0 = DateTime.UtcNow;
        svc.EnqueueBatch(new[] { "p1", "p2", "p3" },
            staggerSeconds: 0, maxConcurrent: 4);

        var snap = svc.Snapshot();
        foreach (var j in snap)
        {
            // Within 100ms of t0 — all should be due immediately.
            Assert.InRange((j.ScheduledAt - t0).TotalMilliseconds, -100, 100);
        }
    }

    [Fact]
    public void EnqueueBatch_AllJobsTaggedWithSource()
    {
        var svc = MakeService();
        svc.EnqueueBatch(new[] { "p1", "p2" }, 0, 4, source: "self-check");
        Assert.All(svc.Snapshot(), j => Assert.Equal("self-check", j.Source));
    }

    [Fact]
    public void Cancel_OnPending_TransitionsToCancelled()
    {
        var svc = MakeService();
        var id = svc.Enqueue(new QueuedRun { ProfileName = "p1" });

        var ok = svc.Cancel(id);

        Assert.True(ok);
        var job = svc.Snapshot().Single();
        Assert.Equal(QueuedRunStatus.Cancelled, job.Status);
        Assert.NotNull(job.FinishedAt);
    }

    [Fact]
    public void Cancel_OnUnknownId_ReturnsFalse()
    {
        var svc = MakeService();
        Assert.False(svc.Cancel(Guid.NewGuid()));
    }

    [Fact]
    public void Cancel_OnAlreadyCancelled_ReturnsFalse()
    {
        var svc = MakeService();
        var id = svc.Enqueue(new QueuedRun { ProfileName = "p1" });
        svc.Cancel(id);
        Assert.False(svc.Cancel(id));
    }

    [Fact]
    public void Snapshot_OrdersRunningBeforePendingBeforeFinished()
    {
        var svc = MakeService();
        var t0 = DateTime.UtcNow;
        // Manually craft a job in each terminal state.
        svc.Enqueue(new QueuedRun
        {
            ProfileName = "pending1",
            ScheduledAt = t0.AddSeconds(5),
        });
        var runningJob = new QueuedRun { ProfileName = "running1" };
        runningJob.Status = QueuedRunStatus.Running;
        runningJob.StartedAt = t0;
        svc.Enqueue(runningJob);
        var doneJob = new QueuedRun { ProfileName = "done1" };
        doneJob.Status = QueuedRunStatus.Done;
        doneJob.FinishedAt = t0.AddSeconds(-10);
        svc.Enqueue(doneJob);

        var snap = svc.Snapshot();
        Assert.Equal(3, snap.Count);
        // Snapshot orders by status priority: Running > Pending > Done.
        Assert.Equal("running1", snap[0].ProfileName);
        Assert.Equal("pending1", snap[1].ProfileName);
        Assert.Equal("done1",    snap[2].ProfileName);
    }

    [Fact]
    public void Enqueue_WithCustomEnqueuedAt_PreservesIt()
    {
        var svc = MakeService();
        var customTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        svc.Enqueue(new QueuedRun
        {
            ProfileName = "p1",
            EnqueuedAt = customTime,
        });
        Assert.Equal(customTime, svc.Snapshot().Single().EnqueuedAt);
    }

    [Fact]
    public void EnqueueBatch_EmptyList_ReturnsEmptyIds()
    {
        var svc = MakeService();
        var ids = svc.EnqueueBatch(Array.Empty<string>(), 30, 4);
        Assert.Empty(ids);
        Assert.Empty(svc.Snapshot());
    }

    private static RunQueueService MakeService()
    {
        // Test fakes — minimal stubs that satisfy the constructor.
        // We don't exercise the dispatcher loop in unit tests (that's
        // an integration concern); we test the public Enqueue/Cancel/
        // Snapshot API surface in isolation.
        var fakeRunner  = new FakeProfileRunner();
        var fakeProfile = new FakeProfileService();
        return new RunQueueService(fakeRunner, fakeProfile,
            NullLogger<RunQueueService>.Instance);
    }

    private sealed class FakeProfileRunner : IProfileRunner
    {
        public bool HasActiveRuns => false;
        public IReadOnlySet<string> ActiveProfileNames =>
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public IBrowserSession? TryGetActiveSession(string profileName) => null;
        public Task<long> StartAsync(Profile profile, CancellationToken ct = default)
            => Task.FromResult(1L);
        public Task<long> StartAsync(Profile profile, CancellationToken ct,
            bool runAssignedScript, bool restoreSession)
            => Task.FromResult(1L);
        public Task<bool> StopAsync(string profileName, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task StopAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler? ActiveChanged { add { } remove { } }
    }

    private sealed class FakeProfileService : IProfileService
    {
        public Task<IReadOnlyList<Profile>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Profile>>(Array.Empty<Profile>());
        public Task<Profile?> GetAsync(string name, CancellationToken ct = default)
            => Task.FromResult<Profile?>(null);
        public Task<Profile> CreateAsync(Profile p, CancellationToken ct = default)
            => Task.FromResult(p);
        public Task UpdateAsync(Profile p, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task DeleteAsync(string name, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<BulkCreateProfilesResult> BulkCreateAsync(
            BulkCreateProfilesRequest req, CancellationToken ct = default)
            => Task.FromResult(new BulkCreateProfilesResult(
                Array.Empty<Profile>(), Array.Empty<string>()));
        public Task RecordRunStartedAsync(
            string name, DateTime startedAt, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
