// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>How "aged"/trusted a profile looks. A fresh profile with no
/// history and no Google cookies is what anti-bot stacks challenge first;
/// trust grows with age, successful warmups, and real runs.</summary>
public enum TrustBand
{
    /// <summary>Brand-new / un-aged — needs warming before sensitive work.</summary>
    Cold,
    /// <summary>Has some history — usable, keep aging.</summary>
    Warming,
    /// <summary>Well-aged with organic history — safe for sensitive targets.</summary>
    Trusted,
}

/// <summary>Inputs to the trust score — all already-available, cheap signals.</summary>
public sealed record ProfileMaturityInput(
    string ProfileName,
    double AgeDays,
    int SuccessfulWarmups,
    double DaysSinceLastWarmup, // negative = never warmed
    int RecentRunCount,
    bool IsActive,
    bool HasProxy);

/// <summary>Computed maturity for a profile.</summary>
public sealed record ProfileTrustReport(
    string ProfileName,
    int Score,                       // 0 cold … 100 trusted
    TrustBand Band,
    int RecommendedWarmupSites,
    string Reason);
