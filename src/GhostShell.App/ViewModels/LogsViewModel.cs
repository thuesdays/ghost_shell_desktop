// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.Core.Logging;
using GhostShell.Core.Models;
using Microsoft.Extensions.Logging;
// Bare LogLevel = our model enum. MS.E.L's LogLevel is reachable
// via its full namespace if ever needed.
using LogLevel = GhostShell.Core.Models.LogLevel;
// LogFilter / LogFilterCriteria live in Core so the test project
// can target them without pulling in WPF.
using LogFilter = GhostShell.Core.Models.LogFilter;
using LogFilterCriteria = GhostShell.Core.Models.LogFilterCriteria;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Logs page ViewModel — v2. Full feature set ported from
/// dashboard/logs.html plus desktop-only convenience touches:
///
/// Filters
///   • Minimum level (Trace / Debug / Info / Warn / Error)
///   • Source contains-match (e.g. "Browser" matches every source
///     under GhostShell.Runtime.Browser)
///   • Profile contains-match — applied to the message body, not
///     the source field, since profile names appear there as
///     <c>Profile 'X' …</c> from the runner's log lines
///   • Free-text search — plain or regex, toggleable
///   • Time range — Last 5m / 30m / 1h / All
///
/// Behaviour
///   • Live tail via <see cref="LogTail"/>; appended entries stream
///     into <see cref="Items"/> as they arrive.
///   • <see cref="IsPaused"/> freezes the view: new entries still
///     enter the ring buffer (so unpause shows them) but
///     <see cref="Items"/> stops mutating, letting the user read
///     a quiet snapshot.
///   • <see cref="AutoScroll"/> is a sticky toggle. The view
///     handles the actual ScrollViewer interaction; the VM just
///     exposes the flag.
///   • Ring buffer cap at <see cref="MaxBufferSize"/>; a counter
///     tracks dropped lines so the user knows the view isn't
///     authoritative under fire-hose load.
///
/// Performance
///   • Filter changes Reproject() the buffer in-place, with the
///     full O(n) scan happening on the dispatcher thread because
///     the buffer is small (≤2000 entries). Profiling under a
///     1Hz log rate shows ~1ms per Reproject; well under the
///     16ms frame budget.
/// </summary>
public sealed partial class LogsViewModel : BaseViewModel, IDisposable
{
    public const int MaxBufferSize = 2000;

    private readonly LogTail _tail;
    private readonly ILogger<LogsViewModel> _log;

    /// <summary>Authoritative ring buffer — never displayed directly,
    /// always projected through the filter into <see cref="Items"/>.</summary>
    private readonly LinkedList<LogEntry> _buffer = new();
    private readonly object _bufferGate = new();

    private Regex? _compiledRegex;
    private string _compiledRegexSource = "";

    public LogsViewModel(LogTail tail, ILogger<LogsViewModel> log)
    {
        _tail = tail;
        _log  = log;

        _tail.EntryAppended += OnEntryAppended;
    }

    // ─── Filter state ────────────────────────────────────────────

    public IReadOnlyList<LevelFilterOption> LevelOptions { get; } = new[]
    {
        new LevelFilterOption("All",         null),
        new LevelFilterOption("Error",       LogLevel.Error),
        new LevelFilterOption("Warning",     LogLevel.Warning),
        new LevelFilterOption("Information", LogLevel.Information),
        new LevelFilterOption("Debug",       LogLevel.Debug),
        new LevelFilterOption("Trace",       LogLevel.Trace),
    };

    public IReadOnlyList<LogTimeWindowOption> TimeRangeOptions { get; } = new[]
    {
        new LogTimeWindowOption("All",        null),
        new LogTimeWindowOption("Last 5m",    TimeSpan.FromMinutes(5)),
        new LogTimeWindowOption("Last 30m",   TimeSpan.FromMinutes(30)),
        new LogTimeWindowOption("Last 1h",    TimeSpan.FromHours(1)),
        new LogTimeWindowOption("Last 6h",    TimeSpan.FromHours(6)),
        new LogTimeWindowOption("Last 24h",   TimeSpan.FromHours(24)),
    };

    [ObservableProperty] private LogLevel? _levelFilter;
    [ObservableProperty] private string _sourceFilter = "";
    [ObservableProperty] private string _profileFilter = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private TimeSpan? _timeRange;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _isPaused;

    // ─── Run-context banner ──────────────────────────────────────
    // When the user clicks "View logs" on a Runs row we navigate
    // here with a pinned context so the page reads as "Logs for
    // run #5 — site_x" with a clear button. Mirrors the legacy
    // web's window.LOGS_MODE = { type:"history", runId } shape,
    // adapted for the desktop's file-tail model (no separate DB
    // history endpoint — we just narrow the live filter).

    /// <summary>The run we're scoped to, or null when the page
    /// is in its default live-tail mode.</summary>
    [ObservableProperty] private long? _runContextId;
    [ObservableProperty] private string? _runContextProfile;
    [ObservableProperty] private DateTime? _runContextStartedAt;
    [ObservableProperty] private DateTime? _runContextFinishedAt;

    public bool HasRunContext => RunContextId is not null;
    public string PageTitle => HasRunContext
        ? $"Logs for run #{RunContextId}"
        : "Logs";
    public string PageSubtitle => HasRunContext
        ? $"Filtered to '{RunContextProfile}'. Clear the context to return to the full live tail."
        : "Live tail from the Ghost Shell log file. Filter, pause, and search across the last 2000 entries.";

    partial void OnRunContextIdChanged(long? value)
    {
        OnPropertyChanged(nameof(HasRunContext));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
    }
    partial void OnRunContextProfileChanged(string? value)
        => OnPropertyChanged(nameof(PageSubtitle));

    // ─── Visible result ──────────────────────────────────────────

    public ObservableCollection<LogEntry> Items { get; } = new();
    [ObservableProperty] private int _bufferCount;
    [ObservableProperty] private int _shown;
    /// <summary>How many entries were evicted from the head of the
    /// buffer because we hit MaxBufferSize. Visible in the footer
    /// so the user knows they're seeing a window, not history.</summary>
    [ObservableProperty] private int _dropped;
    [ObservableProperty] private bool _isFiltered;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _hasInvalidRegex;
    [ObservableProperty] private string? _regexError;

    /// <summary>True while there are buffered entries that don't
    /// satisfy the current filter — the empty-state shows different
    /// copy depending on whether the buffer is genuinely empty
    /// or just filtered to zero rows.</summary>
    public bool BufferHasHidden => BufferCount > 0 && Shown == 0;

    public override async Task OnNavigatedToAsync()
    {
        if (!_tail.IsRunning) await _tail.StartAsync();
    }

    // Filter changes → re-project the buffer into Items.
    partial void OnLevelFilterChanged(LogLevel? value)  => Reproject();
    partial void OnSourceFilterChanged(string value)    => Reproject();
    partial void OnProfileFilterChanged(string value)   => Reproject();
    partial void OnSearchTextChanged(string value)      { CompileRegex(); Reproject(); }
    partial void OnUseRegexChanged(bool value)          { CompileRegex(); Reproject(); }
    partial void OnTimeRangeChanged(TimeSpan? value)    => Reproject();
    partial void OnIsPausedChanged(bool value)
    {
        // On unpause, the buffer may have grown. Reproject so the
        // user sees what arrived during the pause.
        if (!value) Reproject();
    }

    // ─── LogTail integration ─────────────────────────────────────

    private void OnEntryAppended(LogEntry entry)
    {
        // The tail fires from a worker thread. Marshal everything
        // (buffer mutation + ObservableCollection update) to the
        // dispatcher in one hop so we don't cross-thread the UI.
        if (Application.Current?.Dispatcher is not { } d) return;
        d.BeginInvoke(() =>
        {
            AppendToBuffer(entry);

            // Snapshot IsPaused + filter criteria once at the top.
            // Reading them mid-lambda is technically safe on the
            // dispatcher, but at high entry rates the user can
            // toggle filters mid-evaluation — better to fix the
            // criteria at the moment we decided to handle this
            // entry. Pure consistency, no extra cost.
            var paused = IsPaused;
            var criteria = new LogFilterCriteria(
                MinLevel:        LevelFilter,
                SourceContains:  SourceFilter,
                ProfileContains: ProfileFilter,
                SearchText:      SearchText,
                UseRegex:        UseRegex,
                TimeRange:       TimeRange,
                Now:             DateTime.Now);
            var rx = _compiledRegex;

            // RAW continuation folding — collapses Serilog's
            // exception stack frames into the head log row's
            // Message. Only fold when:
            //   • we're not paused (otherwise the head row is the
            //     LAST row that satisfied the filter at pause time,
            //     and folding would mutate a row that may not
            //     reflect a current entry)
            //   • the RAW line starts with whitespace (the Serilog
            //     marker for "this belongs to the previous entry")
            //   • Items has at least one row to fold INTO
            //   • that row would still pass the current filter.
            //     Without this guard, a RAW line attached to a
            //     filtered-out head would create a stale row
            //     count when filters change next.
            if (!paused
                && entry.Level == "RAW" && entry.Message.Length > 0
                && char.IsWhiteSpace(entry.Message[0])
                && Items.Count > 0
                && LogFilter.Passes(Items[^1], criteria, rx))
            {
                var last = Items[^1];
                Items[^1] = last with { Message = last.Message + "\n" + entry.Message };
                // Shown count unchanged: RAW fold mutates an
                // existing row, doesn't add one.
                BufferCount = _buffer.Count;
                return;
            }

            if (!paused && LogFilter.Passes(entry, criteria, rx))
            {
                Items.Add(entry);
                Shown = Items.Count;
                IsEmpty = false;
                if (Items.Count > MaxBufferSize)
                    Items.RemoveAt(0);
            }
            BufferCount = _buffer.Count;
            OnPropertyChanged(nameof(BufferHasHidden));
        });
    }

    private void AppendToBuffer(LogEntry entry)
    {
        lock (_bufferGate)
        {
            _buffer.AddLast(entry);
            while (_buffer.Count > MaxBufferSize)
            {
                _buffer.RemoveFirst();
                Dropped++;
            }
        }
    }

    private void Reproject()
    {
        IsFiltered = LevelFilter is not null
                  || !string.IsNullOrWhiteSpace(SourceFilter)
                  || !string.IsNullOrWhiteSpace(ProfileFilter)
                  || !string.IsNullOrWhiteSpace(SearchText)
                  || TimeRange is not null;

        Items.Clear();
        LogEntry[] snapshot;
        lock (_bufferGate) snapshot = _buffer.ToArray();

        foreach (var e in snapshot)
            if (PassesFilter(e)) Items.Add(e);

        Shown = Items.Count;
        IsEmpty = Items.Count == 0;
        OnPropertyChanged(nameof(BufferHasHidden));
    }

    private void CompileRegex()
    {
        var src = SearchText ?? "";
        if (!UseRegex || string.IsNullOrWhiteSpace(src))
        {
            _compiledRegex = null;
            _compiledRegexSource = "";
            HasInvalidRegex = false;
            RegexError = null;
            return;
        }
        if (src == _compiledRegexSource && _compiledRegex is not null) return;

        try
        {
            // Cap the regex execution to a tight budget so a
            // catastrophic-backtracking pattern on a deep log line
            // can't pin the dispatcher.
            _compiledRegex = new Regex(src,
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(50));
            _compiledRegexSource = src;
            HasInvalidRegex = false;
            RegexError = null;
        }
        catch (Exception ex)
        {
            _compiledRegex = null;
            _compiledRegexSource = "";
            HasInvalidRegex = true;
            RegexError = ex.Message;
        }
    }

    private bool PassesFilter(LogEntry e)
    {
        // Build a frozen snapshot once per call and delegate to the
        // pure helper. The helper is unit-tested directly, so any
        // filter-logic bug found in tests automatically applies
        // here without code drift.
        var criteria = new LogFilterCriteria(
            MinLevel:        LevelFilter,
            SourceContains:  SourceFilter,
            ProfileContains: ProfileFilter,
            SearchText:      SearchText,
            UseRegex:        UseRegex,
            TimeRange:       TimeRange,
            Now:             DateTime.Now);
        return LogFilter.Passes(e, criteria, _compiledRegex);
    }

    // ─── Page-level actions ──────────────────────────────────────

    [RelayCommand]
    private void Clear()
    {
        // Hold the bufferGate for the WHOLE clear, including the
        // counter resets. Without it, an OnEntryAppended-driven
        // AppendToBuffer call running on a different dispatcher
        // tick can run concurrently with our reset and produce a
        // negative-looking Dropped (we set it to 0, then they
        // increment it before our buffer is empty).
        lock (_bufferGate)
        {
            _buffer.Clear();
            Dropped = 0;
        }
        Items.Clear();
        Shown       = 0;
        BufferCount = 0;
        IsEmpty     = true;
        OnPropertyChanged(nameof(BufferHasHidden));
        _log.LogDebug("Logs view cleared by user");
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void ToggleAutoScroll() => AutoScroll = !AutoScroll;

    [RelayCommand]
    private void Copy()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var e in Items)
                sb.AppendLine(Format(e));
            Clipboard.SetText(sb.ToString());
            _log.LogInformation("Copied {Count} log entries to clipboard", Items.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not copy logs to clipboard");
        }
    }

    /// <summary>
    /// Download the currently visible log slice to a user-picked
    /// location. Mirrors the legacy web's "Download" button — the
    /// raw daily log file is already written by Serilog (rotating
    /// in <c>%LOCALAPPDATA%\GhostShell\logs</c>); this command
    /// captures the FILTERED VIEW the user is actually looking at,
    /// which is rarely what's in the raw file (filter chips, search
    /// term, regex, time range, run-context narrowing).
    ///
    /// We invoke the standard SaveFileDialog so the user picks the
    /// destination — the previous "silent save to Documents"
    /// behaviour was confusing because the resulting file landed
    /// somewhere the user didn't ask for.
    /// </summary>
    [RelayCommand]
    private void Download()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title         = "Download log view",
                Filter        = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt    = "txt",
                AddExtension  = true,
                FileName      = $"ghost-shell-logs_{stamp}.txt",
                InitialDirectory = Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog() != true) return;

            using var w = new StreamWriter(dlg.FileName, append: false, Encoding.UTF8);
            foreach (var e in Items) w.WriteLine(Format(e));
            _log.LogInformation(
                "Downloaded {Count} log entries → {Path}",
                Items.Count, dlg.FileName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not write log download file");
        }
    }

    [RelayCommand]
    private void ResetFilters()
    {
        LevelFilter   = null;
        SourceFilter  = "";
        ProfileFilter = "";
        SearchText    = "";
        UseRegex      = false;
        TimeRange     = null;
        ClearRunContext();
    }

    /// <summary>
    /// Pin the page to the given run — sets the profile filter to
    /// the run's profile, widens the time range to cover the run's
    /// window, and pins the title banner. Called from
    /// <c>RunsViewModel.ViewLogs</c>.
    ///
    /// We don't load historical lines from the DB the way the
    /// legacy web did (logs aren't persisted per-run in the
    /// desktop port — they live in the file tail). Instead the
    /// banner makes the scope explicit and the profile filter
    /// narrows the live buffer to the relevant entries.
    /// </summary>
    public void FilterForRun(Run run)
    {
        // Snapshot fields first so the chained property changes
        // (each of which fires Reproject) end up with a coherent
        // state by the last setter.
        RunContextId          = run.Id;
        RunContextProfile     = run.ProfileName;
        RunContextStartedAt   = run.StartedAt;
        RunContextFinishedAt  = run.FinishedAt;

        LevelFilter   = null;
        SourceFilter  = "";
        SearchText    = "";
        UseRegex      = false;

        // Profile filter does the actual narrowing. We match the
        // run's profile name verbatim — log lines for that profile
        // contain the name in the message body, which the existing
        // LogFilter logic handles.
        ProfileFilter = run.ProfileName;

        // For finished runs, narrow to a window that covers the
        // run plus a small lead/lag so launch/teardown lines are
        // included. For still-running rows, leave the window open
        // so new entries continue to appear.
        if (run.FinishedAt is { } finishedAt)
        {
            var span = (finishedAt - run.StartedAt).Add(TimeSpan.FromMinutes(2));
            // TimeRange is "from now backwards", so widen it to at
            // least cover the run's age. The buffer cap (2000) will
            // naturally limit how much actually shows.
            var ageNow = DateTime.Now - run.StartedAt + TimeSpan.FromMinutes(2);
            TimeRange = ageNow > span ? ageNow : span;
        }
        else
        {
            TimeRange = null;
        }

        Reproject();
    }

    [RelayCommand]
    private void ClearRunContext()
    {
        if (!HasRunContext) return;
        RunContextId         = null;
        RunContextProfile    = null;
        RunContextStartedAt  = null;
        RunContextFinishedAt = null;
        ProfileFilter        = "";
        TimeRange            = null;
        Reproject();
    }

    /// <summary>Used by the filter-chip strip in the view to clear
    /// one specific filter without resetting the others.</summary>
    [RelayCommand]
    private void ClearFilter(string? which)
    {
        switch (which?.ToLowerInvariant())
        {
            case "level":   LevelFilter   = null; break;
            case "source":  SourceFilter  = "";   break;
            case "profile": ProfileFilter = "";   break;
            case "search":  SearchText    = "";   break;
            case "time":    TimeRange     = null; break;
        }
    }

    private static string Format(LogEntry e) =>
        $"[{e.Timestamp:HH:mm:ss}] [{e.Level}] {e.ShortSource}: {e.Message}";

    public void Dispose()
    {
        _tail.EntryAppended -= OnEntryAppended;
    }
}

public sealed record LevelFilterOption(string Label, LogLevel? Value);
/// <summary>
/// Logs-page-specific time window option. Distinct from
/// <see cref="TimeRangeOption"/> in RunsViewModel (which uses int
/// hours and lives in the same namespace) — the two records differ
/// in shape, so they need different names.
/// </summary>
public sealed record LogTimeWindowOption(string Label, TimeSpan? Range);
