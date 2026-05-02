// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 34 — Ad density trend service for the Overview widget.
/// Reads from <c>runs.total_ads / total_queries</c> (per-run
/// counters) and <c>action_events</c> (CTR proxy).
///
/// Rationale: ads-per-query is a proxy metric for warmup quality. A
/// fresh / clean profile sees ~0.5-1 ads on a competitive search
/// term; a "burned" profile barely sees any (ad networks suspect
/// it's a bot). Tracking this over time lets the user spot when a
/// click algo or warmup tweak hurt yield.
/// </summary>
public interface IAdDensityService
{
    /// <summary>Build the full Overview widget payload (KPI numbers,
    /// 14-day daily breakdown, top-profile + top-IP tables).</summary>
    Task<AdDensitySummary> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>Append one action_events row. Called by the script
    /// runner when a step finishes (ran / skipped / error). Used to
    /// compute the CTR proxy + skip-reason breakdown.</summary>
    Task RecordActionAsync(ActionEvent ev, CancellationToken ct = default);
}
