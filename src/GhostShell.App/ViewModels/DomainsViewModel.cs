// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 34 — Domains page VM. Manages three domain lists (my / target / block)
/// with multi-line text editing, live counts, dirty tracking, and debounced autosave.
/// </summary>
public sealed partial class DomainsViewModel : BaseViewModel
{
    private readonly IDomainListService _domains;
    private readonly ILogger<DomainsViewModel> _log;

    public DomainsViewModel(
        IDomainListService domains,
        ILogger<DomainsViewModel> log)
    {
        _domains = domains;
        _log     = log;

        // Shared 600 ms debounce timer for all three text fields. When any
        // text changes, Stop + Start this timer so a burst of edits collapses
        // into one save cycle.
        _autoSaveDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _autoSaveDebounce.Tick += async (_, _) =>
        {
            _autoSaveDebounce.Stop();
            await SaveNowAsync();
        };
    }

    private readonly DispatcherTimer _autoSaveDebounce;

    // ─── State ──────────────────────────────────────────────────────────

    [ObservableProperty] private string _myDomainsText = "";
    [ObservableProperty] private string _targetDomainsText = "";
    [ObservableProperty] private string _blockDomainsText = "";

    [ObservableProperty] private int _myCount;
    [ObservableProperty] private int _targetCount;
    [ObservableProperty] private int _blockCount;

    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string? _statusText;

    // Snapshot of the last saved state to detect dirty.
    private string _lastSavedMyDomainsText = "";
    private string _lastSavedTargetDomainsText = "";
    private string _lastSavedBlockDomainsText = "";

    // ─── Lifecycle ──────────────────────────────────────────────────────

    public override async Task OnNavigatedToAsync()
    {
        try
        {
            var myList = await _domains.ListAsync(DomainListKind.My);
            var targetList = await _domains.ListAsync(DomainListKind.Target);
            var blockList = await _domains.ListAsync(DomainListKind.Block);

            MyDomainsText = string.Join("\n", myList.Select(e => e.Domain));
            TargetDomainsText = string.Join("\n", targetList.Select(e => e.Domain));
            BlockDomainsText = string.Join("\n", blockList.Select(e => e.Domain));

            // Snapshot the loaded state as clean.
            _lastSavedMyDomainsText = MyDomainsText;
            _lastSavedTargetDomainsText = TargetDomainsText;
            _lastSavedBlockDomainsText = BlockDomainsText;

            UpdateCounts();
            IsDirty = false;
            StatusText = null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load domain lists");
        }
    }

    // ─── Text change handlers ────────────────────────────────────────────

    partial void OnMyDomainsTextChanged(string value)
    {
        UpdateCounts();
        MarkDirty();
        _autoSaveDebounce.Stop();
        _autoSaveDebounce.Start();
    }

    partial void OnTargetDomainsTextChanged(string value)
    {
        UpdateCounts();
        MarkDirty();
        _autoSaveDebounce.Stop();
        _autoSaveDebounce.Start();
    }

    partial void OnBlockDomainsTextChanged(string value)
    {
        UpdateCounts();
        MarkDirty();
        _autoSaveDebounce.Stop();
        _autoSaveDebounce.Start();
    }

    // ─── Helper methods ─────────────────────────────────────────────────

    private void UpdateCounts()
    {
        MyCount = CountLines(MyDomainsText);
        TargetCount = CountLines(TargetDomainsText);
        BlockCount = CountLines(BlockDomainsText);
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var count = 0;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            // Skip empty lines and comment lines
            if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
            {
                count++;
            }
        }
        return count;
    }

    private void MarkDirty()
    {
        IsDirty = MyDomainsText != _lastSavedMyDomainsText ||
                  TargetDomainsText != _lastSavedTargetDomainsText ||
                  BlockDomainsText != _lastSavedBlockDomainsText;
    }

    // ─── Save command ───────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveNowAsync()
    {
        if (!IsDirty) return;

        IsSaving = true;
        try
        {
            var myList = ParseAndNormalize(MyDomainsText);
            var targetList = ParseAndNormalize(TargetDomainsText);
            var blockList = ParseAndNormalize(BlockDomainsText);

            await _domains.ReplaceAsync(DomainListKind.My, myList);
            await _domains.ReplaceAsync(DomainListKind.Target, targetList);
            await _domains.ReplaceAsync(DomainListKind.Block, blockList);

            // Snapshot the saved state.
            _lastSavedMyDomainsText = MyDomainsText;
            _lastSavedTargetDomainsText = TargetDomainsText;
            _lastSavedBlockDomainsText = BlockDomainsText;

            UpdateCounts();
            IsDirty = false;

            int totalDomains = myList.Count + targetList.Count + blockList.Count;
            StatusText = $"Saved {totalDomains} domains";

            // Clear the status text after 3 seconds.
            await Task.Delay(3000);
            StatusText = null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save domain lists");
            StatusText = "Error saving domains";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private static List<string> ParseAndNormalize(string text)
    {
        var result = new List<string>();

        if (string.IsNullOrEmpty(text)) return result;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();

            // Skip empty lines and comment lines
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                continue;

            var normalized = IDomainListService.Normalize(trimmed);
            if (!string.IsNullOrEmpty(normalized) && !result.Contains(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
