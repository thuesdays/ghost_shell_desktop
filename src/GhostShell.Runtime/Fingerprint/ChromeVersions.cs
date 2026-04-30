// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Runtime.Fingerprint;

/// <summary>
/// Curated list of Chromium major versions our patched binary can
/// pretend to be, with weights skewed toward the most common version
/// we've seen in real browsing data. The weights matter: a profile
/// that always reports Chromium 149 against a backdrop where most
/// real users are on 147–148 is itself a fingerprint.
///
/// To roll: add a new entry at the top, give it the heaviest weight,
/// step down older entries. The patched Chromium binary itself is
/// 149 — these are spoof targets.
/// </summary>
public static class ChromeVersions
{
    /// <summary>(major, full, weight). Weights are uniform-arbitrary —
    /// the absolute scale doesn't matter, only the ratio.</summary>
    public static readonly IReadOnlyList<(string Major, string Full, int Weight)> Versions =
    new (string, string, int)[]
    {
        ("147", "147.0.7780.88",  55),
        ("146", "146.0.7715.130", 25),
        ("145", "145.0.7665.162", 12),
        ("144", "144.0.7615.185",  5),
        ("143", "143.0.7556.210",  3),
    };

    /// <summary>
    /// Pick a version weighted by <see cref="Versions"/>. Bounds are
    /// inclusive on both ends; pass null to skip that bound. The
    /// supplied <paramref name="rng"/> is the per-profile deterministic
    /// RNG so two launches of the same profile pick the same version.
    /// </summary>
    public static (string Major, string Full) PickWeighted(
        Random rng, string? minMajor = null, string? maxMajor = null)
    {
        var candidates = Versions
            .Where(v => InBounds(v.Major, minMajor, maxMajor))
            .ToList();
        if (candidates.Count == 0) candidates = Versions.ToList();

        var totalWeight = candidates.Sum(c => c.Weight);
        var roll = rng.Next(totalWeight);
        var running = 0;
        foreach (var (m, f, w) in candidates)
        {
            running += w;
            if (roll < running) return (m, f);
        }
        // Fall-through impossible but compiler can't prove it.
        var (mm, ff, _) = candidates[^1];
        return (mm, ff);
    }

    private static bool InBounds(string major, string? min, string? max)
    {
        if (!int.TryParse(major, out var v)) return true;
        if (min is not null && int.TryParse(min, out var lo) && v < lo) return false;
        if (max is not null && int.TryParse(max, out var hi) && v > hi) return false;
        return true;
    }
}
