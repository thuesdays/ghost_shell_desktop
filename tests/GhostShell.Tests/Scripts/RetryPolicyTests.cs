// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Runtime.Scripts;
using Xunit;

namespace GhostShell.Tests.Scripts;

/// <summary>Feature #7 — retry backoff math.</summary>
public sealed class RetryPolicyTests
{
    [Fact]
    public void Backoff_StaysWithinDecorrelatedJitterBand()
    {
        var rng = new Random(1);
        // attempt 3: ceiling = min(1000*2^2, 60000) = 4000 → [2000,4000].
        for (var i = 0; i < 1000; i++)
        {
            var d = RetryPolicy.BackoffMs(3, 1000, 60000, rng);
            Assert.InRange(d, 2000, 4000);
        }
    }

    [Fact]
    public void Backoff_GrowsWithAttempt()
    {
        var rng = new Random(2);
        // Compare ceilings: a1≤1000, a2≤2000, a3≤4000. Upper bound grows.
        var maxA1 = 0; var maxA3 = 0;
        for (var i = 0; i < 500; i++)
        {
            maxA1 = Math.Max(maxA1, RetryPolicy.BackoffMs(1, 1000, 60000, rng));
            maxA3 = Math.Max(maxA3, RetryPolicy.BackoffMs(3, 1000, 60000, rng));
        }
        Assert.True(maxA3 > maxA1);
    }

    [Fact]
    public void Backoff_IsCapped()
    {
        var rng = new Random(3);
        for (var i = 0; i < 1000; i++)
        {
            var d = RetryPolicy.BackoffMs(20, 1000, 5000, rng);
            Assert.InRange(d, 2500, 5000); // ceiling capped at 5000 → [2500,5000]
        }
    }

    [Fact]
    public void Backoff_HandlesDegenerateInputs()
    {
        var rng = new Random(4);
        Assert.Equal(0, RetryPolicy.BackoffMs(1, 0, 0, rng));        // base 0
        Assert.InRange(RetryPolicy.BackoffMs(0, 100, 1000, rng), 50, 100); // attempt<1 → treated as 1
        // max<base clamps max UP to base (1000), so ceiling==base → [500,1000].
        Assert.InRange(RetryPolicy.BackoffMs(5, 1000, 500, rng), 500, 1000);
    }

    [Fact]
    public void Backoff_NoOverflow_AtHighAttempt()
    {
        var rng = new Random(5);
        var d = RetryPolicy.BackoffMs(100, 1000, 60000, rng); // shift clamped, no overflow
        Assert.InRange(d, 30000, 60000);
    }
}
