// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.App.Navigation;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Runs page ViewModel — port of <c>dashboard/pages/runs.html</c> +
/// <c>js/pages/runs.js</c>. Mirrors the legacy UX:
///
///   • Single bulk fetch (latest 500), then all filters apply
///     client-side. The web version went this route because the
///     "select-then-paginate-then-filter" UX felt instant. We keep
///     that contract — the filters here mutate <see cref="View"/>
///     synchronously.
///
///   • Filters: status, profile, time-range (hours), search. Same
///     four the web version exposes.
///
///   • Pagination: 25 / 50 / 100 / 200 page sizes; "current page"
///     state mirrors the web. Resetting to page 1 happens on any
///     filter change.
///
///   • Stats counters reflect the UNFILTERED set (so "12 total /
///     8 ok / 3 failed / 1 running" doesn't shift around as you
///     type into the search box). Plus a separate "shown" counter
///     for the post-filter count.
///
///   • Auto-refresh on <see cref="IProfileRunner.ActiveChanged"/>
///     so the user sees a new row appear immediately when a
///     profile starts/stops without re-navigating the page.
/// </summary>
public sealed partial class RunsViewModel : BaseViewModel
{
    private const int FetchLimit = 500;

    private readonly IRunService _runs;
    private readonly IProfileRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _nav;
    private readonly LogsViewModel _logs;
    private readonly ILogger<RunsViewModel> _log;

    /// <summary>The full snapshot from the last server fetch.
    /// Filtering / paging operates on this; never on the visible
    /// collection directly.</summary>
    private readonly List<Run> _all = new();

    public RunsViewModel(
        IRunService runs,
        IProfileRunner runner,
        IDialogService dialogs,
        INavigationService nav,
        LogsViewModel logs,
        ILogger<RunsViewModel> log)
    {
        _runs    = runs;
        _runner  = runner;
        _dialogs = dialogs;
        _nav     = nav;
        _logs    = logs;
        _log     = log;

        _runner.ActiveChanged += (_, _) =>
        {
            if (Application.Current?.Dispatcher is { } d)
                d.BeginInvoke(() => _ = ReloadAsync());
        };
    }

    // ─── Filter state ────────────────────────────────────────────

    public IReadOnlyList<RunStatusFilterOption> StatusOptions { get; } = new[]
    {
        new RunStatusFilterOption("All",        RunStatusFilter.All),
        new RunStatusFilterOption("Successful", RunStatusFilter.Successful),
        new RunStatusFilterOption("Failed",     RunStatusFilter.Failed),
        new RunStatusFilterOption("Running",    RunStatusFilter.Running),
    };

    public IReadOnlyList<TimeRangeOption> TimeRangeOptions { get; } = new[]
    {
        new TimeRangeOption("All time",    null),
        new TimeRangeOption("Last hour",   1),
        new TimeRangeOption("Last 6h",     6),
        new TimeRangeOption("Last 24h",    24),
        new TimeRangeOption("Last 3 days", 72),
        new TimeRangeOption("Last 7 days", 168),
        new TimeRangeOption("Last 30 days",720),
    };

    public IReadOnlyList<int> PageSizeOptions { get; } = new[] { 25, 50, 100, 200 };

    public ObservableCollection<string> ProfileFilterOptions { get; } = new();

    [ObservableProperty] private RunStatusFilter _statusFilter = RunStatusFilter.All;
    [ObservableProperty] private string? _profileFilter;
    [ObservableProperty] private int? _timeRangeHours;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _pageSize = 50;
    [ObservableProperty] private int _currentPage = 1;

    // ─── Visible result ──────────────────────────────────────────

    public ObservableCollection<Run> Items { get; } = new();

    [ObservableProperty] private int _total;
    [ObservableProperty] private int _successful;
    [ObservableProperty] private int _failed;
    [ObservableProperty] private int _running;
    /// <summary>How many rows match the filters (post-filter, pre-paging).</summary>
    [ObservableProperty] private int _shown;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isFiltered;

    public override async Task OnNavigatedToAsync() => await ReloadAsync();

    // ─── Filter change → reset to page 1, recompute ──────────────
    partial void OnStatusFilterChanged(RunStatusFilter value)   { CurrentPage = 1; ApplyFilters(); }
    partial void OnProfileFilterChanged(string? value)          { CurrentPage = 1; ApplyFilters(); }
    partial void OnTimeRangeHoursChanged(int? value)            { CurrentPage = 1; ApplyFilters(); }
    partial void OnSearchTextChanged(string value)              { CurrentPage = 1; ApplyFilters(); }
    partial void OnPageSizeChanged(int value)                   { CurrentPage = 1; ApplyFilters(); }
    partial void OnCurrentPageChanged(int value)                { ApplyFilters(); }

    // ─── Commands ────────────────────────────────────────────────

    [RelayCommand]
    private async Task ReloadAsync()
    {
        _log.LogDebug("Runs: loading bulk snapshot (limit={Limit})", FetchLimit);
        IsBusy = true;
        try
        {
            var rows = await _runs.ListAsync(limit: FetchLimit);
            _all.Clear();
            _all.AddRange(rows);

            // Unfiltered stats — mirror the web behaviour. These are
            // headline counters that should NOT shift as the user
            // changes filters.
            var stats = await _runs.GetStatsAsync();
            Total      = stats.Total;
            Successful = stats.Successful;
            Failed     = stats.Failed;
            Running    = stats.Running;

            RebuildProfileFilterOptions();
            ApplyFilters();

            _log.LogInformation(
                "Runs loaded: {Count} fetched, stats t={T}/s={S}/f={F}/r={R}",
                _all.Count, Total, Successful, Failed, Running);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Runs list failed");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task MarkFailedAsync(Run? selected)
    {
        if (selected is null || !selected.IsRunning) return;

        var ok = await _dialogs.ConfirmAsync(
            $"Mark run #{selected.Id} as failed?",
            "Use this when a run looks stuck (no heartbeat, browser " +
            "window long gone). It records exit_code = -99 and " +
            $"frees '{selected.ProfileName}' so you can launch again.",
            "Mark failed",
            ConfirmSeverity.Danger);
        if (!ok) return;

        try
        {
            await _runs.MarkFailedAsync(selected.Id);
            _log.LogInformation("Run #{Run} marked failed by user", selected.Id);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not mark run #{Run} failed", selected.Id);
            await _dialogs.ConfirmAsync("Could not mark run failed",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    /// <summary>
    /// Parameter is a STRING, not <c>int?</c>, even though the
    /// numbers are integers. WPF passes XAML literals like
    /// <c>CommandParameter="30"</c> as strings; binding a
    /// <c>RelayCommand&lt;int?&gt;</c> to that throws
    /// <c>ArgumentException</c> at layout-update time and cascades
    /// into "Profiles page renders empty" because the dispatcher
    /// exception breaks the data-binding cycle. Accept string, parse
    /// in-method — XAML-friendly and crash-free.
    /// </summary>
    [RelayCommand]
    private async Task ClearAsync(string? olderThanDaysParam)
    {
        int? olderThanDays = int.TryParse(olderThanDaysParam, out var d) ? d : null;
        var cutoff = olderThanDays is > 0
            ? DateTime.UtcNow.AddDays(-olderThanDays.Value)
            : (DateTime?)null;

        var label = olderThanDays switch
        {
            null   => "ALL finished runs",
            1      => "runs older than 1 day",
            7      => "runs older than 7 days",
            30     => "runs older than 30 days",
            _      => $"runs older than {olderThanDays} days",
        };

        var ok = await _dialogs.ConfirmAsync(
            $"Clear {label}?",
            "Active (still-running) rows are never deleted by this " +
            "action. Finished runs are removed permanently — there's " +
            "no undo.",
            "Clear",
            ConfirmSeverity.Danger);
        if (!ok) return;

        try
        {
            var removed = await _runs.ClearAsync(cutoff);
            _log.LogInformation("Cleared {Count} run row(s) (cutoff={Cutoff})",
                removed, cutoff?.ToString("O") ?? "(all)");
            await ReloadAsync();
            await _dialogs.ConfirmAsync("Cleared",
                $"{removed} row(s) removed.", "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Clear runs failed");
            await _dialogs.ConfirmAsync("Could not clear",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    // ─── Filter / page logic ─────────────────────────────────────

    private void RebuildProfileFilterOptions()
    {
        // Preserve the current selection across reloads — if a user
        // had "site_x" picked and the next reload still contains
        // site_x rows, they should stay selected.
        var keep = ProfileFilter;
        ProfileFilterOptions.Clear();
        ProfileFilterOptions.Add("(all profiles)");
        foreach (var name in _all
                     .Select(r => r.ProfileName)
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            ProfileFilterOptions.Add(name);
        }
        ProfileFilter = keep is not null && ProfileFilterOptions.Contains(keep)
            ? keep
            : ProfileFilterOptions.FirstOrDefault();
    }

    private void ApplyFilters()
    {
        IEnumerable<Run> q = _all;

        // Status
        q = StatusFilter switch
        {
            RunStatusFilter.Successful => q.Where(r => r.IsSuccess),
            RunStatusFilter.Failed     => q.Where(r => r.IsFailed),
            RunStatusFilter.Running    => q.Where(r => r.IsRunning),
            _                          => q,
        };

        // Profile (skip filter if "(all profiles)" selected, which is
        // the sentinel index 0 in the options list).
        if (!string.IsNullOrWhiteSpace(ProfileFilter)
            && !ProfileFilter.StartsWith("(", StringComparison.Ordinal))
        {
            q = q.Where(r => string.Equals(r.ProfileName, ProfileFilter,
                StringComparison.OrdinalIgnoreCase));
        }

        // Time range
        if (TimeRangeHours is > 0)
        {
            var cutoff = DateTime.UtcNow.AddHours(-TimeRangeHours.Value);
            q = q.Where(r => r.StartedAt >= cutoff);
        }

        // Free-text search — match against Id, ProfileName, ExitCode.
        // Same shape as runs.js _matchesSearch.
        var needle = SearchText?.Trim();
        if (!string.IsNullOrEmpty(needle))
        {
            var n = needle;
            q = q.Where(r =>
                r.Id.ToString().Contains(n, StringComparison.OrdinalIgnoreCase) ||
                ($"#{r.Id}").Contains(n, StringComparison.OrdinalIgnoreCase) ||
                (r.ProfileName?.Contains(n, StringComparison.OrdinalIgnoreCase) ?? false) ||
                ($"exit:{r.ExitCode}").Contains(n, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = q.ToList();
        Shown       = filtered.Count;
        IsFiltered  = StatusFilter != RunStatusFilter.All
                   || (ProfileFilter is { } pf && !pf.StartsWith("(", StringComparison.Ordinal))
                   || TimeRangeHours is > 0
                   || !string.IsNullOrEmpty(needle);

        // Paging
        TotalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)PageSize));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
        if (CurrentPage < 1)         CurrentPage = 1;

        var page = filtered
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        Items.Clear();
        foreach (var r in page) Items.Add(r);
        IsEmpty = Items.Count == 0;
    }

    /// <summary>
    /// Phase 53 — per-row "Delete" action. Removes a finished run
    /// from history permanently (no undo). Prompts with a styled
    /// confirm dialog showing the run's profile and timestamp.
    /// Only works on finished (non-running) rows.
    /// </summary>
    [RelayCommand]
    private async Task DeleteRunAsync(Run? selected)
    {
        if (selected is null || selected.IsRunning) return;

        var ok = await _dialogs.ConfirmAsync(
            "Delete run?",
            $"Run #{selected.Id} from profile '{selected.ProfileName}' " +
            $"started at {selected.StartedAt:yyyy-MM-dd HH:mm:ss} will be " +
            "permanently deleted from history. This cannot be undone.",
            "Delete",
            ConfirmSeverity.Warning);
        if (!ok) return;

        try
        {
            var deleted = await _runs.DeleteAsync(selected.Id);
            if (deleted)
            {
                _log.LogInformation("Run #{Run} deleted by user", selected.Id);
                await ReloadAsync();
            }
            else
            {
                _log.LogWarning("Run #{Run} could not be deleted (still running or missing)", selected.Id);
                await _dialogs.ConfirmAsync("Could not delete",
                    "Run is still active or no longer exists.", "OK", ConfirmSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not delete run #{Run}", selected.Id);
            await _dialogs.ConfirmAsync("Could not delete run",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    /// <summary>
    /// Per-row "View logs" action. Mirrors the legacy
    /// <c>Runs.viewLogs(runId)</c>: pin the Logs page to the run's
    /// profile + window so the user lands on already-narrowed
    /// output instead of the full live tail. The desktop port
    /// doesn't load a separate /api/logs/history endpoint — logs
    /// live in the file tail and the live filter does the work.
    /// </summary>
    [RelayCommand]
    private void ViewLogs(Run? run)
    {
        if (run is null) return;
        _logs.FilterForRun(run);
        // Phase 71kk — pushHistory:true so the title-bar Back chip
        // appears on the Logs page and returns the user to Runs.
        // Without the push the chip stays hidden (sidebar-style
        // "root nav" semantics) and the user has to re-find Runs in
        // the sidebar to get back.
        _nav.NavigateTo("logs", pushHistory: true);
        _log.LogDebug("Runs: navigated to logs filtered for run #{Run} ({Profile})",
            run.Id, run.ProfileName);
    }

    [RelayCommand] private void FirstPage()    { CurrentPage = 1; }
    [RelayCommand] private void PrevPage()     { CurrentPage = Math.Max(1, CurrentPage - 1); }
    [RelayCommand] private void NextPage()     { CurrentPage = Math.Min(TotalPages, CurrentPage + 1); }
    [RelayCommand] private void LastPage()     { CurrentPage = TotalPages; }
    [RelayCommand] private void ResetFilters()
    {
        StatusFilter    = RunStatusFilter.All;
        ProfileFilter   = ProfileFilterOptions.FirstOrDefault();
        TimeRangeHours  = null;
        SearchText      = "";
    }
}

public sealed record RunStatusFilterOption(string Label, RunStatusFilter Value);
public sealed record TimeRangeOption(string Label, int? Hours);
