// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Runtime.Scripts;
using Xunit;

namespace GhostShell.Tests.Scripts;

/// <summary>
/// Feature #2 — humanisation core. Pure math, so we can pin endpoint
/// exactness, persona stability, and timing bounds deterministically.
/// </summary>
public sealed class HumanBehaviorTests
{
    // ─── Persona ──────────────────────────────────────────────────

    [Fact]
    public void Persona_IsStable_PerProfileName()
    {
        var a = BehaviorPersona.ForProfile("profile_48");
        var b = BehaviorPersona.ForProfile("profile_48");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Persona_DiffersBetweenProfiles()
    {
        var a = BehaviorPersona.ForProfile("alpha");
        var b = BehaviorPersona.ForProfile("bravo");
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("profile_48")]
    [InlineData("")]
    [InlineData("a-very-long-profile-name-123")]
    public void Persona_WithinDocumentedRanges(string name)
    {
        var p = BehaviorPersona.ForProfile(name);
        Assert.InRange(p.SpeedFactor, 0.7, 1.4);
        Assert.InRange(p.TypoRate, 0.01, 0.05);
        Assert.InRange(p.Curvature, 0.10, 0.22);
    }

    // ─── Timing ───────────────────────────────────────────────────

    [Fact]
    public void NextDelay_StaysWithinBounds()
    {
        var rng = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var d = HumanTiming.NextDelay(rng, 40, 180);
            Assert.InRange(d, 40, 180);
        }
    }

    [Fact]
    public void NextDelay_DegenerateRange_ReturnsMin()
    {
        Assert.Equal(50, HumanTiming.NextDelay(new Random(1), 50, 50));
        Assert.Equal(50, HumanTiming.NextDelay(new Random(1), 50, 30)); // max<min
    }

    [Fact]
    public void Gaussian_IsApproximatelyCentred()
    {
        var rng = new Random(99);
        double sum = 0;
        const int n = 20000;
        for (var i = 0; i < n; i++) sum += HumanTiming.Gaussian(rng, 100, 15);
        var mean = sum / n;
        Assert.InRange(mean, 98, 102); // within 2 of the true mean
    }

    [Fact]
    public void KeystrokeDelay_StaysInHumanBand()
    {
        var rng = new Random(3);
        var persona = BehaviorPersona.Default;
        for (var i = 0; i < 3000; i++)
        {
            var d = HumanTiming.KeystrokeDelayMs(rng, 'a', 'b', persona);
            Assert.InRange(d, 25, 1500);
        }
    }

    // ─── Mouse path ───────────────────────────────────────────────

    [Fact]
    public void MousePath_EndsExactlyOnTarget()
    {
        var rng = new Random(42);
        var path = MousePath.Generate(10, 10, 800, 600, rng, BehaviorPersona.Default);
        Assert.True(path.Count >= 8);
        Assert.Equal(800, path[^1].X, 3);
        Assert.Equal(600, path[^1].Y, 3);
        Assert.All(path, p => Assert.True(p.DelayMs >= 0));
    }

    [Fact]
    public void MousePath_ZeroDistance_DoesNotThrow_AndEndsOnPoint()
    {
        var rng = new Random(5);
        var path = MousePath.Generate(300, 300, 300, 300, rng, BehaviorPersona.Default);
        Assert.NotEmpty(path);
        Assert.Equal(300, path[^1].X, 3);
        Assert.Equal(300, path[^1].Y, 3);
        Assert.All(path, p => Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y)));
    }

    [Fact]
    public void MousePath_TotalTime_IsBoundedAndPositive()
    {
        var rng = new Random(11);
        var path = MousePath.Generate(0, 0, 1200, 900, rng, BehaviorPersona.Default);
        var total = path.Sum(p => p.DelayMs);
        Assert.InRange(total, 100, 1200);
    }

    [Fact]
    public void MousePath_LongMove_HasMorePointsThanShortMove()
    {
        var rng = new Random(13);
        var shortP = MousePath.Generate(0, 0, 30, 20, rng, BehaviorPersona.Default);
        var longP  = MousePath.Generate(0, 0, 1500, 1000, rng, BehaviorPersona.Default);
        Assert.True(longP.Count >= shortP.Count);
    }
}
