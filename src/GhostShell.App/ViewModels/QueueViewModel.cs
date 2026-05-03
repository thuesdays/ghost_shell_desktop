// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 64 — Queue page. Lists pending + running + recently-finished
/// jobs in the in-memory <see cref="IRunQueueService"/>. Auto-refreshes
/// when the service raises QueueChanged.
///
/// Phase 66 polish:
///   • <see cref="_debounceTimer"/> — 200ms trailing debounce on
///     QueueChanged events. Without this, a 100-job batch firing
///     QueueChanged on each status transition would queue 100+
///     Refresh() calls on the dispatcher within seconds and freeze
///     the UI. With the debounce, multiple events inside a 200ms
///     window collapse to a single Refresh.
///   • <see cref="_tickTimer"/> — 5s ticker that re-runs Refresh so
///     the relative-time strings ("in 30s", "started 12s ago") stay
///     fresh between QueueChanged events. Without this, a Pending
///     job's "in 30s" label sits stale until the next status change.
/// </summary>
public sealed partial class QueueViewModel : BaseViewModel
{
    private readonly IRunQueueService _queue;
    private readonly ILogger<QueueViewModel> _log;
    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _tickTimer;

    public ObservableCollection<QueuedRunRowVm> Rows { get; } = new();

    [ObservableProperty] private string _summaryLine = "0 pending · 0 running · 0 done";
    [ObservableProperty] private bool _isEmpty = true;

    public QueueViewModel(IRunQueueService queue, ILogger<QueueViewModel> log)
    {
        _queue = queue;
        _log   = log;
        _queue.QueueChanged += OnQueueChanged;

        // Phase 66 — debounce timer for QueueChanged events. Background
        // priority so we don't preempt user input.
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _debounceTimer.Tick += (s, e) =>
        {
            _debounceTimer.Stop();
            Refresh();
        };

        // Phase 66 — relative-time refresh ticker. Started in
        // OnNavigatedToAsync so it only ticks while the queue page is
        // actually visible. Stopped in OnNavigatedFromAsync.
        _tickTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _tickTimer.Tick += (s, e) => Refresh();
    }

    public override Task OnNavigatedToAsync()
    {
        Refresh();
        _tickTimer.Start();
        return Task.CompletedTask;
    }

    public override Task OnNavigatedFromAsync()
    {
        // Stop the ticker — no point repainting an off-screen page.
        // The QueueChanged subscription stays alive (VM is singleton);
        // when the user returns, OnNavigatedToAsync re-starts the timer.
        _tickTimer.Stop();
        // Also flush any pending debounced refresh so we don't fire
        // it on a hidden page right after the user navigates away.
        _debounceTimer.Stop();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Cancel(QueuedRunRowVm? row)
    {
        if (row is null) return;
        if (_queue.Cancel(row.Id))
            _log.LogInformation("Queue: user cancelled '{Profile}' (id={Id})",
                row.ProfileName, row.Id);
    }

    [RelayCommand]
    private void RefreshNow() => Refresh();

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        // Marshal to UI — service fires from its dispatcher thread.
        // Phase 66 — debounce: restart the 200ms timer so a burst of
        // events collapses into a single Refresh. Tick handler runs
        // on the UI thread automatically (DispatcherTimer property).
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        });
    }

    private void Refresh()
    {
        var snap = _queue.Snapshot();
        Rows.Clear();
        foreach (var j in snap) Rows.Add(QueuedRunRowVm.From(j));

        var pending = snap.Count(j => j.Status == QueuedRunStatus.Pending);
        var running = snap.Count(j => j.Status == QueuedRunStatus.Running);
        var done = snap.Count(j => j.Status == QueuedRunStatus.Done);
        var failed = snap.Count(j => j.Status == QueuedRunStatus.Failed);
        var cancelled = snap.Count(j => j.Status == QueuedRunStatus.Cancelled);

        SummaryLine = $"{pending} pending · {running} running · {done} done"
            + (failed > 0 ? $" · {failed} failed" : "")
            + (cancelled > 0 ? $" · {cancelled} cancelled" : "");
        IsEmpty = Rows.Count == 0;
    }
}

public sealed class QueuedRunRowVm
{
    public required Guid Id { get; init; }
    public required string ProfileName { get; init; }
    public required string StatusText { get; init; }
    public required Brush StatusBrush { get; init; }
    public required string ScheduledText { get; init; }
    public required string DurationText { get; init; }
    public required string Source { get; init; }
    public required string ErrorText { get; init; }
    public required bool CanCancel { get; init; }

    public static QueuedRunRowVm From(QueuedRun j)
    {
        var statusBrush = j.Status switch
        {
            QueuedRunStatus.Pending   => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)), // slate
            QueuedRunStatus.Running   => new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF)), // teal
            QueuedRunStatus.Done      => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)), // green
            QueuedRunStatus.Failed    => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), // red
            QueuedRunStatus.Cancelled => new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7A)), // grey
            _                         => new SolidColorBrush(Color.FromRgb(0x52, 0x52, 0x5B)),
        };
        var scheduledText = j.Status == QueuedRunStatus.Pending
            ? RelativeTime(j.ScheduledAt - DateTime.UtcNow, future: true)
            : j.StartedAt.HasValue
                ? "started " + RelativeTime(DateTime.UtcNow - j.StartedAt.Value, future: false)
                : "—";
        var durationText = "";
        if (j.StartedAt.HasValue && j.FinishedAt.HasValue)
            durationText = HumaniseDuration(j.FinishedAt.Value - j.StartedAt.Value);
        else if (j.StartedAt.HasValue && j.Status == QueuedRunStatus.Running)
            durationText = HumaniseDuration(DateTime.UtcNow - j.StartedAt.Value) + " (live)";
        return new QueuedRunRowVm
        {
            Id = j.Id,
            ProfileName = j.ProfileName,
            StatusText = j.Status.ToString().ToLowerInvariant(),
            StatusBrush = statusBrush,
            ScheduledText = scheduledText,
            DurationText = durationText,
            Source = j.Source,
            ErrorText = j.ErrorMessage ?? "",
            CanCancel = j.Status == QueuedRunStatus.Pending,
        };
    }

    private static string RelativeTime(TimeSpan delta, bool future)
    {
        if (delta < TimeSpan.Zero && future) return "now";
        if (delta < TimeSpan.Zero) delta = -delta;
        if (delta.TotalSeconds < 60) return future ? $"in {(int)delta.TotalSeconds}s" : $"{(int)delta.TotalSeconds}s ago";
        if (delta.TotalMinutes < 60) return future ? $"in {(int)delta.TotalMinutes}m" : $"{(int)delta.TotalMinutes}m ago";
        return future ? $"in {delta.TotalHours:0.#}h" : $"{delta.TotalHours:0.#}h ago";
    }

    private static string HumaniseDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m {(int)d.Seconds}s";
        return $"{(int)d.TotalHours}h {(int)d.Minutes}m";
    }
}
