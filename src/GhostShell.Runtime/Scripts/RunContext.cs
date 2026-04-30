// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Mutable runtime state carried through a script execution.
///
///   • Vars         — flat key/value bag the script reads / writes
///                    via save_var / extract_text. Conditions read.
///   • Ads          — list of ad targets parsed by parse_ads.
///                    click_ad and foreach_ad consume entries here.
///   • AdLoopDepth  — re-entrance counter; non-zero means we're
///                    inside a foreach_ad body. parse_ads consults
///                    this to avoid mutating the live ctx.Ads while
///                    a parent loop is iterating it (Phase 14
///                    audit: ctx.Ads concurrent mutation guard).
///   • AdsClicked   — running counter for the script_runs row.
///
/// Loop control (break/continue) used to live here as bool flags;
/// Phase 14 refactored to explicit StepFlow tuple returns inside
/// ScriptRunner so the flow is locally visible at each step.
/// </summary>
public sealed class RunContext
{
    public Dictionary<string, string> Vars { get; } = new(StringComparer.Ordinal);

    /// <summary>Parsed ad records, in DOM order.</summary>
    public List<AdRecord> Ads { get; } = new();

    /// <summary>Non-zero while inside a foreach_ad body.</summary>
    public int AdLoopDepth { get; set; }

    public int AdsClicked { get; set; }

    /// <summary>
    /// Domains the profile owns (set from profile config before the
    /// run starts). Used by per-step <c>SkipOnMyDomain</c> /
    /// <c>OnlyOnMyDomain</c> filters and by the click_ad own-domain
    /// guard. Stored lower-cased and stripped of leading "www." for
    /// host-comparison shortcuts.
    /// </summary>
    public HashSet<string> MyDomains { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Target domains the profile is paid to drive traffic to (also
    /// from profile config). Used by per-step <c>OnlyOnTarget</c> /
    /// <c>SkipOnTarget</c> filters.
    /// </summary>
    public HashSet<string> TargetDomains { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Currently-iterated ad's href, set by foreach_ad on each lap so
    /// per-step domain filters can resolve which ad they're looking at.
    /// Empty outside an ad loop, in which case the filters become
    /// no-ops (web parity: legacy treats the absence of an ad as
    /// "not matching any policy").
    /// </summary>
    public string CurrentAdHref { get; set; } = "";

    /// <summary>Pop a fresh var-name without colliding with existing keys.</summary>
    public string NextVarName(string baseName)
    {
        if (!Vars.ContainsKey(baseName)) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}_{i}";
            if (!Vars.ContainsKey(candidate)) return candidate;
        }
        return baseName + "_x";
    }
}

/// <summary>
/// One ad target produced by parse_ads. The href is the eventual
/// click destination — own-domain guard rejects it if the host
/// matches the page's own host.
/// </summary>
public sealed record AdRecord
{
    public required string Href      { get; init; }
    public string? Title             { get; init; }
    public string? DisplayUrl        { get; init; }
    /// <summary>Stamped DOM id (data-gs-ad-id="N"). Lets click_ad
    /// re-locate the anchor even if the SERP re-renders.</summary>
    public required int    StampId   { get; init; }
}
