// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 34 — Competitors dashboard VM. Displays observed competitor
/// ads with KPI tiles, volume trend chart, and four detail tabs
/// (leaderboard, by-domain, by-query, recent ads).
/// </summary>
public sealed partial class CompetitorsViewModel : BaseViewModel
{
    private readonly ICompetitorService _competitors;
    private readonly IDomainListService _domainLists;
    private readonly IDialogService _dialogs;
    private readonly ILogger<CompetitorsViewModel> _log;
    private CancellationTokenSource? _debounce;

    public CompetitorsViewModel(
        ICompetitorService competitors,
        IDomainListService domainLists,
        IDialogService dialogs,
        ILogger<CompetitorsViewModel> log)
    {
        _competitors = competitors;
        _domainLists = domainLists;
        _dialogs = dialogs;
        _log = log;
    }

    // ─── Search + Period ──────────────────────────────────────────────

    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private int _days = 7;  // 1, 7, 30, or 0 (all)
    [ObservableProperty] private bool _isLoading;

    partial void OnSearchTextChanged(string? value)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        _ = Task.Delay(300, token).ContinueWith(async _ =>
        {
            if (!token.IsCancellationRequested)
                await ReloadAsync(token);
        });
    }

    partial void OnDaysChanged(int value) => _ = ReloadAsync();

    // ─── KPI Tiles ────────────────────────────────────────────────────

    [ObservableProperty] private int _recordsCount;
    [ObservableProperty] private int _uniqueDomainsCount;
    [ObservableProperty] private int _newDomainsCount;
    [ObservableProperty] private int _activeDomainCount;
    [ObservableProperty] private int _quietingDomainsCount;

    // ─── Tab Selection ────────────────────────────────────────────────

    [ObservableProperty] private bool _leaderboardSelected = true;
    [ObservableProperty] private bool _byDomainSelected;
    [ObservableProperty] private bool _byQuerySelected;
    [ObservableProperty] private bool _recentAdsSelected;

    // ─── Collections ──────────────────────────────────────────────────

    public ObservableCollection<CompetitorLeaderRow> LeaderboardRows { get; } = new();
    public ObservableCollection<CompetitorByQueryRow> ByQueryRows { get; } = new();
    public ObservableCollection<CompetitorRecord> RecentAds { get; } = new();
    public ObservableCollection<CompetitorTrendSeries> ChartSeries { get; } = new();

    // ─── Chart State ──────────────────────────────────────────────────

    [ObservableProperty] private string _chartKind = "line";  // "line" or "stacked"

    // ─── Lifecycle ────────────────────────────────────────────────────

    public override async Task OnNavigatedToAsync()
    {
        await ReloadAsync();
    }

    // ─── Commands ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ReloadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var kpis = await _competitors.GetKpisAsync(Days, ct);
            var leaderboard = await _competitors.GetLeaderboardAsync(Days, SearchText, 100, ct);
            var trend = await _competitors.GetTrendAsync(Days, 8, ct);
            var recent = await _competitors.GetRecentAsync(Days, 200, ct);
            var byQuery = await _competitors.GetByQueryAsync(Days, 50, ct);

            // Update KPI tiles
            RecordsCount = kpis.Records;
            UniqueDomainsCount = kpis.UniqueDomains;
            NewDomainsCount = kpis.NewDomains;
            ActiveDomainCount = kpis.ActiveDomains;
            QuietingDomainsCount = kpis.QuietingDomains;

            // Update collections on the UI thread. WPF's Dispatcher
            // doesn't have an `UIThread` static — that's Avalonia's
            // syntax. The right call here is the Application's own
            // dispatcher, which lives on the UI thread by definition.
            Application.Current.Dispatcher.Invoke(() =>
            {
                LeaderboardRows.Clear();
                foreach (var row in leaderboard)
                    LeaderboardRows.Add(row);

                ByQueryRows.Clear();
                foreach (var row in byQuery)
                    ByQueryRows.Add(row);

                RecentAds.Clear();
                foreach (var row in recent)
                    RecentAds.Add(row);

                UpdateChartSeries(trend);
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Competitors reload failed");
            await _dialogs.ConfirmAsync("Competitors load failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void SetPeriod(string daysCode)
    {
        Days = daysCode switch
        {
            "24h" => 1,
            "7d" => 7,
            "30d" => 30,
            "all" => 0,
            _ => 7
        };
    }

    [RelayCommand]
    private void ToggleChartKind()
    {
        ChartKind = ChartKind == "line" ? "stacked" : "line";
    }

    [RelayCommand]
    private async Task AddToTarget(CompetitorLeaderRow row)
    {
        try
        {
            var added = await _domainLists.AddAsync(DomainListKind.Target, row.Domain);
            if (added)
                await _dialogs.ConfirmAsync("Added to Target",
                    $"{row.Domain} added to Target list",
                    "OK", ConfirmSeverity.Success);
            else
                await _dialogs.ConfirmAsync("Already in list",
                    $"{row.Domain} is already in Target list",
                    "OK", ConfirmSeverity.Info);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "AddToTarget failed");
            await _dialogs.ConfirmAsync("Error", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task AddToBlock(CompetitorLeaderRow row)
    {
        try
        {
            var added = await _domainLists.AddAsync(DomainListKind.Block, row.Domain);
            if (added)
                await _dialogs.ConfirmAsync("Added to Block",
                    $"{row.Domain} added to Block list",
                    "OK", ConfirmSeverity.Success);
            else
                await _dialogs.ConfirmAsync("Already in list",
                    $"{row.Domain} is already in Block list",
                    "OK", ConfirmSeverity.Info);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "AddToBlock failed");
            await _dialogs.ConfirmAsync("Error", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"competitors-{DateTime.Now:yyyyMMdd}.csv",
                DefaultExt = ".csv",
                Filter = "CSV files (.csv)|*.csv|All files|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            if (dialog.ShowDialog() == true)
            {
                var csv = BuildLeaderboardCsv();
                await File.WriteAllTextAsync(dialog.FileName, csv);
                await _dialogs.ConfirmAsync("Exported",
                    $"Leaderboard exported to {Path.GetFileName(dialog.FileName)}",
                    "OK", ConfirmSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Export CSV failed");
            await _dialogs.ConfirmAsync("Export failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ExportJson()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"competitors-{DateTime.Now:yyyyMMdd}.json",
                DefaultExt = ".json",
                Filter = "JSON files (.json)|*.json|All files|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            if (dialog.ShowDialog() == true)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(
                    LeaderboardRows.ToList(),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
                );
                await File.WriteAllTextAsync(dialog.FileName, json);
                await _dialogs.ConfirmAsync("Exported",
                    $"Leaderboard exported to {Path.GetFileName(dialog.FileName)}",
                    "OK", ConfirmSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Export JSON failed");
            await _dialogs.ConfirmAsync("Export failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private void UpdateChartSeries(IReadOnlyList<CompetitorTrendPoint> trend)
    {
        ChartSeries.Clear();

        // Group by domain
        var byDomain = trend.GroupBy(p => p.Domain).OrderByDescending(g => g.Sum(p => p.Mentions));

        // Color palette (8 colors)
        var colors = new[]
        {
            "#00D9FF",  // Cyan
            "#FF006E",  // Magenta
            "#00FF00",  // Lime
            "#FFB700",  // Gold
            "#FF4500",  // OrangeRed
            "#9D4EDD",  // Purple
            "#3A86FF",  // Blue
            "#FB5607",  // Orange
        };

        int idx = 0;
        foreach (var group in byDomain)
        {
            var color = colors[idx % colors.Length];
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            var points = group.OrderBy(p => p.Date)
                .Select(p => (p.Date, p.Mentions))
                .ToList();

            ChartSeries.Add(new CompetitorTrendSeries
            {
                Domain = group.Key,
                Points = points,
                LineBrush = brush
            });
            idx++;
        }
    }

    private string BuildLeaderboardCsv()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Domain,Mentions,MentionsPrev,Queries,Clicks,LastSeen,IsNew");
        foreach (var row in LeaderboardRows)
        {
            sb.AppendLine($"\"{row.Domain}\",{row.Mentions},{row.MentionsPrev}," +
                           $"{row.QueriesCount},{row.ClicksCount},{row.LastSeen:O},{row.IsNew}");
        }
        return sb.ToString();
    }
}

/// <summary>One series on the volume trend chart.</summary>
public sealed record CompetitorTrendSeries
{
    public required string Domain { get; init; }
    public required IReadOnlyList<(DateTime Date, int Mentions)> Points { get; init; }
    public required Brush LineBrush { get; init; }
}
