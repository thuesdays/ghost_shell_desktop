// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 64 — Bulk Start parameters dialog. Two sliders — stagger
/// seconds + concurrency cap — plus a live warning panel that
/// estimates total launch time + memory pressure based on the
/// chosen values × the number of selected profiles.
/// </summary>
public partial class BulkStartDialog : Window
{
    private readonly int _profileCount;

    public BulkStartOptions? Result { get; private set; }

    public BulkStartDialog(int profileCount)
    {
        InitializeComponent();
        _profileCount = profileCount;
        HeaderText.Text = $"Start {profileCount} profile(s)";
        UpdateLabels();
    }

    private void StaggerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateLabels();

    private void ConcurrentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateLabels();

    private void UpdateLabels()
    {
        // Sliders may not be initialised yet during XAML load.
        if (StaggerSlider is null || ConcurrentSlider is null) return;

        var stagger = (int)StaggerSlider.Value;
        var conc = (int)ConcurrentSlider.Value;

        StaggerLabel.Text = stagger == 0 ? "no delay" : $"{stagger} s";
        ConcurrentLabel.Text = $"{conc} parallel";

        // Estimate total launch window — last profile starts at
        // (count-1)*stagger seconds. Each profile uses ~300MB of RAM
        // for chromedriver + chrome + extension.
        if (WarningText is null) return;
        var totalMinutes = (_profileCount - 1) * stagger / 60.0;
        var peakRamMb = conc * 350;
        var totalSecs = (_profileCount - 1) * stagger;
        var lastStart = totalSecs >= 60
            ? $"{totalMinutes:0.#} min"
            : $"{totalSecs} s";
        WarningText.Text =
            $"Last profile starts in ~{lastStart}.  Peak RAM ≈ {peakRamMb} MB " +
            $"({conc} × ~350 MB).  " +
            (peakRamMb > 4000
                ? "⚠ This may saturate a 8 GB system — lower the concurrency cap."
                : "Should fit on a typical workstation.");
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Result = new BulkStartOptions
        {
            StaggerSeconds = (int)StaggerSlider.Value,
            MaxConcurrent  = (int)ConcurrentSlider.Value,
        };
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
