// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Phase 34 — domain-list categories used by the script engine.
/// One row per (kind, domain) in the <c>domain_lists</c> table.
///
/// • <see cref="My"/>     — domains the user owns. Click-ad steps
///                          skip these so the user doesn't burn
///                          their own ad budget.
/// • <see cref="Target"/> — competitor domains the user wants to
///                          click specifically (longer dwell, opt-in
///                          probability boost).
/// • <see cref="Block"/>  — aggregators / news sites the user wants
///                          to ignore entirely. Doesn't appear in
///                          competitor-reports either.
/// </summary>
public enum DomainListKind
{
    My     = 0,
    Target = 1,
    Block  = 2,
}

/// <summary>One row in the <c>domain_lists</c> table.</summary>
public sealed record DomainListEntry
{
    public long           Id        { get; init; }
    public DomainListKind Kind      { get; init; }
    public required string Domain   { get; init; }
    public string?        Note      { get; init; }
    public DateTime       CreatedAt { get; init; }
}

/// <summary>One observation of a competing advertiser. Inserted by
/// the script runner whenever it parses a sponsored ad slot from a
/// search-result page.</summary>
public sealed record CompetitorRecord
{
    public long      Id          { get; init; }
    public long?     RunId       { get; init; }
    public string?   ProfileName { get; init; }
    public DateTime  CapturedAt  { get; init; }
    public required string Query { get; init; }
    public required string Domain { get; init; }
    public string?   AdTitle     { get; init; }
    public string?   DisplayUrl  { get; init; }
    public string?   CleanUrl    { get; init; }
    public string?   ClickUrl    { get; init; }
}

/// <summary>One row on the Competitors leaderboard — one domain
/// aggregated across the selected period.</summary>
public sealed record CompetitorLeaderRow
{
    public required string Domain   { get; init; }
    public int      Mentions       { get; init; }     // count in current period
    public int      MentionsPrev   { get; init; }     // count in prior period (for "trend" arrow)
    public int      QueriesCount   { get; init; }     // distinct queries
    public int      ClicksCount    { get; init; }     // distinct click events
    public DateTime LastSeen       { get; init; }
    public bool     IsNew          { get; init; }     // first seen inside the period
}

/// <summary>One sample on the Competitors volume-trend line chart.</summary>
public sealed record CompetitorTrendPoint
{
    public DateTime Date { get; init; }
    public required string Domain { get; init; }
    public int      Mentions { get; init; }
}

/// <summary>One step outcome from a script run. Records WHY the step
/// did/didn't fire so the dashboard can show skip-reason breakdowns
/// and verify the domain-list rules are working.</summary>
public sealed record ActionEvent
{
    public long      Id           { get; init; }
    public long?     RunId        { get; init; }
    public required string ProfileName { get; init; }
    public DateTime  CapturedAt   { get; init; }
    public string?   Query        { get; init; }
    public string?   AdDomain     { get; init; }
    /// <summary>One of: target | my_domain | competitor | unknown.</summary>
    public string?   AdClass      { get; init; }
    /// <summary>e.g. click_ad, dwell, scroll.</summary>
    public required string ActionType { get; init; }
    /// <summary>One of: ran | skipped | error.</summary>
    public required string Outcome { get; init; }
    /// <summary>Reason the step was skipped, if Outcome=skipped.
    /// One of: my_domain | target | not_target | not_my_domain |
    /// blocked | probability | disabled.</summary>
    public string?   SkipReason   { get; init; }
    public double?   DurationSec  { get; init; }
    public string?   Error        { get; init; }
}

/// <summary>Aggregated ad-density snapshot for the Overview widget.</summary>
public sealed record AdDensitySummary
{
    public double AvgAdsPerQuery7d  { get; init; }
    public double AvgAdsPerQuery24h { get; init; }
    public double DeltaPct7dPrev    { get; init; }   // % change vs previous 7d
    public int    TotalRuns7d       { get; init; }
    public int    TotalAds7d        { get; init; }
    public int    TotalQueries7d    { get; init; }
    public int    TotalClicks7d     { get; init; }
    public double Ctr7d             { get; init; }   // clicks / ads_seen
    public IReadOnlyList<AdDensityDailyPoint> Daily { get; init; } = Array.Empty<AdDensityDailyPoint>();
    public IReadOnlyList<AdDensityProfileRow> PerProfile { get; init; } = Array.Empty<AdDensityProfileRow>();
    public IReadOnlyList<AdDensityIpRow>      PerIp      { get; init; } = Array.Empty<AdDensityIpRow>();
}

public sealed record AdDensityDailyPoint
{
    public DateOnly Date         { get; init; }
    public int      Runs         { get; init; }
    public int      Ads          { get; init; }
    public int      Queries      { get; init; }
    public double   AdsPerQuery  { get; init; }
}

public sealed record AdDensityProfileRow
{
    public required string ProfileName { get; init; }
    public int    Runs         { get; init; }
    public double AdsPerQuery  { get; init; }
}

public sealed record AdDensityIpRow
{
    public required string Ip { get; init; }
    public int    Runs         { get; init; }
    public double AdsPerQuery  { get; init; }
}

/// <summary>One configured Overview widget. The Overview page reads
/// this list (ordered by <see cref="Position"/>) and renders only
/// widgets whose <see cref="Enabled"/> is true.</summary>
public sealed record OverviewWidgetState
{
    public required string WidgetId { get; init; }
    public bool   Enabled  { get; init; }
    public int    Position { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>Static manifest entry for one Overview widget. The
/// dashboard's "Configure widgets" gear menu enumerates these so
/// adding a new widget is a one-line registration.</summary>
public sealed record OverviewWidgetDefinition
{
    public required string Id           { get; init; }
    public required string DisplayName  { get; init; }
    public required string Icon         { get; init; }   // emoji
    public required string Description  { get; init; }
    public bool            DefaultOn    { get; init; }   // false for ad_density per user request
    public int             DefaultOrder { get; init; }   // ascending
}
