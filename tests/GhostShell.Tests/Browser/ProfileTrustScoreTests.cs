// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Runtime.Browser;
using Xunit;

namespace GhostShell.Tests.Browser;

/// <summary>Feature #4 — profile maturity scoring + warmup ramp.</summary>
public sealed class ProfileTrustScoreTests
{
    private static ProfileMaturityInput M(
        double ageDays = 0, int warmups = 0, double sinceWarmup = -1,
        int runs = 0, bool active = false)
        => new("p", ageDays, warmups, sinceWarmup, runs, active, HasProxy: true);

    [Fact]
    public void FreshProfile_IsCold()
    {
        var r = ProfileTrustScore.Evaluate(M(ageDays: 0));
        Assert.Equal(TrustBand.Cold, r.Band);
        Assert.True(r.Score < 30);
    }

    [Fact]
    public void AgedAndWarmed_IsTrusted()
    {
        var r = ProfileTrustScore.Evaluate(M(ageDays: 30, warmups: 4, sinceWarmup: 2, runs: 10));
        Assert.Equal(TrustBand.Trusted, r.Band);
        Assert.True(r.Score >= 70);
    }

    [Fact]
    public void Score_IsMonotonicInAge()
    {
        var young = ProfileTrustScore.Evaluate(M(ageDays: 1)).Score;
        var older = ProfileTrustScore.Evaluate(M(ageDays: 15)).Score;
        Assert.True(older > young);
    }

    [Fact]
    public void Score_ClampedTo100()
    {
        var r = ProfileTrustScore.Evaluate(M(ageDays: 999, warmups: 99, sinceWarmup: 1, runs: 999));
        Assert.True(r.Score <= 100);
    }

    [Fact]
    public void NeverWarmed_GetsGentleFirstSession()
    {
        Assert.Equal(4, ProfileTrustScore.RecommendedWarmupSites(M(ageDays: 40, sinceWarmup: -1)));
    }

    [Fact]
    public void RampGrowsWithAge_Bounded()
    {
        var early = ProfileTrustScore.RecommendedWarmupSites(M(ageDays: 3, sinceWarmup: 1));
        var later = ProfileTrustScore.RecommendedWarmupSites(M(ageDays: 30, sinceWarmup: 1));
        Assert.True(later >= early);
        Assert.InRange(ProfileTrustScore.RecommendedWarmupSites(M(ageDays: 999, sinceWarmup: 1)), 4, 12);
    }
}
