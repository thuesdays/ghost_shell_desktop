// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.App.Navigation;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 31 — Overview dashboard. Mirrors the legacy web's home-page
/// surface: status tiles up top, traffic / fingerprint snapshots in
/// the middle, recent activity at the bottom. Auto-refreshes every
/// 10s while visible.
/// </summary>
public sealed partial class OverviewViewModel : BaseViewModel
{
    private readonly IRunService _runs;
    private readonly IProfileService _profiles;
    private readonly IProxyService _proxies;
    private readonly IVaultService _vault;
    private readonly ITrafficService _traffic;
    private readonly INavigationService _nav;
    private readonly IProfileRunner _runner;
    private readonly IOverviewLayoutService _layout;
    // Phase 34 — name avoids colliding with the [ObservableProperty]
    // backing field `_adDensity` (the source generator emits a property
    // `AdDensity` from it). Service stays private/readonly.
    private readonly IAdDensityService _adDensitySvc;
    private readonly ILogger<OverviewViewModel> _log;
    private readonly DispatcherTimer _refresh;
    private Dictionary<string, OverviewWidgetState> _layoutState = new();

    public OverviewViewModel(
        IRunService runs,
        IProfileService profiles,
        IProxyService proxies,
        IVaultService vault,
        ITrafficService traffic,
        INavigationService nav,
        IProfileRunner runner,
        IOverviewLayoutService layout,
        IAdDensityService adDensity,
        ILogger<OverviewViewModel> log)
    {
        _runs     = runs;
        _profiles = profiles;
        _proxies  = proxies;
        _vault    = vault;
        _traffic  = traffic;
        _nav      = nav;
        _runner   = runner;
        _layout   = layout;
        _adDensitySvc = adDensity;
        _log      = log;

        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _refresh.Tick += async (_, _) => await ReloadAsync();
    }

    // ─── Header ─────────────────────────────────────────────────────

    [ObservableProperty] private string _greeting = "Hello";

    // ─── Run stats ──────────────────────────────────────────────────

    [ObservableProperty] private int _totalRuns;
    [ObservableProperty] private int _successfulRuns;
    [ObservableProperty] private int _failedRuns;
    [ObservableProperty] private int _runningRuns;
    [ObservableProperty] private int _profileCount;
    [ObservableProperty] private int _proxyCount;

    /// <summary>Successful / total — used for the colored ring on the
    /// run-stats card. Falls back to 0 when nothing has run yet.</summary>
    public double SuccessRate => TotalRuns == 0 ? 0 : (double)SuccessfulRuns / TotalRuns;
    public string SuccessRateLabel => TotalRuns == 0 ? "—" : $"{SuccessRate * 100:F0}%";
    public string SuccessRateBrushKey => TotalRuns == 0 ? "TextDim"
        : SuccessRate >= 0.85 ? "OkBrush"
        : SuccessRate >= 0.5  ? "WarnBrush"
        :                       "ErrBrush";

    partial void OnTotalRunsChanged(int value)      => RefreshSuccessRate();
    partial void OnSuccessfulRunsChanged(int value) => RefreshSuccessRate();
    private void RefreshSuccessRate()
    {
        OnPropertyChanged(nameof(SuccessRate));
        OnPropertyChanged(nameof(SuccessRateLabel));
        OnPropertyChanged(nameof(SuccessRateBrushKey));
    }

    // ─── Vault status ──────────────────────────────────────────────

    [ObservableProperty] private string _vaultStatusText = "—";
    [ObservableProperty] private string _vaultStatusBrushKey = "TextDim";

    // ─── Traffic snapshot ──────────────────────────────────────────

    [ObservableProperty] private string _traffic24hText = "0 B";
    [ObservableProperty] private string _trafficRequestsText = "0";
    [ObservableProperty] private int _trafficActiveProfiles;
    [ObservableProperty] private int _trafficUniqueDomains;

    // ─── Active runs panel ────────────────────────────────────────

    public ObservableCollection<string> ActiveProfiles { get; } = new();
    public bool HasActiveRuns => ActiveProfiles.Count > 0;

    // ─── Recent runs ──────────────────────────────────────────────

    public ObservableCollection<RecentRun> RecentRuns { get; } = new();

    // ─── Widget visibility (configurable) ────────────────────────

    public bool ShowHeroStats
    {
        get => _layoutState.TryGetValue(OverviewWidgetCatalog.IdHeroStats, out var s) && s.Enabled;
        set => SetWidgetState(OverviewWidgetCatalog.IdHeroStats, value);
    }

    public bool ShowRecentRuns
    {
        get => _layoutState.TryGetValue(OverviewWidgetCatalog.IdRecentRuns, out var s) && s.Enabled;
        set => SetWidgetState(OverviewWidgetCatalog.IdRecentRuns, value);
    }

    public bool ShowProxySummary
    {
        get => _layoutState.TryGetValue(OverviewWidgetCatalog.IdProxySummary, out var s) && s.Enabled;
        set => SetWidgetState(OverviewWidgetCatalog.IdProxySummary, value);
    }

    public bool ShowTrafficToday
    {
        get => _layoutState.TryGetValue(OverviewWidgetCatalog.IdTrafficToday, out var s) && s.Enabled;
        set => SetWidgetState(OverviewWidgetCatalog.IdTrafficToday, value);
    }

    public bool ShowAdDensity
    {
        get => _layoutState.TryGetValue(OverviewWidgetCatalog.IdAdDensity, out var s) && s.Enabled;
        set => SetWidgetState(OverviewWidgetCatalog.IdAdDensity, value);
    }

    private void SetWidgetState(string widgetId, bool value)
    {
        if (_layoutState.TryGetValue(widgetId, out var state) && state.Enabled != value)
        {
            // Just a local setter; actual persistence happens in the dialog.
            _layoutState[widgetId] = state with { Enabled = value };
            OnPropertyChanged(nameof(ShowHeroStats));
            OnPropertyChanged(nameof(ShowRecentRuns));
            OnPropertyChanged(nameof(ShowProxySummary));
            OnPropertyChanged(nameof(ShowTrafficToday));
            OnPropertyChanged(nameof(ShowAdDensity));
        }
    }

    [ObservableProperty] private AdDensitySummary? _adDensity;

    // ─── Lifecycle ────────────────────────────────────────────────

    public override async Task OnNavigatedToAsync()
    {
        // Load layout configuration first, before any reload.
        try
        {
            var layouts = await _layout.ListAsync();
            _layoutState = layouts.ToDictionary(s => s.WidgetId);
            OnPropertyChanged(nameof(ShowHeroStats));
            OnPropertyChanged(nameof(ShowRecentRuns));
            OnPropertyChanged(nameof(ShowProxySummary));
            OnPropertyChanged(nameof(ShowTrafficToday));
            OnPropertyChanged(nameof(ShowAdDensity));
            _log.LogDebug("Overview layout loaded: {WidgetCount} widgets", layouts.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load overview layout");
        }

        _refresh.Start();
        await ReloadAsync();
    }

    public override Task OnNavigatedFromAsync()
    {
        _refresh.Stop();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        _log.LogDebug("Overview: refreshing dashboard counters");
        try
        {
            // Run stats.
            var stats = await _runs.GetStatsAsync();
            TotalRuns      = stats.Total;
            SuccessfulRuns = stats.Successful;
            FailedRuns     = stats.Failed;
            RunningRuns    = stats.Running;

            // Counts.
            ProfileCount = (await _profiles.ListAsync()).Count;
            ProxyCount   = (await _proxies.ListAsync()).Count;

            // Vault status.
            await _vault.RefreshStateAsync();
            if (!_vault.IsInitialized)
            {
                VaultStatusText      = "Not set up";
                VaultStatusBrushKey  = "TextDim";
            }
            else if (_vault.IsUnlocked)
            {
                VaultStatusText      = "🔓  Unlocked";
                VaultStatusBrushKey  = "OkBrush";
            }
            else
            {
                VaultStatusText      = "🔒  Locked";
                VaultStatusBrushKey  = "WarnBrush";
            }

            // Traffic 24h snapshot.
            var summary = await _traffic.GetSummaryAsync(24);
            Traffic24hText        = ByteFormat.Human(summary.TotalBytes);
            TrafficRequestsText   = summary.TotalRequests.ToString("N0");
            TrafficActiveProfiles = summary.ProfileCount;
            TrafficUniqueDomains  = summary.DomainCount;

            // Active runs.
            ActiveProfiles.Clear();
            foreach (var n in _runner.ActiveProfileNames) ActiveProfiles.Add(n);
            OnPropertyChanged(nameof(HasActiveRuns));

            // Recent runs (top 8).
            var recent = await _runs.ListAsync(limit: 8);
            RecentRuns.Clear();
            foreach (var r in recent) RecentRuns.Add(RecentRun.From(r));

            // Ad density (if widget enabled).
            if (ShowAdDensity)
            {
                try
                {
                    AdDensity = await _adDensitySvc.GetSummaryAsync();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to load ad density data");
                    AdDensity = null;
                }
            }

            // Greeting.
            var hour = DateTime.Now.Hour;
            Greeting = hour switch
            {
                < 5  => "Late night",
                < 12 => "Good morning",
                < 17 => "Good afternoon",
                _    => "Good evening",
            };

            _log.LogInformation(
                "Overview loaded: {Total} runs ({Ok} ok, {Fail} fail, {Run} running), {Profiles} profiles, {Proxies} proxies, traffic24h={Bytes}",
                TotalRuns, SuccessfulRuns, FailedRuns, RunningRuns, ProfileCount, ProxyCount, Traffic24hText);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Overview refresh failed");
        }
    }

    // ─── Quick actions ─────────────────────────────────────────────

    // Phase 71v — every Overview-originated nav passes pushHistory=true
    // so the destination page surfaces a Back chip in the title bar.
    // From the user's POV: clicking a tile = "drill in", not "switch
    // sections", so a Back affordance is the natural pair.
    [RelayCommand] private void GoProfiles()    => _nav.NavigateTo("profiles", pushHistory: true);
    [RelayCommand] private void GoScripts()     => _nav.NavigateTo("scripts", pushHistory: true);
    [RelayCommand] private void GoVault()       => _nav.NavigateTo("vault", pushHistory: true);
    [RelayCommand] private void GoFingerprint() => _nav.NavigateTo("fingerprint", pushHistory: true);
    [RelayCommand] private void GoScheduler()   => _nav.NavigateTo("scheduler", pushHistory: true);
    [RelayCommand] private void GoTraffic()     => _nav.NavigateTo("traffic", pushHistory: true);
    [RelayCommand] private void GoLogs()        => _nav.NavigateTo("logs", pushHistory: true);
    // Phase 71u — "View all runs" on the Recent Activity card was
    // pointing at GoLogsCommand which navigates to the Logs page.
    // Logs is the raw text-stream view; what the button label
    // promises is the Runs history grid. Add a dedicated command.
    [RelayCommand] private void GoRuns()        => _nav.NavigateTo("runs", pushHistory: true);
    [RelayCommand] private void GoProxy()       => _nav.NavigateTo("proxy", pushHistory: true);
    [RelayCommand] private void GoExtensions()  => _nav.NavigateTo("extensions", pushHistory: true);

    [RelayCommand]
    public async Task ConfigureLayoutAsync()
    {
        var changed = Application.Current.Dispatcher.Invoke(() =>
            ShowOverviewLayoutDialog());

        if (changed)
        {
            // Reload the layout to reflect user changes.
            try
            {
                var layouts = await _layout.ListAsync();
                _layoutState = layouts.ToDictionary(s => s.WidgetId);
                OnPropertyChanged(nameof(ShowHeroStats));
                OnPropertyChanged(nameof(ShowRecentRuns));
                OnPropertyChanged(nameof(ShowProxySummary));
                OnPropertyChanged(nameof(ShowTrafficToday));
                OnPropertyChanged(nameof(ShowAdDensity));
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to reload layout after configure");
            }
        }
    }

    private bool ShowOverviewLayoutDialog()
    {
        // `App.Dialogs.X` would parse as a member access on the WPF
        // App class (we're in the `GhostShell.App.ViewModels`
        // namespace, so `App` resolves to the partial class first).
        // The `using GhostShell.App.Dialogs;` import lets us drop the
        // namespace prefix entirely.
        var dlg = new OverviewLayoutDialog(_layout)
        {
            Owner = Application.Current.MainWindow,
        };
        return dlg.ShowDialog() == true;
    }
}

/// <summary>One row in the Recent activity panel.</summary>
public sealed record RecentRun
{
    public long Id              { get; init; }
    public string ProfileName   { get; init; } = "";
    public string Status        { get; init; } = "";
    public string StatusIcon    { get; init; } = "•";
    public string StatusBrushKey{ get; init; } = "TextDim";
    public DateTime StartedAt   { get; init; }
    public string Relative      { get; init; } = "";
    public string DurationLabel { get; init; } = "";

    public static RecentRun From(Run r)
    {
        // Run model exposes ExitCode (null = running, 0 = success,
        // anything else = failed) — fold to a small icon + theme key.
        var (icon, brush, label) = r.IsRunning
            ? ("⏵", "Accent",  "running")
            : r.IsSuccess
                ? ("✓", "OkBrush", "OK")
                : ("✗", "ErrBrush", $"exit {r.ExitCode}");
        var rel = HumanRelative(r.StartedAt);
        var dur = r.FinishedAt is { } f
            ? FormatDuration(f - r.StartedAt)
            : "in flight";
        return new RecentRun
        {
            Id            = r.Id,
            ProfileName   = r.ProfileName,
            Status        = label,
            StatusIcon    = icon,
            StatusBrushKey= brush,
            StartedAt     = r.StartedAt,
            Relative      = rel,
            DurationLabel = dur,
        };
    }

    /// <summary>Human-readable "X ago" for a timestamp. Tolerates
    /// DateTimeKind.Unspecified (Dapper hands them back from SQLite
    /// in that shape) by treating non-UTC values as local time, then
    /// folding to UTC for the diff. Negative diffs (clock skew /
    /// future-dated rows) collapse to "just now" instead of the bogus
    /// "-9198s ago" we used to emit.</summary>
    private static string HumanRelative(DateTime ts)
    {
        var utc = ts.Kind switch
        {
            DateTimeKind.Utc          => ts,
            DateTimeKind.Local        => ts.ToUniversalTime(),
            // Unspecified — assume the row was stamped in LOCAL time
            // (RealProfileRunner uses DateTime.Now). Convert it.
            _ => DateTime.SpecifyKind(ts, DateTimeKind.Local).ToUniversalTime(),
        };
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 0) return "just now";
        return HumanDuration(d) + " ago";
    }

    /// <summary>Format a TimeSpan as a multi-component human-friendly
    /// string. Examples:
    ///   3.4s      → "3 sec"
    ///   72s       → "1 min 12 sec"
    ///   3754s     → "1 hr 2 min 34 sec"
    ///   2 days    → "2 days 3 hr"
    ///   8 days    → "8 days"
    /// Mirrors what the user asked for ("3 hours 3 min 30 sec").</summary>
    public static string HumanDuration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        var totalSec = (long)d.TotalSeconds;
        if (totalSec < 1) return "just now";

        var days = (int)d.TotalDays;
        if (days >= 7) return $"{days} days";
        if (days >= 1)
        {
            var hr = d.Hours;
            return hr > 0 ? $"{days}d {hr}h" : $"{days}d";
        }

        var hours = (int)d.TotalHours;
        var min   = d.Minutes;
        var sec   = d.Seconds;

        if (hours >= 1)
        {
            // "3 hr 3 min 30 sec" — drop trailing zero components for compactness.
            if (sec == 0 && min == 0) return $"{hours} hr";
            if (sec == 0)              return $"{hours} hr {min} min";
            return $"{hours} hr {min} min {sec} sec";
        }
        if (min >= 1)
        {
            return sec == 0 ? $"{min} min" : $"{min} min {sec} sec";
        }
        return $"{sec} sec";
    }

    private static string FormatDuration(TimeSpan d) => HumanDuration(d);
}
