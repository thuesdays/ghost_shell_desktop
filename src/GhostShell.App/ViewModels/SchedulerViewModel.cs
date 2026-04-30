// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Scheduler page VM. Lists every schedule with a live "next fire"
/// countdown, exposes commands to enable/disable, run-now, edit, and
/// delete.
///
/// The countdown ticks once per second on the dispatcher; the
/// underlying schedule rows are reloaded only on user action (or
/// after edits) — RunnerHost is what changes <c>next_fire_at</c>
/// in the DB, so the UI snapshots are read-only views.
/// </summary>
public sealed partial class SchedulerViewModel : BaseViewModel, IDisposable
{
    private readonly IScheduleService _schedules;
    private readonly IProfileService _profiles;
    private readonly IProfileGroupService _groups;
    private readonly IProfileRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly ILogger<SchedulerViewModel> _log;

    private readonly DispatcherTimer _countdownTimer;

    public SchedulerViewModel(
        IScheduleService schedules,
        IProfileService profiles,
        IProfileGroupService groups,
        IProfileRunner runner,
        IDialogService dialogs,
        ILogger<SchedulerViewModel> log)
    {
        _schedules = schedules;
        _profiles  = profiles;
        _groups    = groups;
        _runner    = runner;
        _dialogs   = dialogs;
        _log       = log;

        _countdownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _countdownTimer.Tick += (_, _) =>
        {
            foreach (var row in Items) row.RefreshCountdown();
        };
        // Timer starts in OnNavigatedToAsync — keeping it stopped
        // while the page is off-screen avoids 1Hz dispatcher work
        // against an invisible VM and prevents double-firing if the
        // user navigates Scheduler → away → Scheduler.
    }

    public ObservableCollection<ScheduleRowVm> Items { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private int  _enabledCount;
    [ObservableProperty] private int  _disabledCount;

    public override async Task OnNavigatedToAsync()
    {
        // Resume the countdown ticker every time the page comes
        // back into view. NavigationService calls OnNavigatedFrom
        // on the outgoing VM (which stops it) — this pair keeps
        // the timer running ONLY while the user can see the page.
        if (!_countdownTimer.IsEnabled) _countdownTimer.Start();
        await ReloadAsync();
    }

    public override Task OnNavigatedFromAsync()
    {
        if (_countdownTimer.IsEnabled) _countdownTimer.Stop();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            Items.Clear();
            var rows = await _schedules.ListAsync();
            foreach (var s in rows) Items.Add(new ScheduleRowVm(s));
            IsEmpty = Items.Count == 0;
            EnabledCount  = Items.Count(r => r.Schedule.Enabled);
            DisabledCount = Items.Count - EnabledCount;
            _log.LogInformation(
                "Scheduler list loaded: {Total} schedule(s) ({On} enabled / {Off} paused)",
                Items.Count, EnabledCount, DisabledCount);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Schedule list load failed");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var profiles = (await _profiles.ListAsync()).Select(p => p.Name).ToList();
        var groups   = (await _groups.ListAsync()).Select(g => g.Name).ToList();
        var saved = await _dialogs.ShowScheduleEditorAsync(null, profiles, groups);
        if (!saved) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task EditAsync(ScheduleRowVm? selected)
    {
        if (selected is null) return;
        var profiles = (await _profiles.ListAsync()).Select(p => p.Name).ToList();
        var groups   = (await _groups.ListAsync()).Select(g => g.Name).ToList();
        var saved = await _dialogs.ShowScheduleEditorAsync(selected.Schedule, profiles, groups);
        if (!saved) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(ScheduleRowVm? selected)
    {
        if (selected is null) return;
        var ok = await _dialogs.ConfirmAsync(
            $"Delete schedule '{selected.Schedule.Name}'?",
            "The schedule row is removed; the target profile / group is " +
            "left alone. Run history is unaffected.",
            "Delete",
            ConfirmSeverity.Danger);
        if (!ok) return;

        try
        {
            await _schedules.DeleteAsync(selected.Schedule.Id);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Delete schedule #{Id} failed", selected.Schedule.Id);
        }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(ScheduleRowVm? selected)
    {
        if (selected is null) return;
        var newState = !selected.Schedule.Enabled;
        try
        {
            await _schedules.SetEnabledAsync(selected.Schedule.Id, newState);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Toggle enabled #{Id} failed", selected.Schedule.Id);
        }
    }

    /// <summary>
    /// Force-fire a schedule right now. Bypasses the active-window
    /// guard but still respects the runner concurrency cap (a manual
    /// run-now while at the cap fails fast with a dialog).
    /// </summary>
    [RelayCommand]
    private async Task RunNowAsync(ScheduleRowVm? selected)
    {
        if (selected is null) return;
        var s = selected.Schedule;

        try
        {
            if (s.TargetKind == ScheduleTargetKind.Profile)
            {
                if (_runner.ActiveProfileNames.Contains(s.TargetName))
                {
                    await _dialogs.ConfirmAsync(
                        "Already running",
                        $"Profile '{s.TargetName}' is already live. Stop it first " +
                        "from the Profiles page if you want a fresh launch.",
                        "OK");
                    return;
                }
                var profile = await _profiles.GetAsync(s.TargetName);
                if (profile is null)
                {
                    await _dialogs.ConfirmAsync(
                        "Target missing",
                        $"Profile '{s.TargetName}' was deleted. Edit the schedule " +
                        "to point at a real profile or remove the schedule.",
                        "OK", ConfirmSeverity.Error);
                    return;
                }
                await Task.Run(() => _runner.StartAsync(profile));
            }
            else
            {
                var group = (await _groups.ListAsync())
                    .FirstOrDefault(g => string.Equals(g.Name, s.TargetName,
                                                        StringComparison.OrdinalIgnoreCase));
                if (group is null)
                {
                    await _dialogs.ConfirmAsync(
                        "Target missing",
                        $"Group '{s.TargetName}' was deleted. Edit the schedule " +
                        "to point at a real group.",
                        "OK", ConfirmSeverity.Error);
                    return;
                }
                var detailed = await _groups.GetAsync(group.Id);
                if (detailed is null) return;
                foreach (var name in detailed.Members)
                {
                    if (_runner.ActiveProfileNames.Contains(name)) continue;
                    var profile = await _profiles.GetAsync(name);
                    if (profile is null) continue;
                    try { await _runner.StartAsync(profile); }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Group run-now: '{Name}' failed", name);
                    }
                    await Task.Delay(150);
                }
            }
            // Bump fire_count + last_fired_at; let the loop recompute next_fire_at.
            var next = RunnerHostNextFireGuess(s);
            await _schedules.RecordFiredAsync(s.Id, DateTime.UtcNow, next);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Run-now failed for #{Id}", s.Id);
            await _dialogs.ConfirmAsync(
                "Run-now failed",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    /// <summary>Guess at the next fire time for run-now bookkeeping.
    /// Mirrors <c>RunnerHost.ComputeNextFire</c> without referencing
    /// the Runtime project (the App project doesn't depend on Runtime
    /// directly — that's a Phase-3 layering rule).</summary>
    private static DateTime RunnerHostNextFireGuess(Schedule s)
    {
        var nowUtc = DateTime.UtcNow;
        if (s.TriggerKind == ScheduleTriggerKind.Cron)
        {
            var cron = CronExpression.TryParse(s.CronExpr, out _);
            if (cron is null) return nowUtc.AddMinutes(15);
            var nextLocal = cron.NextAfter(nowUtc.ToLocalTime());
            return nextLocal?.ToUniversalTime() ?? nowUtc.AddDays(1);
        }
        var seconds = s.IntervalSec is > 0 ? s.IntervalSec.Value : 60;
        return nowUtc.AddSeconds(seconds);
    }

    public void Dispose()
    {
        _countdownTimer.Stop();
    }
}

/// <summary>
/// Row VM that wraps a <see cref="Schedule"/> with a derived
/// "fires in X" countdown that ticks once per second.
/// </summary>
public sealed partial class ScheduleRowVm : ObservableObject
{
    public Schedule Schedule { get; }

    public ScheduleRowVm(Schedule s)
    {
        Schedule = s;
        RefreshCountdown();
    }

    [ObservableProperty] private string _countdown = "—";

    public string Name           => Schedule.Name;
    public string TargetLabel    => $"{Schedule.TargetKind.ToString().ToLowerInvariant()}: {Schedule.TargetName}";
    public string TriggerSummary => Schedule.TriggerSummary;
    public string ActiveSummary  => $"{Schedule.ActiveDaysSummary} · {Schedule.ActiveHoursSummary}";
    public string LastFiredHuman => Schedule.LastFiredAt is null
        ? "never"
        : Humanize.Age(Schedule.LastFiredAt.Value);
    public int FireCount => Schedule.FireCount;
    public int FailCount => Schedule.FailCount;
    public bool Enabled  => Schedule.Enabled;

    public string EnabledLabel => Enabled ? "ON" : "PAUSED";

    public void RefreshCountdown()
    {
        if (!Schedule.Enabled)
        {
            Countdown = "paused";
            return;
        }
        if (Schedule.NextFireAt is null)
        {
            Countdown = "—";
            return;
        }

        var delta = Schedule.NextFireAt.Value - DateTime.UtcNow;
        if (delta.TotalSeconds <= 0)
        {
            Countdown = "due";
            return;
        }

        if (delta.TotalSeconds < 60)
            Countdown = $"in {(int)delta.TotalSeconds}s";
        else if (delta.TotalMinutes < 60)
            Countdown = $"in {(int)delta.TotalMinutes}m";
        else if (delta.TotalHours < 48)
            Countdown = $"in {(int)delta.TotalHours}h {((int)delta.TotalMinutes) % 60}m";
        else
            Countdown = $"in {(int)delta.TotalDays}d";
    }
}

/// <summary>Tiny helpers we'd otherwise pull in a NuGet package for.</summary>
internal static class Humanize
{
    public static string Age(DateTime utcWhen)
    {
        var span = DateTime.UtcNow - utcWhen;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours   < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays    < 7)  return $"{(int)span.TotalDays}d ago";
        return utcWhen.ToLocalTime().ToString("yyyy-MM-dd");
    }
}
