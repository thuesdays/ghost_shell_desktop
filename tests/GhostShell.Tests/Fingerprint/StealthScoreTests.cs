// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System;
using GhostShell.Core.Models;
using GhostShell.Runtime.Fingerprint;
using Xunit;

namespace GhostShell.Tests.Fingerprint;

/// <summary>Feature #5 — unified per-profile stealth score.</summary>
public sealed class StealthScoreTests
{
    private static FingerprintScore Coh(int overall, int critical = 0, int warnings = 0)
        => new()
        {
            Overall = overall,
            Label = overall >= 85 ? "EXCELLENT" : overall >= 75 ? "OK" : "RISKY",
            CriticalIssues = critical,
            Warnings = warnings,
            Checks = Array.Empty<FingerprintCheck>(),
        };

    private static SelfCheckResult Run(int score, bool leaked = false)
        => new()
        {
            ProfileName = "p",
            RanAt = DateTime.UnixEpoch,
            Score = score,
            WebRtcLeaked = leaked,
        };

    [Fact]
    public void CoherenceOnly_UsesCoherenceScore()
    {
        var r = StealthScore.Combine(Coh(90), runtime: null);
        Assert.Equal(90, r.Score);
        Assert.Equal(StealthBand.Strong, r.Band);
        Assert.Null(r.RuntimeScore);
        Assert.Contains("not runtime-verified", r.Summary);
    }

    [Fact]
    public void Blends50_50_WhenRuntimePresent()
    {
        var r = StealthScore.Combine(Coh(90), Run(70));
        Assert.Equal(80, r.Score);
        Assert.Equal(StealthBand.Fair, r.Band);
        Assert.Equal(70, r.RuntimeScore);
    }

    [Fact]
    public void WebRtcLeak_NeverStrong()
    {
        var r = StealthScore.Combine(Coh(95), Run(95, leaked: true));
        Assert.True(r.Score >= 85);
        Assert.Equal(StealthBand.Fair, r.Band); // capped down from Strong
        Assert.Contains(r.TopIssues, i => i.Contains("WebRTC"));
    }

    [Fact]
    public void Issues_ReflectCoherenceCounts()
    {
        var r = StealthScore.Combine(Coh(60, critical: 2, warnings: 3), runtime: null);
        Assert.Contains(r.TopIssues, i => i.Contains("2 critical"));
        Assert.Contains(r.TopIssues, i => i.Contains("3 coherence warning"));
    }

    [Fact]
    public void LowRuntime_AddsIssue()
    {
        var r = StealthScore.Combine(Coh(90), Run(40));
        Assert.Contains(r.TopIssues, i => i.Contains("runtime self-check low"));
    }

    [Fact]
    public void Bands_FollowScore()
    {
        Assert.Equal(StealthBand.Weak, StealthScore.Combine(Coh(50), null).Band);
        Assert.Equal(StealthBand.Fair, StealthScore.Combine(Coh(70), null).Band);
        Assert.Equal(StealthBand.Strong, StealthScore.Combine(Coh(88), null).Band);
    }
}
