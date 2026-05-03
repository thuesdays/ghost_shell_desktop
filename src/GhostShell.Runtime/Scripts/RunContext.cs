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
    /// Domains to skip entirely — never click, never record competitor
    /// data, never fire action events. Used by per-step
    /// <c>SkipOnBlocked</c> / <c>OnlyOnBlocked</c> filters.
    /// </summary>
    public HashSet<string> BlockDomains { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Currently-iterated ad's href, set by foreach_ad on each lap so
    /// per-step domain filters can resolve which ad they're looking at.
    /// Empty outside an ad loop, in which case the filters become
    /// no-ops (web parity: legacy treats the absence of an ad as
    /// "not matching any policy").
    /// </summary>
    public string CurrentAdHref { get; set; } = "";

    /// <summary>
    /// The ad's *display URL* — the green-text "goodmedika.com.ua/ua"
    /// that Google shows under the ad title. This is the ONLY reliable
    /// signal of the advertiser's identity when the click goes through
    /// an affiliate tracker (e.g. the user advertises goodmedika.com.ua
    /// via partner site 120na80.com.ua, the click href is the partner
    /// domain, but the display URL is the real advertiser). Treated as
    /// an additional source for ad_is_mine/ad_is_target/ad_is_external/
    /// ad_is_competitor checks — if either the click host OR the
    /// display host matches a list, the gate fires.
    /// </summary>
    public string CurrentAdDisplayUrl { get; set; } = "";

    /// <summary>
    /// Phase 24 — credential-vault references resolved at run start.
    /// Shape: <c>{ "12": { "username": "...", "password": "..." } }</c>.
    /// Read by <c>InterpolateVars</c> when a <c>{{vault.&lt;id&gt;.&lt;field&gt;}}</c>
    /// placeholder is encountered; missing items / fields fall through
    /// to the literal placeholder text (debugging-friendly).
    /// </summary>
    public Dictionary<string, IReadOnlyDictionary<string, string>> Vault { get; }
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Phase 69 — profile-scoped vault aliases resolved at run start.
    /// Shape: <c>{ "SEED": "abandon abandon ...", "PASS": "..." }</c>.
    /// Populated from <c>IVaultService.ResolveAliasesAsync(profileName, aliases)</c>
    /// before script execution. <c>InterpolateVars</c> consults this
    /// for <c>{{vault.SEED}}</c>-style placeholders. Missing aliases
    /// fall through to literal placeholder text.
    /// </summary>
    public Dictionary<string, string> VaultAliases { get; }
        = new(StringComparer.OrdinalIgnoreCase);

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
