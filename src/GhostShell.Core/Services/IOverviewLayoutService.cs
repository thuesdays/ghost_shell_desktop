// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 34 — per-user Overview widget config. Drives the gear-icon
/// "Configure widgets" menu and the actual render order on the
/// Overview page.
///
/// New widgets register themselves in
/// <see cref="OverviewWidgetCatalog"/>; this service stores the
/// user's enabled / position overrides per widget id. Anything not
/// in the DB falls through to the catalog's <c>DefaultOn</c>.
/// </summary>
public interface IOverviewLayoutService
{
    /// <summary>List every known widget along with the user's saved
    /// state (or catalog defaults). Ordered by saved <c>position</c>
    /// or — when never saved — by catalog <c>DefaultOrder</c>.</summary>
    Task<IReadOnlyList<OverviewWidgetState>> ListAsync(CancellationToken ct = default);

    /// <summary>Persist the full layout in one shot. Used by the
    /// "Configure widgets" dialog when the user clicks Save.</summary>
    Task SaveAsync(IReadOnlyList<OverviewWidgetState> states, CancellationToken ct = default);

    /// <summary>Wipe all overrides — reverts to catalog defaults.</summary>
    Task ResetAsync(CancellationToken ct = default);
}

/// <summary>Static manifest of every Overview widget. Add new widgets
/// here; the gear menu enumerates this list automatically.</summary>
public static class OverviewWidgetCatalog
{
    public const string IdHeroStats    = "hero_stats";
    public const string IdRecentRuns   = "recent_runs";
    public const string IdAdDensity    = "ad_density";
    public const string IdProxySummary = "proxy_summary";
    public const string IdTrafficToday = "traffic_today";

    public static readonly IReadOnlyList<OverviewWidgetDefinition> All = new[]
    {
        new OverviewWidgetDefinition
        {
            Id = IdHeroStats, DisplayName = "Hero stat tiles", Icon = "📊",
            Description = "Five top-of-page KPI tiles (runs, profiles, success rate, …).",
            DefaultOn = true, DefaultOrder = 0,
        },
        new OverviewWidgetDefinition
        {
            Id = IdRecentRuns, DisplayName = "Recent runs", Icon = "🕒",
            Description = "Last eight runs with profile / status / start time.",
            DefaultOn = true, DefaultOrder = 1,
        },
        new OverviewWidgetDefinition
        {
            Id = IdProxySummary, DisplayName = "Proxy health summary", Icon = "🌐",
            Description = "Health badges for the loaded proxy pool.",
            DefaultOn = true, DefaultOrder = 2,
        },
        new OverviewWidgetDefinition
        {
            Id = IdTrafficToday, DisplayName = "Traffic today", Icon = "📈",
            Description = "Bytes consumed by every profile in the last 24 h.",
            DefaultOn = true, DefaultOrder = 3,
        },
        new OverviewWidgetDefinition
        {
            Id = IdAdDensity, DisplayName = "Ad density trend", Icon = "🪩",
            Description = "Effectiveness of warmup & click algos — ads/query, top profiles, top IPs, 14-day trend.",
            DefaultOn = false, DefaultOrder = 4,
        },
    };

    public static OverviewWidgetDefinition? Find(string id)
        => All.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
}
