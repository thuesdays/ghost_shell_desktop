// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.App.Logging;
using GhostShell.App.Theming;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 29 — full Settings page port. Sections:
/// Build info / Install paths / UA spoof range / SERP engagement /
/// Auto-enrich / Export-Import / Danger zone.
///
/// Each input is bound TwoWay to a property here; changes persist
/// via <see cref="ISettingsService"/> on focus-loss / explicit Save.
/// </summary>
public sealed partial class SettingsViewModel : BaseViewModel
{
    private readonly IChromiumLocator _chromiumLocator;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IProfileService _profiles;
    private readonly IRunService _runs;
    private readonly IScriptService _scripts;
    private readonly ITrafficService _traffic;
    private readonly IDialogService _dialogs;
    private readonly IThemeService _theme;
    private readonly ILogger<SettingsViewModel> _log;
    private bool _initialised;

    public SettingsViewModel(
        IChromiumLocator chromiumLocator,
        ISettingsService settings,
        INotificationService notifications,
        IProfileService profiles,
        IRunService runs,
        IScriptService scripts,
        ITrafficService traffic,
        IDialogService dialogs,
        IThemeService theme,
        ILogger<SettingsViewModel> log)
    {
        _chromiumLocator = chromiumLocator;
        _settings        = settings;
        _notifications   = notifications;
        _profiles        = profiles;
        _runs            = runs;
        _scripts         = scripts;
        _traffic         = traffic;
        _dialogs         = dialogs;
        _theme           = theme;
        _log             = log;
        // Phase 71aa — pre-populate the appearance picker from the
        // currently-active theme so the radio buttons show the right
        // checked state on first navigation.
        _isLightTheme = _theme.Active == AppTheme.Light;
        _isDarkTheme  = _theme.Active == AppTheme.Dark;
        ProbeChromium();
    }

    /// <summary>Phase 30 — left-rail tab id. Drives which section is
    /// visible. Same-shape as the legacy web's hash navigation
    /// (#build-info / #install-paths / etc.).</summary>
    [ObservableProperty] private string _activeTab = "build-info";

    public IReadOnlyList<SettingsTab> Tabs { get; } = new[]
    {
        new SettingsTab("appearance",    "🎨  Appearance"),
        new SettingsTab("build-info",    "🚀  Build info"),
        new SettingsTab("ua-spoof",      "🎭  UA spoof range"),
        new SettingsTab("serp",          "🍯  SERP engagement"),
        new SettingsTab("performance",   "⚡  Performance"),
        new SettingsTab("auto-enrich",   "🔴  Auto-enrich"),
        new SettingsTab("export-import", "📦  Export / Import"),
        new SettingsTab("danger-zone",   "⚠  Danger zone"),
    };

    // ─── Phase 71aa — Appearance / theme picker ──────────────────────
    /// <summary>True when the user has selected (but not necessarily
    /// applied) the Dark variant in the radio group. Saved + restart
    /// prompt happens via <see cref="ApplyThemeCommand"/>.</summary>
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private bool _isLightTheme;

    /// <summary>True when the picker selection differs from the
    /// theme that was loaded at app startup. Drives the visibility
    /// of the "Apply &amp; restart" button so it only appears when
    /// there's actually something to apply.</summary>
    public bool ThemeChanged
    {
        get
        {
            var picked = IsLightTheme ? AppTheme.Light : AppTheme.Dark;
            return picked != _theme.Active;
        }
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        if (value) IsLightTheme = false;
        OnPropertyChanged(nameof(ThemeChanged));
    }

    partial void OnIsLightThemeChanged(bool value)
    {
        if (value) IsDarkTheme = false;
        OnPropertyChanged(nameof(ThemeChanged));
    }

    [RelayCommand]
    private async Task ApplyThemeAsync()
    {
        var picked = IsLightTheme ? AppTheme.Light : AppTheme.Dark;
        if (picked == _theme.Active) return;

        await _theme.SaveAsync(picked);
        OnPropertyChanged(nameof(ThemeChanged));

        // StaticResource brushes are baked at parse time, so a live
        // swap won't repaint already-rendered windows. Prompt for a
        // restart — user clicks "Restart now" → we tear down the app
        // and the OS shell respawns it (app is registered with a
        // shortcut/installer so Process.Start on the assembly path
        // starts the right binary).
        var ok = await _dialogs.ConfirmAsync(
            "Restart Ghost Shell?",
            $"The {picked} theme is saved. Restart now to apply, or keep the current look until next launch.",
            "Restart now");
        if (!ok) return;

        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                // Indirect launch via cmd.exe with a 1-second delay.
                // App.OnStartup holds a singleton Mutex (Local\GhostShellDesktop_Singleton);
                // a direct Process.Start would race against the OS
                // teardown of THIS process and the new instance would
                // see the mutex still held, signal the existing
                // window, and exit. The 1-second pause via `timeout`
                // gives the kernel enough time to fully release the
                // mutex handle before the new process starts.
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "cmd.exe",
                    Arguments       = $"/c timeout /t 1 /nobreak >nul && start \"\" \"{exe}\"",
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Theme restart: couldn't relaunch — user must reopen manually");
        }

        // Bypass the tray-hide branch in MainWindow.OnClosing so
        // we actually exit; the periodic update loop has the same
        // hook so reusing AllowingShutdown is safe.
        App.AllowingShutdown = true;
        Application.Current?.Shutdown(0);
    }

    [RelayCommand]
    private void SetActiveTab(string? id) { if (!string.IsNullOrEmpty(id)) ActiveTab = id; }

    public override async Task OnNavigatedToAsync()
    {
        // Phase 29 audit fix — flip _initialised back to true ONLY if
        // the entire load succeeded. If anything throws we leave it
        // false so subsequent property changes don't accidentally
        // persist over a half-loaded state. The user can reload by
        // navigating away + back.
        _initialised = false;
        var loaded = false;
        _log.LogInformation("Settings.OnNavigatedToAsync: load BEGIN (vm hash={H})", GetHashCode());
        try
        {
            // Phase 60d — read raw DB values FIRST so the diagnostic log
            // shows what's actually in the DB (no defaults baked in).
            // Without this we can't tell whether "false" came from the DB
            // or from the `?? false` fallback.
            var rawYoutube = await _settings.GetBoolAsync(SettingsKeys.BlockYoutubeVideo);
            var rawImages  = await _settings.GetBoolAsync(SettingsKeys.BlockGoogleImages);
            var rawMaps    = await _settings.GetBoolAsync(SettingsKeys.BlockGoogleMapsTiles);
            var rawFonts   = await _settings.GetBoolAsync(SettingsKeys.BlockFonts);
            var rawAna     = await _settings.GetBoolAsync(SettingsKeys.BlockAnalytics);
            var rawSocial  = await _settings.GetBoolAsync(SettingsKeys.BlockSocialWidgets);
            var rawVideo   = await _settings.GetBoolAsync(SettingsKeys.BlockVideoEverywhere);
            var rawCustom  = await _settings.GetStringAsync(SettingsKeys.BlockCustomPatterns);
            _log.LogInformation(
                "Settings.OnNavigatedToAsync: DB values block_youtube={Y} block_images={I} " +
                "block_maps={M} block_fonts={F} block_analytics={A} block_social={S} " +
                "block_video={V} custom_patterns_len={CL}",
                rawYoutube?.ToString() ?? "NULL",
                rawImages?.ToString() ?? "NULL",
                rawMaps?.ToString() ?? "NULL",
                rawFonts?.ToString() ?? "NULL",
                rawAna?.ToString() ?? "NULL",
                rawSocial?.ToString() ?? "NULL",
                rawVideo?.ToString() ?? "NULL",
                rawCustom?.Length ?? 0);

            ChromiumBinaryPath = await _settings.GetChromiumBinaryPathAsync() ?? "";
            UaSpoofMin         = await _settings.GetUaSpoofMinAsync();
            UaSpoofMax         = await _settings.GetUaSpoofMaxAsync();
            SerpScroll         = await _settings.GetSerpScrollEnabledAsync();
            SerpDwell          = await _settings.GetSerpDwellEnabledAsync();
            OrganicClick       = await _settings.GetOrganicClickEnabledAsync();
            OrganicClickProb   = await _settings.GetOrganicClickProbabilityAsync();
            OrganicDwellMin    = await _settings.GetOrganicDwellMinSecAsync();
            OrganicDwellMax    = await _settings.GetOrganicDwellMaxSecAsync();
            AutoEnrichEnabled  = await _settings.GetAutoEnrichEnabledAsync();
            AutoEnrichMaxDays  = await _settings.GetAutoEnrichMaxDaysAsync();
            AutoEnrichMaxUrls  = await _settings.GetAutoEnrichMaxUrlsAsync();
            AutoEnrichSrcPath  = await _settings.GetAutoEnrichSourcePathAsync() ?? "";
            // Phase 30 — performance / resource blocking. Use the raw values
            // pre-fetched above (avoids a second round-trip per key).
            BlockYoutube         = rawYoutube ?? false;
            BlockGoogleImages    = rawImages  ?? false;
            BlockMapsTiles       = rawMaps    ?? false;
            BlockFonts           = rawFonts   ?? false;
            BlockAnalytics       = rawAna     ?? false;
            BlockSocialWidgets   = rawSocial  ?? false;
            BlockVideoEverywhere = rawVideo   ?? false;
            BlockCustomPatterns  = rawCustom  ?? "";
            UpdatePoolPreview();
            UpdateBlockingPreview();
            loaded = true;
            _log.LogInformation(
                "Settings.OnNavigatedToAsync: load OK — BlockYoutube={Y} BlockMaps={M} " +
                "BlockFonts={F} BlockAnalytics={A} BlockSocial={S} BlockVideo={V}",
                BlockYoutube, BlockMapsTiles, BlockFonts,
                BlockAnalytics, BlockSocialWidgets, BlockVideoEverywhere);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Settings load failed — leaving fields disabled until next visit");
        }
        finally
        {
            _initialised = loaded;
            _log.LogInformation(
                "Settings.OnNavigatedToAsync: END _initialised={Init}", _initialised);
        }
    }

    // ─── Build info (read-only) ──────────────────────────────────────

    [ObservableProperty] private string _dataDirectory   = AppPaths.DataDir;
    [ObservableProperty] private string _databasePath    = AppPaths.DatabasePath;
    [ObservableProperty] private string _logsDirectory   = AppPaths.LogsDir;
    [ObservableProperty] private string _currentLogFile  = LoggingSetup.CurrentLogPath;

    [ObservableProperty] private string _chromiumPath          = "—";
    [ObservableProperty] private string _chromedriverPath      = "—";
    [ObservableProperty] private string _chromiumVersion       = "—";
    [ObservableProperty] private string _chromiumProbedFrom    = "—";
    [ObservableProperty] private string _chromiumStatusMessage = "Probing…";
    [ObservableProperty] private bool   _chromiumFound;
    [ObservableProperty] private string _chromiumCandidates    = "";

    [ObservableProperty]
    private string _appVersion =
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    // ─── Install paths ───────────────────────────────────────────────

    [ObservableProperty] private string _chromiumBinaryPath = "";
    partial void OnChromiumBinaryPathChanged(string value) => _ = PersistAsync(
        () => _settings.SetChromiumBinaryPathAsync(string.IsNullOrWhiteSpace(value) ? null : value.Trim()));

    // ─── UA spoof range ──────────────────────────────────────────────

    [ObservableProperty] private int _uaSpoofMin = 130;
    [ObservableProperty] private int _uaSpoofMax = 147;
    [ObservableProperty] private string _uaPoolPreview = "";

    partial void OnUaSpoofMinChanged(int value) => _ = PersistRangeAsync();
    partial void OnUaSpoofMaxChanged(int value) => _ = PersistRangeAsync();

    private async Task PersistRangeAsync()
    {
        UpdatePoolPreview();
        if (!_initialised) return;
        try { await _settings.SetUaSpoofRangeAsync(UaSpoofMin, UaSpoofMax); }
        catch (Exception ex) { _log.LogWarning(ex, "Persist UA range failed"); }
    }

    private void UpdatePoolPreview()
    {
        // Deterministic preview — the actual fingerprint pool lives in
        // the runtime layer, but the user-facing preview should match
        // the inclusive [Min..Max] window. We surface up to 6 versions
        // (newest first) so the chip stays compact.
        var hi = Math.Max(UaSpoofMin, UaSpoofMax);
        var lo = Math.Min(UaSpoofMin, UaSpoofMax);
        if (hi - lo > 30) // sanity clamp on the preview row
            UaPoolPreview = $"{hi}.0.7780.88, {hi - 1}.0.7715.130, {hi - 2}.0.7665.162, … (range too wide)";
        else
        {
            var versions = new List<string>();
            for (int v = hi; v >= lo && versions.Count < 6; v--)
                versions.Add($"{v}.0.{7000 + v * 5}.{20 + v % 100}");
            UaPoolPreview = string.Join(", ", versions);
        }
    }

    // ─── SERP engagement ─────────────────────────────────────────────

    [ObservableProperty] private bool   _serpScroll        = true;
    [ObservableProperty] private bool   _serpDwell         = true;
    [ObservableProperty] private bool   _organicClick      = true;
    [ObservableProperty] private double _organicClickProb  = 0.25;
    [ObservableProperty] private int    _organicDwellMin   = 8;
    [ObservableProperty] private int    _organicDwellMax   = 26;

    partial void OnSerpScrollChanged(bool value)        => _ = PersistAsync(() => _settings.SetBoolAsync(SettingsKeys.SerpScrollEnabled, value));
    partial void OnSerpDwellChanged(bool value)         => _ = PersistAsync(() => _settings.SetBoolAsync(SettingsKeys.SerpDwellEnabled, value));
    partial void OnOrganicClickChanged(bool value)      => _ = PersistAsync(() => _settings.SetBoolAsync(SettingsKeys.OrganicClickEnabled, value));
    partial void OnOrganicClickProbChanged(double value)=> _ = PersistAsync(() => _settings.SetDoubleAsync(SettingsKeys.OrganicClickProbability, Math.Clamp(value, 0, 1)));
    partial void OnOrganicDwellMinChanged(int value)    => _ = PersistAsync(() => _settings.SetIntAsync(SettingsKeys.OrganicDwellMinSec, Math.Max(0, value)));
    partial void OnOrganicDwellMaxChanged(int value)    => _ = PersistAsync(() => _settings.SetIntAsync(SettingsKeys.OrganicDwellMaxSec, Math.Max(0, value)));

    // ─── Performance / resource blocking (Phase 30) ──────────────────

    [ObservableProperty] private bool   _blockYoutube;
    [ObservableProperty] private bool   _blockGoogleImages;
    [ObservableProperty] private bool   _blockMapsTiles;
    [ObservableProperty] private bool   _blockFonts;
    [ObservableProperty] private bool   _blockAnalytics;
    [ObservableProperty] private bool   _blockSocialWidgets;
    [ObservableProperty] private bool   _blockVideoEverywhere;
    [ObservableProperty] private string _blockCustomPatterns = "";
    [ObservableProperty] private string _blockingSummary = "";

    partial void OnBlockYoutubeChanged(bool value)         => _ = PersistBoolAsync(SettingsKeys.BlockYoutubeVideo, value);
    partial void OnBlockGoogleImagesChanged(bool value)    => _ = PersistBoolAsync(SettingsKeys.BlockGoogleImages, value);
    partial void OnBlockMapsTilesChanged(bool value)       => _ = PersistBoolAsync(SettingsKeys.BlockGoogleMapsTiles, value);
    partial void OnBlockFontsChanged(bool value)           => _ = PersistBoolAsync(SettingsKeys.BlockFonts, value);
    partial void OnBlockAnalyticsChanged(bool value)       => _ = PersistBoolAsync(SettingsKeys.BlockAnalytics, value);
    partial void OnBlockSocialWidgetsChanged(bool value)   => _ = PersistBoolAsync(SettingsKeys.BlockSocialWidgets, value);
    partial void OnBlockVideoEverywhereChanged(bool value) => _ = PersistBoolAsync(SettingsKeys.BlockVideoEverywhere, value);
    partial void OnBlockCustomPatternsChanged(string value) => _ = PersistAsync(
        () => _settings.SetStringAsync(SettingsKeys.BlockCustomPatterns, value));

    private async Task PersistBoolAsync(string key, bool v)
    {
        UpdateBlockingPreview();
        if (!_initialised) return;
        try
        {
            _log.LogInformation("Persisting {Key} = {Value}", key, v);
            await _settings.SetBoolAsync(key, v);
            _log.LogInformation("Successfully persisted {Key} = {Value}", key, v);
        }
        catch (Exception ex) { _log.LogError(ex, "Persist {Key} = {Value} FAILED", key, v); }
    }

    private void UpdateBlockingPreview()
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (BlockYoutube)         enabled.Add("block_youtube_video");
        if (BlockGoogleImages)    enabled.Add("block_google_images");
        if (BlockMapsTiles)       enabled.Add("block_google_maps_tiles");
        if (BlockFonts)           enabled.Add("block_fonts");
        if (BlockAnalytics)       enabled.Add("block_analytics");
        if (BlockSocialWidgets)   enabled.Add("block_social_widgets");
        if (BlockVideoEverywhere) enabled.Add("block_video_everywhere");
        var patterns = ResourceBlockingPatterns.Compose(enabled, BlockCustomPatterns);
        var custom   = ResourceBlockingPatterns.ParseCustomPatterns(BlockCustomPatterns).Count();
        BlockingSummary = patterns.Count == 0
            ? "No patterns active — every resource will load."
            : $"{patterns.Count} URL pattern(s) will be blocked · {enabled.Count} bucket(s) + {custom} custom";
    }

    // ─── Auto-enrich ─────────────────────────────────────────────────

    [ObservableProperty] private bool   _autoEnrichEnabled;
    [ObservableProperty] private int    _autoEnrichMaxDays = 30;
    [ObservableProperty] private int    _autoEnrichMaxUrls = 500;
    [ObservableProperty] private string _autoEnrichSrcPath = "";

    partial void OnAutoEnrichEnabledChanged(bool value) => _ = PersistAsync(() => _settings.SetBoolAsync(SettingsKeys.AutoEnrichEnabled, value));
    partial void OnAutoEnrichMaxDaysChanged(int value)  => _ = PersistAsync(() => _settings.SetIntAsync(SettingsKeys.AutoEnrichMaxDays, Math.Clamp(value, 1, 365)));
    partial void OnAutoEnrichMaxUrlsChanged(int value)  => _ = PersistAsync(() => _settings.SetIntAsync(SettingsKeys.AutoEnrichMaxUrls, Math.Clamp(value, 10, 10000)));
    partial void OnAutoEnrichSrcPathChanged(string value)=> _ = PersistAsync(() => _settings.SetStringAsync(SettingsKeys.AutoEnrichSourcePath, string.IsNullOrWhiteSpace(value) ? null : value.Trim()));

    private async Task PersistAsync(Func<Task> setter)
    {
        if (!_initialised) return;
        try
        {
            _log.LogInformation("Persisting setting change");
            await setter();
            _log.LogInformation("Successfully persisted setting change");
        }
        catch (Exception ex) { _log.LogError(ex, "Persist setting FAILED"); }
    }

    // ─── Export / Import / Reset ────────────────────────────────────

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        var sfd = new SaveFileDialog
        {
            FileName = $"ghost-shell-config-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON bundle (*.json)|*.json",
        };
        if (sfd.ShowDialog() != true) return;
        try
        {
            var settings  = await _settings.GetAllAsync();
            var profiles  = await _profiles.ListAsync();
            var scripts   = await _scripts.ListAsync();
            var bundle = new
            {
                format_version = 2,
                exported_at    = DateTime.UtcNow.ToString("O"),
                app_version    = AppVersion,
                config         = settings,
                profiles       = profiles,
                scripts        = scripts,
            };
            var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            await File.WriteAllTextAsync(sfd.FileName, json);
            await _notifications.AddAsync(NotificationSeverity.Success,
                "Config exported",
                $"Bundle written to {Path.GetFileName(sfd.FileName)}",
                source: "settings_export");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Export config failed");
            await _dialogs.ConfirmAsync("Export failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [ObservableProperty] private bool _replaceMode;
    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        var ofd = new OpenFileDialog { Filter = "JSON bundle (*.json)|*.json" };
        if (ofd.ShowDialog() != true) return;
        if (ReplaceMode)
        {
            var ok = await _dialogs.ConfirmAsync(
                "Replace ALL config?",
                "Replace mode wipes every existing setting before importing the bundle. Profiles, scripts, and proxies are kept. There is no undo.",
                "Replace", ConfirmSeverity.Danger);
            if (!ok) return;
        }
        try
        {
            var raw = await File.ReadAllTextAsync(ofd.FileName);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            int settingsCount = 0;
            if (root.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, string?>();
                foreach (var kv in cfg.EnumerateObject())
                    dict[kv.Name] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.GetRawText();
                await _settings.ApplyAllAsync(dict, replaceAll: ReplaceMode);
                settingsCount = dict.Count;
            }
            await _notifications.AddAsync(NotificationSeverity.Success,
                "Config imported",
                $"{settingsCount} setting(s) applied (mode={(ReplaceMode ? "replace" : "merge")}).",
                source: "settings_import");
            await OnNavigatedToAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Import config failed");
            await _dialogs.ConfirmAsync("Import failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ResetStatsAsync()
    {
        var ok = await _dialogs.ConfirmAsync(
            "Reset stats counters?",
            "Wipes run history, traffic stats, and notifications. Profiles, scripts, vault, and settings are preserved. There is no undo.",
            "Reset", ConfirmSeverity.Danger);
        if (!ok) return;
        try
        {
            // Wipe runs, traffic stats, and old notifications. Vault,
            // profiles, scripts, proxies, and settings stay untouched.
            await _runs.ClearAsync(olderThan: null);
            await _traffic.CleanupOlderThanAsync(1);  // smallest valid window — effectively wipes all
            await _notifications.PurgeOlderThanAsync(1);
            await _notifications.AddAsync(NotificationSeverity.Info,
                "Stats counters reset",
                "Run history + traffic stats wiped. Profiles + scripts kept.",
                source: "settings_reset");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reset stats failed");
            await _dialogs.ConfirmAsync("Reset failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    // ─── Existing chrome / folder helpers (unchanged) ───────────────

    [RelayCommand]
    private void ProbeChromium()
    {
        try
        {
            var status = _chromiumLocator.Locate();
            ChromiumFound          = status.Found;
            ChromiumPath           = status.ChromePath       ?? "—";
            ChromedriverPath       = status.ChromeDriverPath ?? "—";
            ChromiumVersion        = status.VersionString    ?? "—";
            ChromiumProbedFrom     = status.ProbedFrom       ?? "—";
            ChromiumStatusMessage  = status.Found
                ? $"Located via {status.ProbedFrom}"
                : status.Error ?? "Not found.";
            ChromiumCandidates     = string.Join("\n", status.Candidates);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chromium probe threw");
            ChromiumFound         = false;
            ChromiumStatusMessage = $"Probe failed: {ex.Message}";
        }
    }

    [RelayCommand] private void OpenLogsFolder()       => TryOpenInExplorer(AppPaths.LogsDir, "logs folder");
    [RelayCommand] private void OpenDataFolder()       => TryOpenInExplorer(AppPaths.DataDir, "data folder");
    [RelayCommand]
    private void OpenChromiumFolder()
    {
        if (string.IsNullOrEmpty(ChromiumPath) || ChromiumPath == "—") return;
        var dir = Path.GetDirectoryName(ChromiumPath);
        if (!string.IsNullOrEmpty(dir)) TryOpenInExplorer(dir, "Chromium folder");
    }

    private void TryOpenInExplorer(string path, string label)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { _log.LogError(ex, "Failed to open {Label}", label); }
    }
}

/// <summary>Phase 30 — left-rail tab descriptor.</summary>
public sealed partial class SettingsTab : ObservableObject
{
    public string Id    { get; }
    public string Label { get; }
    public SettingsTab(string id, string label) { Id = id; Label = label; }
}
