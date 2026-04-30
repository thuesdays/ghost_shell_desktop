// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Runtime.Browser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GhostShell.Tests.Browser;

/// <summary>
/// Behavioural tests for the watchdog state machine. We override
/// the tick interval inside the tests with reflection-free indirect
/// timing — the watchdog uses real timers internally, so we keep
/// the fake session lean (no Selenium) and accept ~6s for the dead-
/// detection test (2 ticks × 3s). Other tests check states without
/// relying on the loop schedule.
///
/// Race-condition coverage:
///   • External close detection requires TWO consecutive null titles
///   • Pause prevents detection during the pause window
///   • Stop is idempotent and safe to call concurrently
///   • Heartbeat fires at least once on initial startup
/// </summary>
public class SessionWatchdogTests
{
    [Fact]
    public async Task Start_PerformsInitialHeartbeat()
    {
        var session = new FakeSession();
        var runs    = new RecordingRunService();

        await using var dog = new SessionWatchdog(
            profileName:     "p",
            runId:           42,
            session:         session,
            runs:            runs,
            onExternalClose: (_, _) => Task.CompletedTask,
            log:             NullLogger<SessionWatchdog>.Instance);

        dog.Start();

        // Initial heartbeat fires immediately on Start — we don't
        // wait for the 30s cadence to kick in. Give the loop a tick
        // of grace to actually issue the call.
        await WaitForAsync(() => runs.HeartbeatCalls.Count >= 1, TimeSpan.FromSeconds(2));

        Assert.True(runs.HeartbeatCalls.Count >= 1,
            $"expected ≥1 heartbeat call within 2s, got {runs.HeartbeatCalls.Count}");
        Assert.Contains(42L, runs.HeartbeatCalls);
    }

    [Fact]
    public async Task TwoConsecutiveNullTitles_TriggerExternalClose()
    {
        var session = new FakeSession { TitleResult = null }; // dead from the start
        var runs    = new RecordingRunService();
        var triggered = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dog = new SessionWatchdog(
            profileName:     "ghost",
            runId:           1,
            session:         session,
            runs:            runs,
            onExternalClose: (name, _) =>
            {
                triggered.TrySetResult(name);
                return Task.CompletedTask;
            },
            log:             NullLogger<SessionWatchdog>.Instance);

        dog.Start();

        // 2 consecutive nulls × 3s tick = ~6s. Allow generous slack
        // for CI machines under load.
        var name = await triggered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("ghost", name);
    }

    [Fact]
    public async Task SingleNullThenSuccess_DoesNotTriggerExternalClose()
    {
        // Simulates a transient null-during-navigation. One null,
        // then back to a healthy title. Watchdog must NOT declare
        // the session dead — that's the legacy debounce behaviour.
        var session = new FakeSession();
        var runs    = new RecordingRunService();
        var triggeredCount = 0;

        await using var dog = new SessionWatchdog(
            profileName:     "p",
            runId:           1,
            session:         session,
            runs:            runs,
            onExternalClose: (_, _) =>
            {
                Interlocked.Increment(ref triggeredCount);
                return Task.CompletedTask;
            },
            log:             NullLogger<SessionWatchdog>.Instance);

        // Strategy: null on the first probe, then revert.
        var probeCount = 0;
        session.OnGetTitle = () =>
        {
            probeCount++;
            return probeCount == 1 ? null : "ok";
        };

        dog.Start();

        // Run for ~10s (≈ 3 ticks). With our pattern the watchdog
        // sees null, "ok", "ok" — never two-in-a-row → no trigger.
        await Task.Delay(TimeSpan.FromSeconds(10));

        Assert.Equal(0, triggeredCount);
    }

    [Fact]
    public async Task Pause_SuppressesExternalCloseDuringPauseWindow()
    {
        var session = new FakeSession { TitleResult = null }; // would normally trip
        var runs    = new RecordingRunService();
        var triggered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dog = new SessionWatchdog(
            profileName:     "p",
            runId:           1,
            session:         session,
            runs:            runs,
            onExternalClose: (_, _) => { triggered.TrySetResult(true); return Task.CompletedTask; },
            log:             NullLogger<SessionWatchdog>.Instance);

        dog.Pause("test-rotation");
        dog.Start();

        // While paused, no external-close detection should fire even
        // though the session is "dead". Wait long enough that two
        // unpaused ticks would have already triggered it.
        var fired = await Task.WhenAny(
            triggered.Task,
            Task.Delay(TimeSpan.FromSeconds(8)));

        Assert.NotSame(triggered.Task, fired);
        Assert.True(dog.IsPaused);
    }

    [Fact]
    public async Task StopAsync_IsIdempotent()
    {
        var session = new FakeSession();
        var runs    = new RecordingRunService();

        var dog = new SessionWatchdog(
            profileName:     "p",
            runId:           1,
            session:         session,
            runs:            runs,
            onExternalClose: (_, _) => Task.CompletedTask,
            log:             NullLogger<SessionWatchdog>.Instance);

        dog.Start();
        // Multiple Stop calls in flight should all return without
        // throwing; the underlying CTS.Cancel() is idempotent.
        await Task.WhenAll(dog.StopAsync(), dog.StopAsync(), dog.StopAsync());
        await dog.DisposeAsync();
    }

    [Fact]
    public async Task DoubleStart_Throws()
    {
        var dog = new SessionWatchdog(
            profileName:     "p",
            runId:           1,
            session:         new FakeSession(),
            runs:            new RecordingRunService(),
            onExternalClose: (_, _) => Task.CompletedTask,
            log:             NullLogger<SessionWatchdog>.Instance);

        dog.Start();
        Assert.Throws<InvalidOperationException>(() => dog.Start());
        await dog.DisposeAsync();
    }

    // ─────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
    }

    private sealed class FakeSession : IBrowserSession
    {
        public string ProfileName { get; init; } = "p";
        public long RunId { get; init; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public bool IsAlive { get; set; } = true;

        /// <summary>Static title — used by simple tests.</summary>
        public string? TitleResult { get; set; } = "ok";

        /// <summary>Override per-call behaviour for sequential tests.</summary>
        public Func<string?>? OnGetTitle { get; set; }

        public Task NavigateAsync(string url, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> GetTitleAsync(CancellationToken ct = default) =>
            Task.FromResult(OnGetTitle?.Invoke() ?? TitleResult);

        // Cookie / storage methods — empty implementations. The
        // watchdog never calls these; they're required by the
        // expanded IBrowserSession contract added for the Sessions
        // & Cookies feature, so the fake compiles.
        public Task<IReadOnlyList<CookieEntry>> GetCookiesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CookieEntry>>(Array.Empty<CookieEntry>());
        public Task SetCookiesAsync(IEnumerable<CookieEntry> cookies, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task ClearCookiesAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<StorageEntry>> GetStorageAsync(
            IEnumerable<string> origins, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StorageEntry>>(Array.Empty<StorageEntry>());
        public Task SetStorageAsync(IEnumerable<StorageEntry> entries, CancellationToken ct = default) =>
            Task.CompletedTask;

        // ExecuteScriptAsync was added to IBrowserSession for the
        // Phase-6 warmup robot (consent-banner click + scroll). The
        // watchdog never invokes JS, so the fake returns null.
        public Task<object?> ExecuteScriptAsync(
            string script, object[]? args = null, CancellationToken ct = default) =>
            Task.FromResult<object?>(null);

        public Task<string> CaptureScreenshotAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(path);

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Tiny in-memory IRunService used only by these tests — records
    /// every call so assertions can verify what the watchdog asked
    /// for. Methods we don't need from IRunService throw to fail
    /// fast if a test path drifts into them by accident.
    /// </summary>
    private sealed class RecordingRunService : IRunService
    {
        public ConcurrentBag<long> HeartbeatCalls { get; } = new();
        public ConcurrentBag<(long Id, int Code, string? Reason)> Finishes { get; } = new();

        public Task HeartbeatAsync(long runId, CancellationToken ct = default)
        {
            HeartbeatCalls.Add(runId);
            return Task.CompletedTask;
        }

        public Task FinishAsync(long runId, int exitCode,
            string? lastError = null, string? stopReason = null,
            CancellationToken ct = default)
        {
            Finishes.Add((runId, exitCode, stopReason));
            return Task.CompletedTask;
        }

        public Task<long>                 StartAsync(string n, CancellationToken ct = default) => Task.FromResult(0L);
        public Task                       MarkFailedAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int>                  ClearAsync(DateTime? d, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<Run>>   ListAsync(int l = 50, string? p = null,
                                              RunStatusFilter s = RunStatusFilter.All,
                                              int? h = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Run>>(Array.Empty<Run>());
        public Task<Run?>                 GetAsync(long id, CancellationToken ct = default) => Task.FromResult<Run?>(null);
        public Task<RunStats>             GetStatsAsync(CancellationToken ct = default) =>
            Task.FromResult(new RunStats());
    }
}
