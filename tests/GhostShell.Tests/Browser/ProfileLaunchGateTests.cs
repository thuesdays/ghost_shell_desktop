// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System;
using System.Threading;
using System.Threading.Tasks;
using GhostShell.Core.Common;
using GhostShell.Runtime.Browser;
using Xunit;

namespace GhostShell.Tests.Browser;

/// <summary>
/// Phase 71oo — covers <see cref="ProfileLaunchGate"/>. The gate is
/// the only thing preventing two concurrent
/// <c>BrowserLauncher.LaunchAsync</c> calls from killing each other
/// in the preflight orphan-reaper. Every behaviour the gate promises
/// (single-holder per profile, FAIL-FAST on contention, atomic
/// release, case-insensitive keying, independence across profiles)
/// is pinned here so a regression can't sneak in.
/// </summary>
public class ProfileLaunchGateTests
{
    // ── basic acquire / release ─────────────────────────────────────

    [Fact]
    public void Acquire_ThenDispose_AllowsSubsequentAcquire()
    {
        var gate = new ProfileLaunchGate();

        var first = gate.Acquire("p1");
        first.Dispose();

        // After release, the same profile can be acquired again
        // (typical case: sequential launches separated by a few seconds).
        using var second = gate.Acquire("p1");
    }

    [Fact]
    public void Acquire_WhileHeld_ThrowsProfileBusyImmediately()
    {
        var gate = new ProfileLaunchGate();
        using var held = gate.Acquire("p1");

        // No timeout, no wait — the second caller must fail FAST so
        // it doesn't waste work it would then have to throw away.
        var ex = Assert.Throws<ProfileBusyException>(() => gate.Acquire("p1"));
        Assert.Equal("p1", ex.ProfileName);
        Assert.Contains("p1", ex.Message);
    }

    [Fact]
    public void ProfileBusyException_DerivesFromInvalidOperation_ForBackCompat()
    {
        // Existing catch-Exception / catch-InvalidOperationException
        // blocks must keep matching, so older callers that haven't
        // been updated to catch ProfileBusyException still degrade
        // gracefully.
        var ex = new ProfileBusyException("p1");
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.IsAssignableFrom<Exception>(ex);
    }

    // ── independence across profile names ───────────────────────────

    [Fact]
    public void Acquire_DifferentProfiles_DoNotBlockEachOther()
    {
        var gate = new ProfileLaunchGate();

        // Three concurrent acquires across DIFFERENT profiles must
        // all succeed — the gate is per-profile, not global.
        using var a = gate.Acquire("alpha");
        using var b = gate.Acquire("beta");
        using var c = gate.Acquire("gamma");

        Assert.True(gate.IsHeld("alpha"));
        Assert.True(gate.IsHeld("beta"));
        Assert.True(gate.IsHeld("gamma"));
        Assert.False(gate.IsHeld("delta"));
    }

    [Fact]
    public void Acquire_IsCaseInsensitive()
    {
        // Profile names in Ghost Shell are case-insensitive (matches
        // AppPaths.ProfileDir conventions). The gate must agree —
        // otherwise "Profile_1" and "profile_1" would launch in
        // parallel, defeating the whole point.
        var gate = new ProfileLaunchGate();
        using var first = gate.Acquire("Profile_1");

        Assert.Throws<ProfileBusyException>(() => gate.Acquire("profile_1"));
        Assert.Throws<ProfileBusyException>(() => gate.Acquire("PROFILE_1"));
        Assert.Throws<ProfileBusyException>(() => gate.Acquire("pRoFiLe_1"));
    }

    // ── release semantics ───────────────────────────────────────────

    [Fact]
    public void Dispose_IsIdempotent_NoOverReleaseException()
    {
        // SemaphoreSlim(1,1).Release() when CurrentCount is already 1
        // throws SemaphoreFullException — that would surface as a
        // confusing runtime crash. The Releaser uses Interlocked-swap
        // to make double-Dispose a no-op. Verify.
        var gate = new ProfileLaunchGate();
        var d = gate.Acquire("p1");

        d.Dispose();
        d.Dispose();   // must not throw
        d.Dispose();   // not even on third call

        // And subsequent Acquire still works (proves CurrentCount
        // wasn't accidentally pushed above 1, which would silently
        // allow two simultaneous holders later).
        using var again = gate.Acquire("p1");
        Assert.True(gate.IsHeld("p1"));
    }

    [Fact]
    public void IsHeld_TracksAcquireRelease()
    {
        var gate = new ProfileLaunchGate();
        Assert.False(gate.IsHeld("p1"));

        var d = gate.Acquire("p1");
        Assert.True(gate.IsHeld("p1"));

        d.Dispose();
        Assert.False(gate.IsHeld("p1"));
    }

    [Fact]
    public void IsHeld_NullOrUnknown_IsFalse_AndDoesNotThrow()
    {
        var gate = new ProfileLaunchGate();
        Assert.False(gate.IsHeld(null!));
        Assert.False(gate.IsHeld(""));
        Assert.False(gate.IsHeld("never-acquired"));
    }

    // ── input validation ────────────────────────────────────────────

    [Fact]
    public void Acquire_NullOrWhitespace_ThrowsArgument()
    {
        var gate = new ProfileLaunchGate();
        Assert.Throws<ArgumentException>(() => gate.Acquire(null!));
        Assert.Throws<ArgumentException>(() => gate.Acquire(""));
        Assert.Throws<ArgumentException>(() => gate.Acquire("   "));
        Assert.Throws<ArgumentException>(() => gate.Acquire("\t\n"));
    }

    // ── concurrency proof ───────────────────────────────────────────

    [Fact]
    public async Task ConcurrentAcquires_ExactlyOneWins_OthersFailFast()
    {
        // The whole point of the gate. Spin up N concurrent attempts
        // for the same profile, gated on a Barrier so they all hit
        // Acquire at the same instant. EXACTLY ONE must win; the
        // others must throw ProfileBusyException without blocking.
        const int parallelism = 16;
        var gate    = new ProfileLaunchGate();
        var barrier = new Barrier(parallelism);
        var winners = 0;
        var busies  = 0;
        var others  = 0;

        async Task TryAcquire()
        {
            // Move to thread-pool thread; SignalAndWait BLOCKS until
            // all participants arrive, ensuring max contention at the
            // Acquire call site.
            await Task.Yield();
            barrier.SignalAndWait();
            try
            {
                using var d = gate.Acquire("contended");
                Interlocked.Increment(ref winners);
                // Hold briefly so other Acquires definitely see it
                // as held. Without the delay, on a 1-CPU runner the
                // first holder could release before the second tries.
                await Task.Delay(20);
            }
            catch (ProfileBusyException)
            {
                Interlocked.Increment(ref busies);
            }
            catch
            {
                Interlocked.Increment(ref others);
            }
        }

        var tasks = new Task[parallelism];
        for (var i = 0; i < parallelism; i++)
            tasks[i] = Task.Run(TryAcquire);
        await Task.WhenAll(tasks);

        Assert.Equal(1, winners);
        Assert.Equal(parallelism - 1, busies);
        Assert.Equal(0, others);
    }

    [Fact]
    public async Task SerialAcquiresOnSameProfile_AllSucceed()
    {
        // Sequential pattern — common case: scheduler fires every
        // 6 minutes, each launch is fully disposed before the next
        // one starts. Must NOT throw ProfileBusyException.
        var gate = new ProfileLaunchGate();
        for (var i = 0; i < 20; i++)
        {
            using var d = gate.Acquire("serial");
            await Task.Yield();
        }
    }

    // ── exception propagation under contention ──────────────────────

    [Fact]
    public void Acquire_ExceptionInsideUsing_StillReleasesGate()
    {
        // The `using` pattern guarantees Dispose runs even when the
        // caller throws. Validate by throwing mid-block, then proving
        // a subsequent Acquire succeeds. If the gate were "stuck",
        // the second Acquire would throw ProfileBusyException — that
        // would mask the real bug, so it's worth pinning explicitly.
        var gate = new ProfileLaunchGate();

        // Cast to Action — without it, xUnit's overload resolution
        // prefers the obsolete `Func<Task>` overload (the lambda has
        // a `throw` which the compiler considers ambiguous between
        // Action and Func<Task> returning never).
        Action tryLaunch = () =>
        {
            using var d = gate.Acquire("p1");
            throw new InvalidOperationException("simulated launch failure");
        };
        Assert.Throws<InvalidOperationException>(tryLaunch);

        using var next = gate.Acquire("p1");
        Assert.True(gate.IsHeld("p1"));
    }
}
