// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Runtime.Fingerprint;
using Xunit;

namespace GhostShell.Tests.Fingerprint;

public sealed class ChromeVersionsTests
{
    [Fact]
    public void PickWeighted_AlwaysReturnsKnownVersion()
    {
        var rng = new Random(42);
        for (var i = 0; i < 1000; i++)
        {
            var (major, full) = ChromeVersions.PickWeighted(rng);
            Assert.Contains(ChromeVersions.Versions, v => v.Major == major && v.Full == full);
        }
    }

    [Fact]
    public void PickWeighted_RespectsMinBound()
    {
        var rng = new Random(0);
        for (var i = 0; i < 200; i++)
        {
            var (major, _) = ChromeVersions.PickWeighted(rng, minMajor: "146");
            Assert.True(int.Parse(major) >= 146);
        }
    }

    [Fact]
    public void PickWeighted_RespectsMaxBound()
    {
        // Upper bound inside the curated range (146-149 post-PCP-04):
        // capping at 147 must exclude 148/149.
        var rng = new Random(0);
        for (var i = 0; i < 200; i++)
        {
            var (major, _) = ChromeVersions.PickWeighted(rng, maxMajor: "147");
            Assert.True(int.Parse(major) <= 147);
        }
    }

    [Fact]
    public void PickWeighted_ImpossibleBounds_FallsBackGracefully()
    {
        // min > max → no candidates fit. Per the implementation,
        // we fall back to the full set instead of throwing.
        var rng = new Random(0);
        var (major, _) = ChromeVersions.PickWeighted(rng,
            minMajor: "200", maxMajor: "100");
        Assert.False(string.IsNullOrEmpty(major));
    }

    [Fact]
    public void PickWeighted_DistributionFavorsHeavierWeights()
    {
        // Run 10000 picks with no bounds. Post-PCP-04 the engine major
        // (149) carries the heaviest weight (52 of 100) so it must
        // dominate; expect ~52% of picks. Allow generous slop.
        var rng = new Random(123);
        var counts = new Dictionary<string, int>();
        for (var i = 0; i < 10000; i++)
        {
            var (major, _) = ChromeVersions.PickWeighted(rng);
            counts[major] = counts.GetValueOrDefault(major) + 1;
        }
        Assert.True(counts.GetValueOrDefault("149") > 4500,
            $"149 picked only {counts.GetValueOrDefault("149")} / 10000");
    }
}
