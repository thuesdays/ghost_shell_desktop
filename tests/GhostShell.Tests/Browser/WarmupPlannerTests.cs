// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Runtime.Browser;
using Xunit;

namespace GhostShell.Tests.Browser;

/// <summary>Feature #4 — autonomous warmup selection.</summary>
public sealed class WarmupPlannerTests
{
    private static readonly AutoWarmupConfig Cfg =
        new(Enabled: true, IntervalMinutes: 30, TrustThreshold: 50, MinIntervalDays: 1.0, PresetId: "organic");

    private static ProfileMaturityInput P(
        string name, double ageDays, int warmups = 0, double sinceWarmup = -1, bool active = false)
        => new(name, ageDays, warmups, sinceWarmup, RecentRunCount: 0, IsActive: active, HasProxy: true);

    [Fact]
    public void PicksColdestEligible()
    {
        var plan = WarmupPlanner.PlanNext(new[]
        {
            P("warm", ageDays: 10, warmups: 1),  // higher score
            P("cold", ageDays: 0),               // coldest
        }, Cfg);
        Assert.NotNull(plan);
        Assert.Equal("cold", plan!.ProfileName);
    }

    [Fact]
    public void SkipsActiveProfiles()
    {
        var plan = WarmupPlanner.PlanNext(new[] { P("cold", ageDays: 0, active: true) }, Cfg);
        Assert.Null(plan);
    }

    [Fact]
    public void SkipsRecentlyWarmed()
    {
        // Warmed 0.2 days ago (< MinIntervalDays=1) → skip.
        var plan = WarmupPlanner.PlanNext(new[] { P("c", ageDays: 0, sinceWarmup: 0.2) }, Cfg);
        Assert.Null(plan);
    }

    [Fact]
    public void SkipsProfilesAboveTrustThreshold()
    {
        // Aged + warmed → score >= 50 → not eligible.
        var plan = WarmupPlanner.PlanNext(new[] { P("mature", ageDays: 30, warmups: 4, sinceWarmup: 2) }, Cfg);
        Assert.Null(plan);
    }

    [Fact]
    public void Disabled_ReturnsNull()
    {
        var off = Cfg with { Enabled = false };
        var plan = WarmupPlanner.PlanNext(new[] { P("cold", ageDays: 0) }, off);
        Assert.Null(plan);
    }

    [Fact]
    public void EmptyList_ReturnsNull()
    {
        Assert.Null(WarmupPlanner.PlanNext(System.Array.Empty<ProfileMaturityInput>(), Cfg));
    }

    [Fact]
    public void Decision_CarriesRampedSiteCount()
    {
        var plan = WarmupPlanner.PlanNext(new[] { P("cold", ageDays: 0) }, Cfg);
        Assert.NotNull(plan);
        Assert.Equal("organic", plan!.PresetId);
        Assert.True(plan.SiteCount >= 4);
    }
}
