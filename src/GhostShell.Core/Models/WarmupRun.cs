// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// One row of the <c>warmup_runs</c> table — a single warmup
/// invocation against a single profile.
///
/// Lifecycle states (<see cref="Status"/>):
/// <list type="bullet">
///   <item><c>running</c> — engine is mid-flight, <see cref="FinishedAt"/> is null.</item>
///   <item><c>ok</c>      — every planned site visited successfully.</item>
///   <item><c>partial</c> — at least one site visited but not all.</item>
///   <item><c>failed</c>  — zero sites succeeded (browser launch failure or
///                         per-site errors across the board).</item>
/// </list>
///
/// Triggers describe who initiated the run:
/// <list type="bullet">
///   <item><c>manual</c>           — user clicked "Run warmup now".</item>
///   <item><c>scheduled</c>        — fired by the scheduler (Phase 5).</item>
///   <item><c>auto_quality</c>     — quality monitor saw a captcha-rate
///                                  spike and self-healed (Phase 6+).</item>
///   <item><c>auto_before_run</c>  — pre-run warmup gate (Phase 6+).</item>
/// </list>
///
/// We keep the trigger column free-form (TEXT, no enum constraint)
/// so future trigger kinds don't force a migration.
/// </summary>
public sealed record WarmupRun
{
    public long Id { get; init; }

    public required string ProfileName { get; init; }

    /// <summary>UTC ISO-8601. Set at INSERT time.</summary>
    public required DateTime StartedAt { get; init; }

    /// <summary>UTC ISO-8601 or null while still running.</summary>
    public DateTime? FinishedAt { get; init; }

    /// <summary>
    /// Preset id (e.g. "general", "commerce_ua"). Free-form string
    /// rather than enum — the DB will keep historic rows for presets
    /// that have since been renamed or removed, and we still want to
    /// display them in the history table.
    /// </summary>
    public string? Preset { get; init; }

    public int SitesPlanned   { get; init; }
    public int SitesVisited   { get; init; }
    public int SitesSucceeded { get; init; }

    /// <summary>Wall-clock duration; null until <c>finished_at</c> is set.</summary>
    public double? DurationSec { get; init; }

    public required string Status  { get; init; }
    public required string Trigger { get; init; }

    /// <summary>Free-form note (error fragment, "1 site timeout", etc.).</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// JSON-serialised array of <see cref="WarmupSiteResult"/>. Lazy:
    /// the list page doesn't deserialise this — only the row-expand
    /// drilldown does. Length cap ≈ 50 entries × ~250 bytes each ≈ 12KB.
    /// </summary>
    public string SitesLogJson { get; init; } = "[]";
}

/// <summary>
/// Per-site record inside <see cref="WarmupRun.SitesLogJson"/>.
/// Stored as JSON for forward-compatibility — adding a field doesn't
/// break old rows because the deserialiser ignores unknown keys.
/// </summary>
public sealed record WarmupSiteResult
{
    public required string Url { get; init; }
    public string? Topic { get; init; }
    public bool Ok { get; init; }
    public int DurationMs { get; init; }
    public int CookiesBefore { get; init; }
    public int CookiesAfter { get; init; }
    public bool ConsentClicked { get; init; }
    public string? Error { get; init; }
}
