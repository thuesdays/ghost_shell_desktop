// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Code-behind for the Create / Edit Schedule modal.
///
/// Two trigger modes:
///   • Interval — a numeric value × unit (seconds / minutes / hours
///     / days). The body shows a live "every X" human summary.
///   • Cron — 5-field expression. Live-validates on every keystroke
///     and renders the next 5 fire times so the user can sanity-
///     check what they typed before saving.
///
/// Active days (Mon..Sun toggle chips) and active hours (from / to
/// 0-23) apply to both modes. Empty selection = unrestricted.
///
/// Save validates everything and only calls IScheduleService once.
/// next_fire_at is computed up-front so the runner picks the row up
/// at the right moment without needing to recompute.
/// </summary>
public partial class ScheduleEditorDialog : Window
{
    private readonly IScheduleService _service;
    private readonly Schedule? _existing;
    private readonly IReadOnlyList<string> _profileNames;
    private readonly IReadOnlyList<string> _groupNames;

    public bool DidSave { get; private set; }

    public ScheduleEditorDialog(
        IScheduleService service,
        Schedule? existing,
        IReadOnlyList<string> profileNames,
        IReadOnlyList<string> groupNames)
    {
        InitializeComponent();
        _service       = service;
        _existing      = existing;
        _profileNames  = profileNames;
        _groupNames    = groupNames;

        TargetNameField.ItemsSource = profileNames;

        if (existing is not null)
        {
            TitleText.Text  = $"✏ Edit schedule: {existing.Name}";
            NameField.Text  = existing.Name;
            EnabledCheckbox.IsChecked = existing.Enabled;

            // Target
            if (existing.TargetKind == ScheduleTargetKind.Group)
            {
                TargetKindField.SelectedIndex = 1;
                TargetNameField.ItemsSource   = groupNames;
            }
            TargetNameField.SelectedItem = existing.TargetName;

            // Trigger mode + value — three-way: Simple / Interval / Cron.
            switch (existing.TriggerKind)
            {
                case ScheduleTriggerKind.Cron:
                    CronTab.IsChecked = true;
                    CronField.Text    = existing.CronExpr ?? "";
                    break;
                case ScheduleTriggerKind.Interval:
                    IntervalTab.IsChecked = true;
                    FillIntervalFields(existing.IntervalSec ?? 60);
                    break;
                case ScheduleTriggerKind.Simple:
                default:
                    SimpleTab.IsChecked       = true;
                    RunsPerDayField.Text      = existing.RunsPerDay?.ToString(CultureInfo.InvariantCulture)
                                                  ?? "150";
                    MinJitterField.Text       = (existing.MinJitterSec ?? 20).ToString(CultureInfo.InvariantCulture);
                    MaxJitterField.Text       = (existing.MaxJitterSec ?? 180).ToString(CultureInfo.InvariantCulture);
                    break;
            }

            // Active days
            foreach (var d in existing.ActiveDays) ToggleDay(d, true);

            // Active hours
            FromHourField.Text = existing.ActiveFromHour?.ToString(CultureInfo.InvariantCulture) ?? "";
            ToHourField.Text   = existing.ActiveToHour?.ToString(CultureInfo.InvariantCulture)   ?? "";
        }
        else
        {
            // Sane new-schedule defaults: Simple trigger, 150 runs/day,
            // 20-180s jitter — matches the typical warmup workload the
            // user described when adding this mode. Active hours stay
            // empty (= any time) so the user explicitly opts into a
            // bounded window.
            FillIntervalFields(3600);
            TargetNameField.ItemsSource = profileNames;
        }

        Loaded += (_, _) =>
        {
            UpdateIntervalSummary();
            UpdateSimpleSummary();
            ValidateCron();
            UpdatePaneVisibility();
            NameField.Focus();
            NameField.SelectAll();
        };
    }

    // ─── Target ──────────────────────────────────────────────────

    private void OnTargetKindChanged(object sender, SelectionChangedEventArgs e)
    {
        // Swap the Target dropdown source between profiles and groups
        // depending on which kind the user selected. We snapshot both
        // lists in the ctor so this swap is one rebind, no service call.
        if (!IsLoaded) return;
        var isGroup = TargetKindField.SelectedIndex == 1;
        TargetNameField.ItemsSource = isGroup ? _groupNames : _profileNames;
        TargetNameField.SelectedItem = null;
    }

    // ─── Trigger pane swap ───────────────────────────────────────

    private void OnTriggerKindChanged(object sender, RoutedEventArgs e)
        => UpdatePaneVisibility();

    private void UpdatePaneVisibility()
    {
        if (!IsInitialized) return;
        SimplePane.Visibility   = SimpleTab.IsChecked   == true ? Visibility.Visible : Visibility.Collapsed;
        IntervalPane.Visibility = IntervalTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CronPane.Visibility     = CronTab.IsChecked     == true ? Visibility.Visible : Visibility.Collapsed;

        // Cron's invalid-state gate only applies while Cron is the
        // active mode. When the user switches away, restore Save.
        if (CronTab.IsChecked != true) SaveBtn.IsEnabled = true;
        else                           ValidateCron();
    }

    // ─── Simple ─────────────────────────────────────────────────

    private void OnSimpleChanged(object sender, RoutedEventArgs e) => UpdateSimpleSummary();

    private void UpdateSimpleSummary()
    {
        if (!IsInitialized) return;

        var min = ParseInt(MinJitterField.Text);
        var max = ParseInt(MaxJitterField.Text);
        var runs = ParseInt(RunsPerDayField.Text);

        if (min is null || max is null || min < 1 || max < min)
        {
            SimpleSummaryText.Text = "Set both min and max gap to positive seconds (max ≥ min).";
            return;
        }

        var avg = (min.Value + max.Value) / 2.0;

        // Active-window estimate: prefer the dialog's hour fields if
        // the user has filled them in. Otherwise assume 24h.
        var fromH = ParseHour(FromHourField?.Text ?? "");
        var toH   = ParseHour(ToHourField?.Text ?? "");
        double windowHours;
        if (fromH is { } a && toH is { } b)
            windowHours = b > a ? b - a + 1 : (24 - a) + b + 1; // inclusive
        else
            windowHours = 24;

        var expected = (windowHours * 3600.0) / avg;
        var expectedRounded = (int)Math.Round(expected);

        var note = $"Average gap {avg:F0}s × {windowHours:F0}h window ≈ {expectedRounded} fires/day";
        if (runs is { } target && target > 0)
        {
            if (expectedRounded > target)
                note += $". Capped at {target}/day (rest of window stays idle).";
            else if (expectedRounded < target * 0.7)
                note += $". You asked for {target}/day — narrow the gap range to hit that target.";
            else
                note += $". Targeting ~{target}/day.";
        }
        SimpleSummaryText.Text = note;
    }

    private static int? ParseInt(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw.Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    // ─── Interval ────────────────────────────────────────────────

    private void OnIntervalChanged(object sender, RoutedEventArgs e)
        => UpdateIntervalSummary();

    private void UpdateIntervalSummary()
    {
        if (!IsInitialized) return;
        var seconds = ReadIntervalSeconds();
        if (seconds is null)
        {
            IntervalHumanText.Text = "(invalid)";
            return;
        }
        IntervalHumanText.Text = SummarizeInterval(seconds.Value);
    }

    private int? ReadIntervalSeconds()
    {
        if (!int.TryParse(IntervalField.Text, NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out var v) || v <= 0)
            return null;
        if (IntervalUnitField.SelectedItem is not ComboBoxItem ui
            || ui.Tag is not string mulRaw
            || !int.TryParse(mulRaw, NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out var mul))
            mul = 60;
        return v * mul;
    }

    private void FillIntervalFields(int seconds)
    {
        // Pick the highest unit that divides cleanly.
        if (seconds % 86400 == 0) { IntervalField.Text = (seconds / 86400).ToString(); IntervalUnitField.SelectedIndex = 3; return; }
        if (seconds % 3600  == 0) { IntervalField.Text = (seconds / 3600).ToString();  IntervalUnitField.SelectedIndex = 2; return; }
        if (seconds % 60    == 0) { IntervalField.Text = (seconds / 60).ToString();    IntervalUnitField.SelectedIndex = 1; return; }
        IntervalField.Text = seconds.ToString();
        IntervalUnitField.SelectedIndex = 0;
    }

    private static string SummarizeInterval(int seconds)
    {
        if (seconds < 60)          return $"every {seconds}s";
        if (seconds < 3600)        return $"every {seconds / 60}m";
        if (seconds < 86400)       return seconds % 3600 == 0
            ? $"every {seconds / 3600}h"
            : $"every {seconds / 3600}h {(seconds % 3600) / 60}m";
        return $"every {seconds / 86400}d";
    }

    // ─── Cron ────────────────────────────────────────────────────

    private void OnCronChanged(object sender, TextChangedEventArgs e) => ValidateCron();

    private void OnCronPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string preset)
        {
            CronField.Text = preset;
        }
    }

    private void ValidateCron()
    {
        if (!IsInitialized) return;
        var cron = CronExpression.TryParse(CronField.Text, out var err);
        if (cron is null)
        {
            CronErrorText.Text       = err ?? "Invalid cron expression.";
            CronErrorText.Visibility = Visibility.Visible;
            CronPreviewList.ItemsSource = Array.Empty<string>();
            // When cron is the active mode and it's invalid, gate
            // Save so the user can't ship a broken row. We DON'T do
            // this in non-cron modes — Interval / Simple are easier
            // to bounds-check at OnSave instead.
            if (CronTab.IsChecked == true) SaveBtn.IsEnabled = false;
            return;
        }
        CronErrorText.Visibility = Visibility.Collapsed;
        var fires = cron.NextFires(DateTime.Now, 5)
            .Select(t => t.ToString("yyyy-MM-dd HH:mm"))
            .ToList();
        CronPreviewList.ItemsSource = fires;
        SaveBtn.IsEnabled = true;
    }

    // ─── Day toggles ─────────────────────────────────────────────

    private void ToggleDay(int isoWeekday, bool on)
    {
        var btn = isoWeekday switch
        {
            1 => DayMon, 2 => DayTue, 3 => DayWed, 4 => DayThu,
            5 => DayFri, 6 => DaySat, 7 => DaySun, _ => null,
        };
        if (btn is not null) btn.IsChecked = on;
    }

    private List<int> ReadActiveDays()
    {
        var result = new List<int>();
        foreach (var (btn, day) in new (ToggleButton, int)[]
        {
            (DayMon,1),(DayTue,2),(DayWed,3),(DayThu,4),
            (DayFri,5),(DaySat,6),(DaySun,7),
        })
        {
            if (btn.IsChecked == true) result.Add(day);
        }
        return result;
    }

    // ─── Save / cancel ───────────────────────────────────────────

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var name = (NameField.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowStatus("Name is required.", isError: true);
            return;
        }

        var targetKind = TargetKindField.SelectedIndex == 1
            ? ScheduleTargetKind.Group
            : ScheduleTargetKind.Profile;
        var targetName = TargetNameField.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            ShowStatus($"Pick a target {targetKind.ToString().ToLowerInvariant()}.", isError: true);
            return;
        }

        ScheduleTriggerKind triggerKind;
        string? cronExpr     = null;
        int?    intervalSec  = null;
        int?    runsPerDay   = null;
        int?    minJitterSec = null;
        int?    maxJitterSec = null;

        if (CronTab.IsChecked == true)
        {
            triggerKind = ScheduleTriggerKind.Cron;
            var cron = CronExpression.TryParse(CronField.Text, out var err);
            if (cron is null)
            {
                ShowStatus($"Cron invalid: {err}", isError: true);
                return;
            }
            cronExpr = CronField.Text.Trim();
        }
        else if (IntervalTab.IsChecked == true)
        {
            triggerKind = ScheduleTriggerKind.Interval;
            var sec = ReadIntervalSeconds();
            if (sec is null)
            {
                ShowStatus("Interval must be a positive integer.", isError: true);
                return;
            }
            intervalSec = sec;
        }
        else
        {
            // Simple — runs/day + jitter range. min/max are mandatory;
            // runs/day is optional (acts as a daily cap).
            triggerKind = ScheduleTriggerKind.Simple;
            var min = ParseInt(MinJitterField.Text);
            var max = ParseInt(MaxJitterField.Text);
            if (min is null or < 1 || max is null || max < min)
            {
                ShowStatus("Min and max gap must be positive integers (max ≥ min).", isError: true);
                return;
            }
            minJitterSec = min;
            maxJitterSec = max;
            runsPerDay   = ParseInt(RunsPerDayField.Text); // null = no cap
            if (runsPerDay is < 0)
            {
                ShowStatus("Runs/day must be a positive integer or empty.", isError: true);
                return;
            }
        }

        int? fromHour = ParseHour(FromHourField.Text);
        int? toHour   = ParseHour(ToHourField.Text);
        if (fromHour is null && !string.IsNullOrWhiteSpace(FromHourField.Text)
         || toHour   is null && !string.IsNullOrWhiteSpace(ToHourField.Text))
        {
            ShowStatus("Active hours must be 0–23 or empty.", isError: true);
            return;
        }

        var s = new Schedule
        {
            Id             = _existing?.Id ?? 0,
            Name           = name,
            TargetKind     = targetKind,
            TargetName     = targetName,
            TriggerKind    = triggerKind,
            CronExpr       = cronExpr,
            IntervalSec    = intervalSec,
            RunsPerDay     = runsPerDay,
            MinJitterSec   = minJitterSec,
            MaxJitterSec   = maxJitterSec,
            ActiveDays     = ReadActiveDays(),
            ActiveFromHour = fromHour,
            ActiveToHour   = toHour,
            Enabled        = EnabledCheckbox.IsChecked == true,
            LastFiredAt    = _existing?.LastFiredAt,
            FireCount      = _existing?.FireCount ?? 0,
            FailCount      = _existing?.FailCount ?? 0,
            CreatedAt      = _existing?.CreatedAt ?? default,
            // NextFireAt computed below.
        };

        // Compute next_fire_at up-front. Mirrors RunnerHost.ComputeNextFire.
        var nextFire = ComputeNextFire(s);
        s = new Schedule
        {
            Id             = s.Id,
            Name           = s.Name,
            TargetKind     = s.TargetKind,
            TargetName     = s.TargetName,
            TriggerKind    = s.TriggerKind,
            CronExpr       = s.CronExpr,
            IntervalSec    = s.IntervalSec,
            RunsPerDay     = s.RunsPerDay,
            MinJitterSec   = s.MinJitterSec,
            MaxJitterSec   = s.MaxJitterSec,
            ActiveDays     = s.ActiveDays,
            ActiveFromHour = s.ActiveFromHour,
            ActiveToHour   = s.ActiveToHour,
            Enabled        = s.Enabled,
            LastFiredAt    = s.LastFiredAt,
            NextFireAt     = nextFire,
            FireCount      = s.FireCount,
            FailCount      = s.FailCount,
            CreatedAt      = s.CreatedAt,
            UpdatedAt      = s.UpdatedAt,
        };

        SaveBtn.IsEnabled = false;
        ShowStatus("Saving…", isError: false);

        try
        {
            if (_existing is null) await _service.CreateAsync(s);
            else                   await _service.UpdateAsync(s);
            DidSave = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowStatus($"Save failed: {ex.Message}", isError: true);
            SaveBtn.IsEnabled = true;
        }
    }

    private static int? ParseHour(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out var v)) return null;
        if (v < 0 || v > 23) return null;
        return v;
    }

    private static DateTime ComputeNextFire(Schedule s)
    {
        var nowUtc = DateTime.UtcNow;
        switch (s.TriggerKind)
        {
            case ScheduleTriggerKind.Cron:
            {
                var cron = CronExpression.TryParse(s.CronExpr, out _);
                if (cron is null) return nowUtc.AddMinutes(15);
                var local = cron.NextAfter(nowUtc.ToLocalTime());
                return local?.ToUniversalTime() ?? nowUtc.AddDays(1);
            }
            case ScheduleTriggerKind.Simple:
            {
                var min = s.MinJitterSec is > 0 ? s.MinJitterSec.Value : 60;
                var max = s.MaxJitterSec is > 0 && s.MaxJitterSec.Value >= min
                    ? s.MaxJitterSec.Value
                    : Math.Max(min, 120);
                return nowUtc.AddSeconds(Random.Shared.Next(min, max + 1));
            }
            default:
            {
                var seconds = s.IntervalSec is > 0 ? s.IntervalSec.Value : 60;
                return nowUtc.AddSeconds(seconds);
            }
        }
    }

    private void ShowStatus(string text, bool isError)
    {
        StatusBox.Visibility = Visibility.Visible;
        StatusText.Text = text;
        StatusBox.BorderBrush = isError
            ? (System.Windows.Media.Brush)Application.Current.Resources["ErrBrush"]
            : (System.Windows.Media.Brush)Application.Current.Resources["Border"];
    }
}
