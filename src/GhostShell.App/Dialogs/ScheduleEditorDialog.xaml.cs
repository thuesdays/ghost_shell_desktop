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
                    SimpleTab.IsChecked      = true;
                    RunsPerDayField.Text     = existing.RunsPerDay?.ToString(CultureInfo.InvariantCulture)
                                                 ?? "150";
                    // Phase 71cc — UseJitter replaces the old min/max-jitter
                    // pair. Read the persisted bool; defaults to true for
                    // pre-V28 rows that don't have the column.
                    UseJitterCheckbox.IsChecked = existing.UseJitter;
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

        var runs = ParseInt(RunsPerDayField.Text);
        if (runs is null || runs < 1)
        {
            SimpleSummaryText.Text = "Runs / day must be a positive integer.";
            return;
        }

        // Phase 71cc — gap is now derived: meanGap = window / runs.
        // Match exactly what RunnerHost.ComputeNextFire does at fire
        // time so the user's preview is honest.
        var fromH = ParseHour(FromHourField?.Text ?? "");
        var toH   = ParseHour(ToHourField?.Text ?? "");
        double windowHours;
        if (fromH is { } a && toH is { } b)
            windowHours = b >= a ? b - a + 1 : (24 - a) + b + 1; // inclusive
        else
            windowHours = 24;

        var meanGapSec = (windowHours * 3600.0) / runs.Value;
        var useJitter  = UseJitterCheckbox?.IsChecked == true;

        string gapLabel;
        if (useJitter)
        {
            var minGap = meanGapSec * 0.5;
            var maxGap = meanGapSec * 1.5;
            gapLabel = $"random {FormatGap(minGap)}–{FormatGap(maxGap)} (mean {FormatGap(meanGapSec)})";
        }
        else
        {
            gapLabel = $"every {FormatGap(meanGapSec)}, evenly spaced";
        }

        SimpleSummaryText.Text =
            $"{runs} fires across a {windowHours:F0}h window → {gapLabel}.";
    }

    private static string FormatGap(double seconds)
    {
        if (seconds < 90) return $"{seconds:F0}s";
        if (seconds < 5400) return $"{seconds / 60:F1}m";
        return $"{seconds / 3600:F2}h";
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
        bool    useJitter    = true;

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
            // Phase 71cc — Simple = runs/day + use_jitter flag.
            // Gap is computed by the runner from
            // (active_window / runs_per_day).
            triggerKind = ScheduleTriggerKind.Simple;
            runsPerDay   = ParseInt(RunsPerDayField.Text);
            if (runsPerDay is null or < 1)
            {
                ShowStatus("Runs / day must be a positive integer.", isError: true);
                return;
            }
            useJitter = UseJitterCheckbox.IsChecked == true;
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
            UseJitter      = useJitter,
            // Phase 71cc — Min/Max jitter no longer set by the editor.
            // We clear them on save so the runner falls through to the
            // window-based computation. Pre-V28 rows with values still
            // set keep them until re-edited (legacy back-compat path
            // in RunnerHost.ComputeNextFire).
            MinJitterSec   = null,
            MaxJitterSec   = null,
            FiresToday     = _existing?.FiresToday ?? 0,
            LastFireDay    = _existing?.LastFireDay,
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
            UseJitter      = s.UseJitter,
            FiresToday     = s.FiresToday,
            LastFireDay    = s.LastFireDay,
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
        // Phase 71cc — defer to the runtime's canonical implementation
        // so the editor's "preview next fire" matches what the runner
        // actually computes at fire time. Keeps the two in lock-step
        // even if the formula evolves further.
        return GhostShell.Runtime.RunnerHost.ComputeNextFire(s, DateTime.UtcNow);
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
