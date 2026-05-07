// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 29 — flat key/value settings store. Mirrors the legacy web's
/// dotted-key config table. Strongly-typed accessors for the common
/// settings + raw GetString/SetString for everything else (custom
/// blocking patterns, bundle paths, etc.).
/// </summary>
public interface ISettingsService
{
    // ─── Generic ────────────────────────────────────────────────────
    Task<string?> GetStringAsync(string key, CancellationToken ct = default);
    Task<int?>    GetIntAsync(string key, CancellationToken ct = default);
    Task<double?> GetDoubleAsync(string key, CancellationToken ct = default);
    Task<bool?>   GetBoolAsync(string key, CancellationToken ct = default);

    Task SetStringAsync(string key, string? value, CancellationToken ct = default);
    Task SetIntAsync(string key, int value, CancellationToken ct = default);
    Task SetDoubleAsync(string key, double value, CancellationToken ct = default);
    Task SetBoolAsync(string key, bool value, CancellationToken ct = default);

    /// <summary>Drop a key. No-op if it isn't present.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>All rows — for export / debug. Order is alphabetical by key.</summary>
    Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Bulk apply — used by Import. Existing keys overwritten;
    /// missing keys left untouched (merge mode); pass replaceAll=true
    /// to wipe the table first (replace mode).</summary>
    Task ApplyAllAsync(IReadOnlyDictionary<string, string?> values, bool replaceAll = false, CancellationToken ct = default);

    // ─── Strongly-typed shortcuts ───────────────────────────────────

    Task<int>    GetUaSpoofMinAsync(CancellationToken ct = default);
    Task<int>    GetUaSpoofMaxAsync(CancellationToken ct = default);
    Task SetUaSpoofRangeAsync(int min, int max, CancellationToken ct = default);

    Task<string?> GetChromiumBinaryPathAsync(CancellationToken ct = default);
    Task SetChromiumBinaryPathAsync(string? path, CancellationToken ct = default);

    Task<bool> GetSerpScrollEnabledAsync(CancellationToken ct = default);
    Task<bool> GetSerpDwellEnabledAsync(CancellationToken ct = default);
    Task<bool> GetOrganicClickEnabledAsync(CancellationToken ct = default);
    Task<double> GetOrganicClickProbabilityAsync(CancellationToken ct = default);
    Task<int> GetOrganicDwellMinSecAsync(CancellationToken ct = default);
    Task<int> GetOrganicDwellMaxSecAsync(CancellationToken ct = default);

    Task<bool> GetAutoEnrichEnabledAsync(CancellationToken ct = default);
    Task<int>  GetAutoEnrichMaxDaysAsync(CancellationToken ct = default);
    Task<int>  GetAutoEnrichMaxUrlsAsync(CancellationToken ct = default);
    Task<string?> GetAutoEnrichSourcePathAsync(CancellationToken ct = default);
}

/// <summary>Centralised list of dotted setting keys the desktop knows
/// about. Other layers (runtime, UI) reference these constants instead
/// of raw strings, so a typo doesn't silently fall through to default.</summary>
public static class SettingsKeys
{
    public const string ChromiumBinaryPath  = "browser.binary_path";
    public const string UaSpoofMin          = "browser.spoof_chrome_min";
    public const string UaSpoofMax          = "browser.spoof_chrome_max";

    public const string SerpScrollEnabled       = "behavior.serp_scroll_enabled";
    public const string SerpDwellEnabled        = "behavior.serp_dwell_enabled";
    public const string OrganicClickEnabled     = "behavior.organic_click_enabled";
    public const string OrganicClickProbability = "behavior.organic_click_probability";
    public const string OrganicDwellMinSec      = "behavior.organic_dwell_min_sec";
    public const string OrganicDwellMaxSec      = "behavior.organic_dwell_max_sec";

    public const string AutoEnrichEnabled    = "browser.auto_enrich_from_host_chrome";
    public const string AutoEnrichMaxDays    = "browser.auto_enrich_max_days";
    public const string AutoEnrichMaxUrls    = "browser.auto_enrich_max_urls";
    public const string AutoEnrichSourcePath = "browser.auto_enrich_source_path";

    // Phase 30 — Performance / resource blocking. Eight bucket toggles
    // + free-form custom patterns. Each bucket maps to a hand-curated
    // list of CDP wildcard URL patterns; see ResourceBlockingPatterns.
    public const string BlockYoutubeVideo     = "browser.block_youtube_video";
    public const string BlockGoogleImages     = "browser.block_google_images";
    public const string BlockGoogleMapsTiles  = "browser.block_google_maps_tiles";
    public const string BlockFonts            = "browser.block_fonts";
    public const string BlockAnalytics        = "browser.block_analytics";
    public const string BlockSocialWidgets    = "browser.block_social_widgets";
    public const string BlockVideoEverywhere  = "browser.block_video_everywhere";
    public const string BlockCustomPatterns   = "browser.block_custom_patterns"; // newline-separated

    /// <summary>Phase 71aa — UI theme picker. Values: "dark" (default,
    /// Linear/Vercel high-contrast inkblot) or "light" (Solar near-white).
    /// Read at app startup by IThemeService BEFORE MainWindow is shown
    /// so the saved choice applies on the first frame; persisted by
    /// the Settings → Appearance tab.</summary>
    public const string UiTheme               = "ui.theme";
}
