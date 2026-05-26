// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using GhostShell.Core.Common;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Phase 71oo — per-profile launch gate. Prevents two concurrent
/// <see cref="BrowserLauncher.LaunchAsync"/> calls for the same profile
/// from racing through the preflight orphan-reaper.
///
/// The bug it solves
/// ─────────────────
/// <c>LaunchPreflight.Run</c> kills any <c>chrome.exe</c> whose
/// <c>--user-data-dir</c> matches the profile. That's correct for an
/// actual orphan from a previously-crashed run, but catastrophic when
/// it kills a <c>chrome.exe</c> spawned by another in-flight
/// <see cref="BrowserLauncher.LaunchAsync"/> call: chrome.exe never
/// reaches the DevTools-port-registered state, and chromedriver
/// surfaces it as
/// <c>"session not created: Chrome failed to start: crashed
/// (DevToolsActivePort file doesn't exist)"</c>. Observed when the
/// user clicks Run-now on a schedule that the RunnerHost background
/// loop also fires within the same second.
///
/// Design notes
/// ────────────
/// • Per-profile <see cref="SemaphoreSlim"/>(1, 1), keyed
///   case-insensitively (matches AppPaths.ProfileDir conventions).
/// • Non-blocking <see cref="SemaphoreSlim.Wait(int)"/> with timeout 0:
///   second caller fails FAST with <see cref="ProfileBusyException"/>
///   — no waste on locator / proxy / forwarder work it would
///   immediately have to throw away.
/// • Gate is released as soon as <c>LaunchAsync</c> returns (success
///   OR failure) via <c>using var gate = ...</c>. The session it
///   produced continues running unchanged — gate scope is JUST the
///   launch initialisation, not the session lifetime.
/// • Releaser is idempotent (atomic swap-to-null) so a double-Dispose
///   on the same handle can't accidentally over-release a semaphore
///   to count &gt; 1 (which would silently break the gate for the
///   next caller).
/// • SemaphoreSlim instances live for the app lifetime. Each is
///   tiny (~100 B), and the profile-name set is bounded (≪1000 in
///   any realistic setup), so we don't bother evicting them.
///
/// Thread safety
/// ─────────────
/// <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/> may call
/// its factory more than once for the same key under contention, but
/// only one of those <see cref="SemaphoreSlim"/> instances becomes
/// the dictionary value. The discarded ones are GC'd. This is fine
/// — discarded semaphores have never been acquired by anyone.
/// </summary>
public sealed class ProfileLaunchGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Try to acquire the launch gate for <paramref name="profileName"/>.
    /// Returns an <see cref="IDisposable"/> that releases the gate on
    /// <see cref="IDisposable.Dispose"/>. Throws
    /// <see cref="ProfileBusyException"/> immediately (no wait) if
    /// another launch is already holding the gate.
    /// </summary>
    public IDisposable Acquire(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException(
                "Profile name is required.", nameof(profileName));

        var sem = _gates.GetOrAdd(profileName, _ => new SemaphoreSlim(1, 1));

        // Wait(0) returns immediately — true if we got the semaphore,
        // false if it was already held. No blocking; the race-loser
        // is told to back off and try again later.
        if (!sem.Wait(0))
            throw new ProfileBusyException(profileName);

        return new Releaser(sem);
    }

    /// <summary>
    /// Diagnostic: whether the gate is currently held for the given
    /// profile. Inherently racy — exists only for tests and log lines.
    /// </summary>
    public bool IsHeld(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return false;
        return _gates.TryGetValue(profileName, out var sem)
            && sem.CurrentCount == 0;
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _sem;

        public Releaser(SemaphoreSlim sem) => _sem = sem;

        public void Dispose()
        {
            // Atomic swap-to-null guarantees a double-Dispose can't
            // call Release twice — that would push CurrentCount above
            // the max of 1 and throw SemaphoreFullException AT THAT
            // POINT, but worse, the gate would silently allow two
            // concurrent acquires on the next round. Catch the bug
            // here instead.
            var sem = Interlocked.Exchange(ref _sem, null);
            sem?.Release();
        }
    }
}
