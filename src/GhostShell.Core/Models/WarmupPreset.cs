// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// One site inside a warmup preset. Dwell range is a (min, max) tuple;
/// the engine rolls a uniform-random value inside it per visit so two
/// runs of the same preset don't generate identical timing fingerprints.
/// </summary>
public sealed record WarmupSite
{
    public required string Url { get; init; }
    public required string Topic { get; init; }

    /// <summary>Lower bound for dwell (seconds).</summary>
    public required int DwellMinSec { get; init; }
    /// <summary>Upper bound for dwell (seconds), inclusive.</summary>
    public required int DwellMaxSec { get; init; }

    /// <summary>
    /// Whether the engine should scroll during dwell. Off for landing
    /// pages where scroll would actually steer to a different sub-page.
    /// </summary>
    public bool Scroll { get; init; } = true;

    /// <summary>
    /// ISO-2 country tags this site is appropriate for. <c>"*"</c> means
    /// universal. Used by the engine's geo-filter so a UA-targeted
    /// profile doesn't visit US-only sites and confuse Google's
    /// locale-inference heuristic.
    /// </summary>
    public IReadOnlyList<string> Countries { get; init; } = ["*"];

    /// <summary>Optional internal note explaining why the site is in the preset.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// One preset — a labelled bucket of <see cref="WarmupSite"/>s with
/// a default site count. The Sessions page shows these as cards in
/// the Warmup-robot tab.
/// </summary>
public sealed record WarmupPresetDef
{
    public required string Id    { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<WarmupSite> Sites { get; init; }

    /// <summary>Default value for the "Site count" input on the UI.</summary>
    public int DefaultSiteCount => Math.Min(Sites.Count, 8);

    public int SiteCount => Sites.Count;
}
