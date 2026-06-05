// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Pure, deterministic profile maturity scorer. Turns cheap history signals
/// (age, successful warmups, recent runs) into a 0–100 trust score, a band, and
/// a ramped recommended warmup size. No I/O — fully unit-testable.
/// </summary>
public static class ProfileTrustScore
{
    public static ProfileTrustReport Evaluate(ProfileMaturityInput m)
    {
        var ageDays = Math.Max(0, m.AgeDays);

        // Components (capped) — age dominates early, warmups/runs compound.
        var ageComp   = Math.Min(40, ageDays * 2.0);
        var warmComp  = Math.Min(35, m.SuccessfulWarmups * 12.0);
        var runComp   = Math.Min(25, m.RecentRunCount * 3.0);

        var score = (int)Math.Clamp(ageComp + warmComp + runComp, 0, 100);

        var band = score >= 70 ? TrustBand.Trusted
                 : score >= 30 ? TrustBand.Warming
                 : TrustBand.Cold;

        var sites = RecommendedWarmupSites(m);

        var reason =
            $"age {ageDays:0.#}d (+{ageComp:0}), warmups {m.SuccessfulWarmups} (+{warmComp:0}), " +
            $"runs {m.RecentRunCount} (+{runComp:0})";

        return new ProfileTrustReport(m.ProfileName, score, band, sites, reason);
    }

    /// <summary>Ramped warmup size: a gentle first touch, growing with age so a
    /// profile's activity level rises like a real returning user's.</summary>
    public static int RecommendedWarmupSites(ProfileMaturityInput m)
    {
        if (m.DaysSinceLastWarmup < 0)
            return 4; // never warmed — gentle first session
        var byAge = (int)Math.Min(12, 4 + Math.Max(0, m.AgeDays) / 3.0);
        return Math.Clamp(byAge, 4, 12);
    }
}
