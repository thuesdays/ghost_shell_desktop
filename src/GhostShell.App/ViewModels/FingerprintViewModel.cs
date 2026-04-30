// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Fingerprint page VM. Mirrors the legacy web's coherence-validator
/// surface: per-profile score (overall + 4 sub-scores), list of
/// per-check results, regenerate / reshuffle / re-validate actions.
///
/// State model:
///   • Profile dropdown picks which profile to score
///   • OnNavigatedTo loads the list and scores the first profile
///   • Regenerate / Reshuffle write to DB and re-score
///   • History tab pulls from fingerprint_audits
/// </summary>
public sealed partial class FingerprintViewModel : BaseViewModel
{
    private readonly IFingerprintService _fp;
    private readonly IProfileService _profiles;
    private readonly IProfileRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly ILogger<FingerprintViewModel> _log;

    private readonly Brush _okBrush;
    private readonly Brush _warnBrush;
    private readonly Brush _errBrush;
    private readonly Brush _dimBrush;

    public FingerprintViewModel(
        IFingerprintService fp,
        IProfileService profiles,
        IProfileRunner runner,
        IDialogService dialogs,
        ILogger<FingerprintViewModel> log)
    {
        _fp       = fp;
        _profiles = profiles;
        _runner   = runner;
        _dialogs  = dialogs;
        _log      = log;

        _okBrush   = (Brush)(Application.Current?.TryFindResource("OkBrush")    ?? Brushes.LimeGreen);
        _warnBrush = (Brush)(Application.Current?.TryFindResource("WarnBrush")  ?? Brushes.Orange);
        _errBrush  = (Brush)(Application.Current?.TryFindResource("ErrBrush")   ?? Brushes.IndianRed);
        _dimBrush  = (Brush)(Application.Current?.TryFindResource("TextDim")    ?? Brushes.Gray);

        ScoreBrush = _okBrush;

        // External testers — curated list. Order matches the legacy
        // web's display order (most-trusted first). Each tester is
        // checked by default; the "Probe in profile" command opens
        // every checked one in the launched browser.
        ExternalTesters = new ObservableCollection<ExternalTester>
        {
            new() { Name = "CreepJS",            Icon = "🧙",  Url = "https://abrahamjuliot.github.io/creepjs/",
                    Description = "Trust score 0-100, the strictest grader." },
            new() { Name = "Sannysoft Bot Test", Icon = "👹",  Url = "https://bot.sannysoft.com/",
                    Description = "The classic Selenium leak panel." },
            new() { Name = "Pixelscan",          Icon = "🍣",  Url = "https://pixelscan.net/",
                    Description = "Canvas/WebGL hash uniqueness + geo correlation." },
            new() { Name = "AmIUnique",          Icon = "🔍",  Url = "https://amiunique.org/fingerprint",
                    Description = "Compares to a public fingerprint DB." },
            new() { Name = "BrowserLeaks",       Icon = "💧",  Url = "https://browserleaks.com/",
                    Description = "Per-API leak breakdown. Gold standard." },
            new() { Name = "Fingerprint.com BotD",Icon = "🛡",  Url = "https://fingerprint.com/products/bot-detection/",
                    Description = "The realest test — commercial bot-detect demo." },
        };
    }

    /// <summary>
    /// External fingerprint testers (CreepJS, BrowserLeaks, etc.).
    /// Each tester's IsSelected drives whether "Probe in profile" opens it.
    /// </summary>
    public ObservableCollection<ExternalTester> ExternalTesters { get; }

    /// <summary>
    /// All device templates (catalog) for the left-side selector.
    /// Click a row to switch the focused profile's template_id and
    /// re-score immediately. Filtered by <see cref="TemplateFilter"/>.
    /// </summary>
    public ObservableCollection<DeviceTemplateRow> Templates { get; } = new();

    [ObservableProperty] private string _templateFilter = "all"; // all|desktop|laptop|phone

    partial void OnTemplateFilterChanged(string value) => RefreshTemplateList();

    public ObservableCollection<string> ProfileNames { get; } = new();

    [ObservableProperty] private string? _selectedProfile;

    /// <summary>
    /// Cancels the in-flight RefreshAsync when the user toggles the
    /// profile dropdown again before the previous score completes.
    /// Without this, two RefreshAsync calls could land out of order
    /// and the UI would briefly show stale data for the wrong profile.
    /// </summary>
    private CancellationTokenSource? _refreshCts;

    partial void OnSelectedProfileChanged(string? value)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        _ = RefreshAsync(_refreshCts.Token);
    }

    // ─── Score card ────────────────────────────────────────────
    [ObservableProperty] private int _overallScore;
    [ObservableProperty] private string _scoreLabel = "—";
    [ObservableProperty] private Brush  _scoreBrush;
    [ObservableProperty] private string _summaryLine = "";

    [ObservableProperty] private int _identityScore;
    [ObservableProperty] private int _hardwareScore;
    [ObservableProperty] private int _networkScore;
    [ObservableProperty] private int _automationScore;

    public ObservableCollection<CheckRowVm> Checks { get; } = new();

    [ObservableProperty] private int _passCount;
    [ObservableProperty] private int _warnCount;
    [ObservableProperty] private int _failCount;
    [ObservableProperty] private int _skipCount;

    [ObservableProperty] private bool _isWorking;

    public override async Task OnNavigatedToAsync()
    {
        try
        {
            var list = await _profiles.ListAsync();
            ProfileNames.Clear();
            foreach (var p in list) ProfileNames.Add(p.Name);
            if (ProfileNames.Count > 0 && SelectedProfile is null)
                SelectedProfile = ProfileNames[0];
            else
                await RefreshAsync();
            RefreshTemplateList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fingerprint page initial load failed");
        }
    }

    private void RefreshTemplateList()
    {
        // The catalog is static; filter & rebuild on every nav and on
        // template-filter chip changes. Total weight is computed across
        // the FILTERED set so the percentages add up to 100% within
        // the chosen filter.
        var filter = TemplateFilter ?? "all";
        var all = DeviceTemplateCatalog.All;
        IEnumerable<DeviceTemplate> filtered = filter switch
        {
            "desktop" => all.Where(t => t.FormFactor == FormFactor.Desktop && !t.IsLaptop),
            "laptop"  => all.Where(t => t.IsLaptop),
            "phone"   => all.Where(t => t.FormFactor == FormFactor.Mobile),
            _         => all,
        };
        var ordered = filtered
            .OrderByDescending(t => t.Weight)
            .ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var totalWeight = Math.Max(1, ordered.Sum(t => t.Weight));
        Templates.Clear();
        foreach (var t in ordered)
        {
            var pct = 100.0 * t.Weight / totalWeight;
            Templates.Add(new DeviceTemplateRow
            {
                Id          = t.Id,
                Label       = t.ToLabel(),
                FormFactor  = t.FormFactor.ToString().ToLowerInvariant(),
                IsLaptop    = t.IsLaptop,
                WeightPct   = $"{pct:F1}%",
            });
        }
    }

    [RelayCommand]
    private async Task SelectTemplateAsync(DeviceTemplateRow? row)
    {
        if (row is null || string.IsNullOrEmpty(SelectedProfile)) return;
        try
        {
            var p = await _profiles.GetAsync(SelectedProfile)
                ?? throw new InvalidOperationException("Profile gone");
            // We only update the TemplateId; everything else stays the
            // same. UpdateAsync persists; then we re-score so the user
            // sees the consequences of the swap immediately.
            var updated = new Profile
            {
                Name             = p.Name,
                GroupName        = p.GroupName,
                TemplateId       = row.Id,
                Language         = p.Language,
                ProxySlug        = p.ProxySlug,
                IsReady          = p.IsReady,
                EnrichOnFirstRun = p.EnrichOnFirstRun,
                LastRunAt        = p.LastRunAt,
                RunCount         = p.RunCount,
                Note             = p.Note,
                CreatedAt        = p.CreatedAt,
                UpdatedAt        = DateTime.UtcNow,
                FpRegenSalt      = p.FpRegenSalt,
                FpNoiseSalt      = p.FpNoiseSalt,
            };
            await _profiles.UpdateAsync(updated);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Switch template failed");
            await _dialogs.ConfirmAsync("Switch failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(SelectedProfile)) return;
        var snapshot = SelectedProfile; // capture so a mid-flight switch doesn't apply to wrong profile
        IsBusy = true;
        try
        {
            var score = await _fp.GetScoreAsync(snapshot, ct);
            // If the user switched profiles while we awaited, drop
            // the stale result on the floor.
            if (ct.IsCancellationRequested) return;
            if (!string.Equals(snapshot, SelectedProfile, StringComparison.Ordinal)) return;
            ApplyScore(score);
        }
        catch (OperationCanceledException) { /* expected on profile switch */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetScore failed for '{Profile}'", snapshot);
            ScoreLabel = "ERROR";
            ScoreBrush = _errBrush;
            SummaryLine = ex.Message;
            Checks.Clear();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RegenerateAsync()
    {
        if (string.IsNullOrEmpty(SelectedProfile) || IsWorking) return;
        IsWorking = true;
        try
        {
            var score = await _fp.RegenerateAsync(SelectedProfile);
            ApplyScore(score);
            await _dialogs.ConfirmAsync(
                "Fingerprint regenerated",
                $"New score: {score.Overall}/100 ({score.Label}). " +
                "Next browser launch uses the fresh payload.",
                "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Regenerate failed");
            await _dialogs.ConfirmAsync("Regenerate failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
        finally { IsWorking = false; }
    }

    [RelayCommand]
    private async Task ReshuffleAsync()
    {
        if (string.IsNullOrEmpty(SelectedProfile) || IsWorking) return;
        IsWorking = true;
        try
        {
            var score = await _fp.ReshuffleAsync(SelectedProfile);
            ApplyScore(score);
            await _dialogs.ConfirmAsync(
                "Noise re-rolled",
                $"Canvas/WebGL/audio jitter rolled. Score: {score.Overall}/100.",
                "OK", ConfirmSeverity.Info);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reshuffle failed");
            await _dialogs.ConfirmAsync("Reshuffle failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
        finally { IsWorking = false; }
    }

    /// <summary>
    /// Launch the patched browser for the focused profile and copy
    /// every selected tester URL into the clipboard so the user can
    /// paste them as new tabs. We don't auto-navigate yet — that
    /// requires CDP automation per-tab and is on the Phase 11 roadmap.
    /// For v1 the goal is "click → browser opens with the right
    /// fingerprint payload, here are the URLs you wanted".
    /// </summary>
    [RelayCommand]
    private async Task ProbeInProfileAsync()
    {
        if (string.IsNullOrEmpty(SelectedProfile))
        {
            await _dialogs.ConfirmAsync(
                "No profile selected",
                "Pick a profile in the dropdown above first.",
                "OK", ConfirmSeverity.Warning);
            return;
        }
        var selected = ExternalTesters.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            await _dialogs.ConfirmAsync(
                "No testers selected",
                "Tick at least one tester card before probing.",
                "OK", ConfirmSeverity.Warning);
            return;
        }

        try
        {
            // Start the profile if it isn't already running.
            if (!_runner.ActiveProfileNames.Contains(SelectedProfile))
            {
                var profile = await _profiles.GetAsync(SelectedProfile)
                    ?? throw new InvalidOperationException($"Profile '{SelectedProfile}' not found");
                _ = await _runner.StartAsync(profile);
            }

            // Copy URLs to clipboard — newline-separated so paste-as-
            // multiple-tabs works in Chrome's address bar (Chromium
            // recognises newline-pasted URLs and opens each as a tab).
            var clipText = string.Join(Environment.NewLine, selected.Select(t => t.Url));
            try { Clipboard.SetText(clipText); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Clipboard set failed");
            }

            var msg = $"Profile '{SelectedProfile}' is up.\n\n" +
                      $"{selected.Count} tester URL(s) copied to clipboard:\n  • " +
                      string.Join("\n  • ", selected.Select(t => $"{t.Name} — {t.Url}")) +
                      "\n\nPaste into the address bar (multiple URLs open as tabs).";
            await _dialogs.ConfirmAsync(
                "Probe ready", msg, "OK", ConfirmSeverity.Info);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Probe-in-profile failed");
            await _dialogs.ConfirmAsync(
                "Could not start probe", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
    }

    private void ApplyScore(FingerprintScore s)
    {
        OverallScore     = s.Overall;
        ScoreLabel       = s.Label;
        ScoreBrush       = s.Overall >= 85 ? _okBrush
                         : s.Overall >= 75 ? _warnBrush
                         : _errBrush;
        SummaryLine      = $"{s.Overall}/100 — {s.CriticalIssues} critical, {s.Warnings} warnings";
        IdentityScore    = s.Identity;
        HardwareScore    = s.Hardware;
        NetworkScore     = s.Network;
        AutomationScore  = s.Automation;

        Checks.Clear();
        foreach (var c in s.Checks)
            Checks.Add(CheckRowVm.From(c, _okBrush, _warnBrush, _errBrush, _dimBrush));

        PassCount = s.Checks.Count(c => c.Status == FingerprintCheckStatus.Pass);
        WarnCount = s.Checks.Count(c => c.Status == FingerprintCheckStatus.Warn);
        FailCount = s.Checks.Count(c => c.Status == FingerprintCheckStatus.Fail);
        SkipCount = s.Checks.Count(c => c.Status == FingerprintCheckStatus.Skip);
    }
}

/// <summary>
/// Row in the Device-templates panel on the Fingerprint page.
/// Click → switches the focused profile's template_id.
/// </summary>
public sealed record DeviceTemplateRow
{
    public required string Id          { get; init; }
    public required string Label       { get; init; }
    public required string FormFactor  { get; init; }
    public required bool   IsLaptop    { get; init; }
    public required string WeightPct   { get; init; }
}

/// <summary>
/// One external fingerprint-tester entry on the Fingerprint page.
/// IsSelected is mutable (drives the "Probe in profile" target set).
/// </summary>
public sealed partial class ExternalTester : ObservableObject
{
    public required string Name        { get; init; }
    public required string Icon        { get; init; }
    public required string Url         { get; init; }
    public required string Description { get; init; }

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _resultText = "no result yet — click 🚀 below";
}

public sealed record CheckRowVm
{
    public required string Title  { get; init; }
    public required string Detail { get; init; }
    public required string StatusText { get; init; }
    public required Brush  StatusBrush { get; init; }
    public required string SeverityLabel { get; init; }

    public static CheckRowVm From(FingerprintCheck c, Brush ok, Brush warn, Brush err, Brush dim) => new()
    {
        Title = c.Title,
        Detail = c.Detail,
        StatusText = c.Status switch
        {
            FingerprintCheckStatus.Pass => "PASS",
            FingerprintCheckStatus.Warn => "WARN",
            FingerprintCheckStatus.Fail => "FAIL",
            _                           => "SKIP",
        },
        StatusBrush = c.Status switch
        {
            FingerprintCheckStatus.Pass => ok,
            FingerprintCheckStatus.Warn => warn,
            FingerprintCheckStatus.Fail => err,
            _                           => dim,
        },
        SeverityLabel = c.Severity switch
        {
            FingerprintCheckSeverity.Critical => "CRITICAL",
            FingerprintCheckSeverity.Warning  => "WARNING",
            _                                 => "INFO",
        },
    };
}
