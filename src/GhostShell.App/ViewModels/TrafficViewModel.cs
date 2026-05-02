// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 28 — Traffic dashboard VM. Mirrors the legacy web's traffic.js
/// surface: range dropdown, total stat-bar, line chart, by-profile +
/// by-domain tables. Auto-refreshes every 30 s while the page is open.
/// </summary>
public sealed partial class TrafficViewModel : BaseViewModel
{
    private readonly ITrafficService _traffic;
    private readonly IDialogService _dialogs;
    private readonly ILogger<TrafficViewModel> _log;
    private readonly DispatcherTimer _autoRefresh;

    public TrafficViewModel(
        ITrafficService traffic,
        IDialogService dialogs,
        ILogger<TrafficViewModel> log)
    {
        _traffic = traffic;
        _dialogs = dialogs;
        _log     = log;
        _autoRefresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _autoRefresh.Tick += async (_, _) => await ReloadAsync();
    }

    // ─── Range dropdown ───────────────────────────────────────────────

    public IReadOnlyList<RangeOption> RangeOptions { get; } = new[]
    {
        new RangeOption("Last hour",     1),
        new RangeOption("Last 6 hours",  6),
        new RangeOption("Last 24 hours", 24),
        new RangeOption("Last 7 days",   24 * 7),
        new RangeOption("Last 30 days",  24 * 30),
        new RangeOption("Last 90 days",  24 * 90),
    };

    [ObservableProperty] private RangeOption _selectedRange = new("Last 24 hours", 24);

    // ─── Stat bar ─────────────────────────────────────────────────────

    [ObservableProperty] private string _totalBytesText    = "—";
    [ObservableProperty] private string _totalRequestsText = "—";
    [ObservableProperty] private string _profileCountText  = "—";
    [ObservableProperty] private string _domainCountText   = "—";
    [ObservableProperty] private string _avgBytesText      = "—";

    // ─── Tables ───────────────────────────────────────────────────────

    public ObservableCollection<TrafficByProfile> ByProfile { get; } = new();
    public ObservableCollection<TrafficByDomain>  ByDomain  { get; } = new();
    /// <summary>Sentinel that means "no filter, show all profiles" in
    /// the domains dropdown. We use a real label instead of an empty
    /// string so the ComboBox doesn't render a blank-looking row at
    /// the top.</summary>
    public const string AllProfilesSentinel = "(All profiles)";

    public ObservableCollection<string> ProfileFilters { get; } = new();
    [ObservableProperty] private string? _domainProfileFilter; // sentinel when null / "all"
    [ObservableProperty] private bool _isEmpty = true;

    // ─── Time series for the chart ────────────────────────────────────

    public ObservableCollection<TrafficTimePoint> Series { get; } = new();
    [ObservableProperty] private string _chartCaption = "";

    // ─── Lifecycle ────────────────────────────────────────────────────

    public override async Task OnNavigatedToAsync()
    {
        _autoRefresh.Start();
        await ReloadAsync();
    }

    public override Task OnNavigatedFromAsync()
    {
        _autoRefresh.Stop();
        return Task.CompletedTask;
    }

    partial void OnSelectedRangeChanged(RangeOption value) => _ = ReloadAsync();
    partial void OnDomainProfileFilterChanged(string? value) => _ = ReloadDomainsAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var hours = SelectedRange.Hours;
            var bucket = hours <= 48 ? "hour" : "day";

            var summary = await _traffic.GetSummaryAsync(hours, bucket);
            TotalBytesText    = FormatBytes(summary.TotalBytes);
            TotalRequestsText = summary.TotalRequests.ToString("N0");
            ProfileCountText  = summary.ProfileCount.ToString();
            DomainCountText   = summary.DomainCount.ToString();
            AvgBytesText      = summary.TotalRequests > 0
                ? FormatBytes(summary.TotalBytes / summary.TotalRequests) + " / req"
                : "—";

            Series.Clear();
            foreach (var p in summary.Timeseries) Series.Add(p);
            ChartCaption = $"{summary.Timeseries.Count} {bucket}-buckets · " +
                           $"{FormatBytes(summary.TotalBytes)} total";

            var byProfile = await _traffic.GetByProfileAsync(hours);
            ByProfile.Clear();
            foreach (var r in byProfile) ByProfile.Add(r);
            ProfileFilters.Clear();
            ProfileFilters.Add(AllProfilesSentinel);
            foreach (var p in byProfile) ProfileFilters.Add(p.ProfileName);
            // Preserve current selection if it's still valid; otherwise
            // fall back to "(All profiles)".
            if (DomainProfileFilter is null ||
                !ProfileFilters.Contains(DomainProfileFilter))
                DomainProfileFilter = AllProfilesSentinel;

            await ReloadDomainsAsync();

            IsEmpty = summary.TotalBytes == 0 && summary.TotalRequests == 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Traffic reload failed");
            await _dialogs.ConfirmAsync("Traffic load failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
        finally { IsBusy = false; }
    }

    private async Task ReloadDomainsAsync()
    {
        try
        {
            // Treat the sentinel + null/empty as "no filter".
            var raw = DomainProfileFilter;
            var profile = string.IsNullOrWhiteSpace(raw) ||
                          string.Equals(raw, AllProfilesSentinel, StringComparison.Ordinal)
                ? null : raw;
            var rows = await _traffic.GetByDomainAsync(SelectedRange.Hours, 50, profile);
            ByDomain.Clear();
            foreach (var r in rows) ByDomain.Add(r);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ByDomain reload failed");
        }
    }

    // ─── Format helpers ───────────────────────────────────────────────

    /// <summary>Forwards to the shared <see cref="GhostShell.Core.Common.ByteFormat"/>
    /// helper. Kept as a static here so the chart code-behind doesn't
    /// need to add a using directive.</summary>
    public static string FormatBytes(long bytes) => GhostShell.Core.Common.ByteFormat.Human(bytes);
}

/// <summary>One entry in the range dropdown.</summary>
public sealed record RangeOption(string Label, int Hours)
{
    public override string ToString() => Label;
}
