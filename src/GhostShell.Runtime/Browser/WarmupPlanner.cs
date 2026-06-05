// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Runtime.Browser;

/// <summary>Knobs for the autonomous warm-up scheduler.</summary>
public sealed record AutoWarmupConfig(
    bool Enabled,
    int IntervalMinutes,
    int TrustThreshold,       // warm profiles scoring below this
    double MinIntervalDays,   // don't re-warm a profile more often than this
    string PresetId);

/// <summary>A decision to warm one profile now.</summary>
public sealed record WarmupDecision(string ProfileName, string PresetId, int SiteCount);

/// <summary>
/// Pure scheduler brain. Given the current maturity state of every profile and
/// the config, decides which single profile (if any) to warm next. Picks the
/// coldest eligible profile — the one most in need of aging — while skipping
/// anything active or warmed too recently. No I/O, fully testable.
/// </summary>
public static class WarmupPlanner
{
    public static WarmupDecision? PlanNext(
        IReadOnlyList<ProfileMaturityInput> profiles, AutoWarmupConfig cfg)
    {
        if (!cfg.Enabled || profiles.Count == 0) return null;

        ProfileMaturityInput? best = null;
        ProfileTrustReport? bestReport = null;

        foreach (var p in profiles)
        {
            if (p.IsActive) continue;                                   // never double-launch
            if (p.DaysSinceLastWarmup >= 0 && p.DaysSinceLastWarmup < cfg.MinIntervalDays)
                continue;                                              // warmed recently
            var r = ProfileTrustScore.Evaluate(p);
            if (r.Score >= cfg.TrustThreshold) continue;               // already mature enough

            if (bestReport is null || r.Score < bestReport.Score)
            {
                best = p;
                bestReport = r;
            }
        }

        if (best is null || bestReport is null) return null;
        return new WarmupDecision(best.ProfileName, cfg.PresetId, bestReport.RecommendedWarmupSites);
    }
}
