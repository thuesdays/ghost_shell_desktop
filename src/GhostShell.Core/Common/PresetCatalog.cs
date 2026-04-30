// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Common;

/// <summary>
/// Static catalog of warmup presets. Mirrors the legacy
/// <c>ghost_shell/session/site_presets.py</c> — same ids, same site
/// lists, same dwell ranges, same country tags. Kept in code (not DB)
/// because the lists are curated like a recipe book; treating them
/// as data the user could edit invites them to break things.
///
/// To add a preset:
/// <list type="number">
///   <item>Append a new <see cref="WarmupPresetDef"/> below.</item>
///   <item>Add the id to the static accessor at the bottom.</item>
///   <item>The Warmup-robot tab picks it up automatically — no UI change.</item>
/// </list>
/// </summary>
public static class PresetCatalog
{
    // ──────────────────────────────────────────────────────────────
    // Presets
    // ──────────────────────────────────────────────────────────────

    public static readonly WarmupPresetDef General = new()
    {
        Id = "general",
        Label = "General",
        Description = "Balanced mix — search, video, reference, social, utility.",
        Sites = new WarmupSite[]
        {
            new() { Url = "https://www.google.com/",                   Topic = "search",  DwellMinSec = 6,  DwellMaxSec = 12 },
            new() { Url = "https://www.youtube.com/",                  Topic = "video",   DwellMinSec = 8,  DwellMaxSec = 15 },
            new() { Url = "https://en.wikipedia.org/wiki/Main_Page",   Topic = "ref",     DwellMinSec = 10, DwellMaxSec = 20 },
            new() { Url = "https://www.reddit.com/",                   Topic = "social",  DwellMinSec = 8,  DwellMaxSec = 14 },
            new() { Url = "https://www.bing.com/",                     Topic = "search",  DwellMinSec = 4,  DwellMaxSec = 8  },
            new() { Url = "https://www.imdb.com/",                     Topic = "ref",     DwellMinSec = 6,  DwellMaxSec = 12 },
            new() { Url = "https://www.weather.com/",                  Topic = "utility", DwellMinSec = 4,  DwellMaxSec = 8,  Countries = ["US"] },
            new() { Url = "https://openweathermap.org/",               Topic = "utility", DwellMinSec = 4,  DwellMaxSec = 8  },
        },
    };

    public static readonly WarmupPresetDef CommerceUa = new()
    {
        Id = "commerce_ua",
        Label = "Commerce UA  (★ ad-density booster)",
        Description =
            "Visits major UA online stores (Rozetka, Prom, Allo, Comfy, Apteka, Makeup, etc). " +
            "Runs the strongest commercial-intent signal Google has for UA profiles -- pumps " +
            "ad density on subsequent monitor runs by 2-5x.",
        Sites = new WarmupSite[]
        {
            new() { Url = "https://rozetka.com.ua/",     Topic = "marketplace", DwellMinSec = 15, DwellMaxSec = 30, Countries = ["UA"] },
            new() { Url = "https://prom.ua/",            Topic = "marketplace", DwellMinSec = 15, DwellMaxSec = 30, Countries = ["UA"] },
            new() { Url = "https://allo.ua/",            Topic = "electronics", DwellMinSec = 10, DwellMaxSec = 25, Countries = ["UA"] },
            new() { Url = "https://comfy.ua/",           Topic = "electronics", DwellMinSec = 10, DwellMaxSec = 25, Countries = ["UA"] },
            new() { Url = "https://www.foxtrot.com.ua/", Topic = "electronics", DwellMinSec = 10, DwellMaxSec = 25, Countries = ["UA"] },
            new() { Url = "https://eldorado.ua/",        Topic = "electronics", DwellMinSec = 10, DwellMaxSec = 25, Countries = ["UA"] },
            new() { Url = "https://www.moyo.ua/",        Topic = "electronics", DwellMinSec = 10, DwellMaxSec = 25, Countries = ["UA"] },
            new() { Url = "https://www.citrus.ua/",      Topic = "electronics", DwellMinSec = 10, DwellMaxSec = 25, Countries = ["UA"] },
            new() { Url = "https://apteka.com.ua/",      Topic = "pharmacy",    DwellMinSec = 10, DwellMaxSec = 18, Countries = ["UA"] },
            new() { Url = "https://liki24.com/",         Topic = "pharmacy",    DwellMinSec = 10, DwellMaxSec = 18, Countries = ["UA"] },
            new() { Url = "https://makeup.com.ua/",      Topic = "beauty",      DwellMinSec = 10, DwellMaxSec = 18, Countries = ["UA"] },
            new() { Url = "https://eva.ua/",             Topic = "beauty",      DwellMinSec = 10, DwellMaxSec = 18, Countries = ["UA"] },
            new() { Url = "https://shafa.ua/",           Topic = "fashion",     DwellMinSec = 10, DwellMaxSec = 18, Countries = ["UA"] },
            new() { Url = "https://kasta.ua/",           Topic = "fashion",     DwellMinSec = 10, DwellMaxSec = 18, Countries = ["UA"] },
            new() { Url = "https://shopping.google.com/",Topic = "shopping",    DwellMinSec = 10, DwellMaxSec = 18 },
        },
    };

    public static readonly WarmupPresetDef Medical = new()
    {
        Id = "medical",
        Label = "Medical / Health",
        Description = "Authoritative medical + a Ukrainian-Wikipedia anchor for UA-geo profiles.",
        Sites = new WarmupSite[]
        {
            new() { Url = "https://www.who.int/",                                       Topic = "medical", DwellMinSec = 8,  DwellMaxSec = 14 },
            new() { Url = "https://en.wikipedia.org/wiki/Health",                       Topic = "ref",     DwellMinSec = 8,  DwellMaxSec = 16 },
            new() { Url = "https://www.mayoclinic.org/",                                Topic = "medical", DwellMinSec = 8,  DwellMaxSec = 14, Countries = ["US"] },
            new() { Url = "https://www.webmd.com/",                                     Topic = "medical", DwellMinSec = 8,  DwellMaxSec = 14, Countries = ["US"] },
            new() { Url = "https://www.healthline.com/",                                Topic = "medical", DwellMinSec = 8,  DwellMaxSec = 14, Countries = ["US"] },
            new() { Url = "https://medlineplus.gov/",                                   Topic = "medical", DwellMinSec = 6,  DwellMaxSec = 12, Countries = ["US"] },
            new() { Url = "https://www.ncbi.nlm.nih.gov/",                              Topic = "research",DwellMinSec = 8,  DwellMaxSec = 14, Countries = ["US"] },
            new() { Url = "https://uk.wikipedia.org/wiki/%D0%9C%D0%B5%D0%B4%D0%B8%D1%86%D0%B8%D0%BD%D0%B0", Topic = "ref", DwellMinSec = 8, DwellMaxSec = 16, Countries = ["UA"] },
            new() { Url = "https://moz.gov.ua/",                                        Topic = "medical", DwellMinSec = 8,  DwellMaxSec = 14, Countries = ["UA"] },
            new() { Url = "https://compendium.com.ua/",                                 Topic = "pharmacy",DwellMinSec = 8,  DwellMaxSec = 14, Countries = ["UA"] },
            new() { Url = "https://likar.info/",                                        Topic = "medical", DwellMinSec = 8,  DwellMaxSec = 14, Countries = ["UA"] },
            new() { Url = "https://apteka.com.ua/",                                     Topic = "pharmacy",DwellMinSec = 8,  DwellMaxSec = 14, Countries = ["UA"] },
            new() { Url = "https://www.cdc.gov/",                                       Topic = "medical", DwellMinSec = 6,  DwellMaxSec = 12, Countries = ["US"] },
        },
    };

    public static readonly WarmupPresetDef Tech = new()
    {
        Id = "tech",
        Label = "Tech / Developer",
        Description = "HN, StackOverflow, MDN, GitHub, tech journalism.",
        Sites = new WarmupSite[]
        {
            new() { Url = "https://news.ycombinator.com/",       Topic = "news",  DwellMinSec = 8,  DwellMaxSec = 14 },
            new() { Url = "https://stackoverflow.com/",          Topic = "qa",    DwellMinSec = 8,  DwellMaxSec = 14 },
            new() { Url = "https://github.com/",                 Topic = "code",  DwellMinSec = 8,  DwellMaxSec = 14 },
            new() { Url = "https://developer.mozilla.org/",      Topic = "ref",   DwellMinSec = 8,  DwellMaxSec = 14 },
            new() { Url = "https://www.theverge.com/",           Topic = "news",  DwellMinSec = 6,  DwellMaxSec = 12, Countries = ["US"] },
            new() { Url = "https://arstechnica.com/",            Topic = "news",  DwellMinSec = 6,  DwellMaxSec = 12, Countries = ["US"] },
            new() { Url = "https://www.wired.com/",              Topic = "news",  DwellMinSec = 6,  DwellMaxSec = 12, Countries = ["US"] },
            new() { Url = "https://dou.ua/",                     Topic = "news",  DwellMinSec = 6,  DwellMaxSec = 12, Countries = ["UA"] },
            new() { Url = "https://itc.ua/",                     Topic = "news",  DwellMinSec = 6,  DwellMaxSec = 12, Countries = ["UA"] },
        },
    };

    public static readonly WarmupPresetDef News = new()
    {
        Id = "news",
        Label = "News / Reading",
        Description = "Mix of English + Ukrainian news — simulates a daily reader.",
        Sites = new WarmupSite[]
        {
            new() { Url = "https://www.bbc.com/news",          Topic = "news", DwellMinSec = 8, DwellMaxSec = 14 },
            new() { Url = "https://www.reuters.com/",          Topic = "news", DwellMinSec = 8, DwellMaxSec = 14 },
            new() { Url = "https://apnews.com/",               Topic = "news", DwellMinSec = 8, DwellMaxSec = 14 },
            new() { Url = "https://www.bloomberg.com/",        Topic = "news", DwellMinSec = 6, DwellMaxSec = 12, Countries = ["US"] },
            new() { Url = "https://www.nytimes.com/",          Topic = "news", DwellMinSec = 8, DwellMaxSec = 14, Countries = ["US"] },
            new() { Url = "https://www.theguardian.com/",      Topic = "news", DwellMinSec = 8, DwellMaxSec = 14, Countries = ["GB"] },
            new() { Url = "https://www.pravda.com.ua/",        Topic = "news", DwellMinSec = 8, DwellMaxSec = 14, Countries = ["UA"] },
            new() { Url = "https://www.epravda.com.ua/",       Topic = "news", DwellMinSec = 6, DwellMaxSec = 12, Countries = ["UA"] },
            new() { Url = "https://tsn.ua/",                   Topic = "news", DwellMinSec = 6, DwellMaxSec = 12, Countries = ["UA"] },
        },
    };

    public static readonly WarmupPresetDef Mobile = new()
    {
        Id = "mobile",
        Label = "Mobile",
        Description =
            "m.* / mobile.* destinations — auto-selected for mobile-fingerprint profiles when preset=auto.",
        Sites = new WarmupSite[]
        {
            new() { Url = "https://m.youtube.com/",          Topic = "video",  DwellMinSec = 8,  DwellMaxSec = 14 },
            new() { Url = "https://m.wikipedia.org/",        Topic = "ref",    DwellMinSec = 8,  DwellMaxSec = 14 },
            new() { Url = "https://www.google.com/",         Topic = "search", DwellMinSec = 6,  DwellMaxSec = 12 },
            new() { Url = "https://mobile.twitter.com/",     Topic = "social", DwellMinSec = 8,  DwellMaxSec = 14 },
            new() { Url = "https://www.reddit.com/",         Topic = "social", DwellMinSec = 8,  DwellMaxSec = 14 },
            new() { Url = "https://www.instagram.com/",      Topic = "social", DwellMinSec = 6,  DwellMaxSec = 12 },
            new() { Url = "https://www.bbc.com/news",        Topic = "news",   DwellMinSec = 8,  DwellMaxSec = 14 },
            new() { Url = "https://www.weather.com/",        Topic = "utility",DwellMinSec = 4,  DwellMaxSec = 8 },
        },
    };

    /// <summary>All known presets, ordered as displayed on the UI.</summary>
    public static readonly IReadOnlyList<WarmupPresetDef> All = new[]
    {
        General, CommerceUa, Medical, Tech, News, Mobile,
    };

    /// <summary>
    /// Look up a preset by id. Returns <c>null</c> for unknown ids
    /// (we deliberately don't fall back to General — historic warmup
    /// rows referencing a now-removed preset should display the
    /// preset id, not silently rebrand themselves).
    /// </summary>
    public static WarmupPresetDef? Find(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    // ──────────────────────────────────────────────────────────────
    // Site picker — country filter + uniform shuffle. Same shape as
    // legacy `pick_sites()` so behaviour parity holds.
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Pull <paramref name="n"/> sites from the named preset. If
    /// <paramref name="targetCountry"/> is supplied (ISO-2 like "UA"),
    /// only sites tagged with <c>"*"</c> or that country survive. If
    /// the filter empties the list, falls back to unfiltered (matches
    /// legacy's "rather visit something than nothing" fallback).
    ///
    /// Order is randomised so two consecutive runs of the same preset
    /// don't generate identical visit fingerprints. Pass a
    /// <paramref name="rng"/> for deterministic test runs.
    /// </summary>
    public static IReadOnlyList<WarmupSite> PickSites(
        WarmupPresetDef preset, int n,
        string? targetCountry = null, Random? rng = null)
    {
        if (n <= 0) return Array.Empty<WarmupSite>();
        rng ??= Random.Shared;

        IEnumerable<WarmupSite> filtered = preset.Sites;
        if (!string.IsNullOrWhiteSpace(targetCountry))
        {
            var iso = targetCountry.Trim().ToUpperInvariant();
            var ok = preset.Sites
                .Where(s => s.Countries.Contains("*") || s.Countries.Any(c =>
                    string.Equals(c, iso, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            // Empty intersection (e.g. preset=mobile, country="JP")
            // → "rather any than none" fallback; the alternative is
            // a no-op warmup that confuses the user.
            filtered = ok.Count > 0 ? ok : preset.Sites;
        }

        // Shuffle then slice — the standard OrderBy(rng.Next()) pattern
        // is known biased on small lists; Fisher-Yates is one line and
        // unbiased.
        var list = filtered.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list.Take(Math.Max(1, n)).ToList();
    }
}
