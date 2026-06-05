// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Pure exponential-backoff-with-jitter policy for the <c>retry</c> script step.
/// Decorrelated jitter (delay drawn from [base·2^n / 2, base·2^n], capped)
/// avoids the thundering-herd / fixed-cadence pattern a flat retry produces —
/// which also reads more human than re-hitting a failing action at an exact
/// interval. No I/O — fully unit-testable with an injected RNG.
/// </summary>
public static class RetryPolicy
{
    /// <summary>Delay (ms) to wait BEFORE retry <paramref name="attempt"/>
    /// (1-based: attempt 1 is the first retry after the initial try).</summary>
    public static int BackoffMs(int attempt, int baseMs, int maxMs, Random rng)
    {
        if (attempt < 1) attempt = 1;
        if (baseMs < 0) baseMs = 0;
        if (maxMs < baseMs) maxMs = baseMs;

        // base * 2^(attempt-1), guarded against overflow, capped at maxMs.
        var shift = Math.Min(attempt - 1, 20);
        var raw = (long)baseMs * (1L << shift);
        var ceiling = (int)Math.Min(raw, maxMs);

        // Decorrelated jitter: anywhere in [ceiling/2, ceiling].
        var lo = ceiling / 2;
        if (ceiling <= 0) return 0;
        return rng.Next(lo, ceiling + 1);
    }
}
