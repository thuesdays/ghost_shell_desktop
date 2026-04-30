// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Groups page VM — port of <c>dashboard/pages/groups.html</c>.
/// One row per group with member count, max-parallel cap, and
/// "Start group / Stop group" buttons. Edit-modal lives in
/// <see cref="IDialogService.ShowGroupEditorAsync"/>.
///
/// Group start/stop iterates the member list and pumps the runner
/// one launch at a time — same shape as the legacy web's start
/// loop, with a small inter-launch delay so we don't shoot 50
/// chromedriver spawns at the box in one frame.
/// </summary>
public sealed partial class GroupsViewModel : BaseViewModel
{
    private readonly IProfileGroupService _groups;
    private readonly IProfileService      _profiles;
    private readonly IProfileRunner       _runner;
    private readonly IDialogService       _dialogs;
    private readonly ILogger<GroupsViewModel> _log;

    public GroupsViewModel(
        IProfileGroupService groups,
        IProfileService profiles,
        IProfileRunner runner,
        IDialogService dialogs,
        ILogger<GroupsViewModel> log)
    {
        _groups   = groups;
        _profiles = profiles;
        _runner   = runner;
        _dialogs  = dialogs;
        _log      = log;

        _runner.ActiveChanged += (_, _) =>
            Application.Current?.Dispatcher.BeginInvoke(SyncRunningCounts);
    }

    public ObservableCollection<GroupRowVm> Items { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _searchText = "";

    /// <summary>Full snapshot — search filters into <see cref="Items"/>.</summary>
    private readonly List<GroupRowVm> _all = new();

    public override async Task OnNavigatedToAsync() => await ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            _all.Clear();
            foreach (var g in await _groups.ListAsync())
                _all.Add(new GroupRowVm(g));
            ApplyFilter();
            SyncRunningCounts();
            _log.LogInformation("Groups loaded: {Count}", _all.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Group list failed");
        }
        finally { IsBusy = false; }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var needle = SearchText?.Trim() ?? "";
        IEnumerable<GroupRowVm> q = _all;
        if (!string.IsNullOrEmpty(needle))
        {
            q = _all.Where(r =>
                r.Group.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                (r.Group.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        Items.Clear();
        foreach (var r in q) Items.Add(r);
        IsEmpty = Items.Count == 0;
    }

    /// <summary>Refresh the per-group "running N of M" counter
    /// without re-fetching from DB. The runner's active set is the
    /// authority for liveness.</summary>
    private void SyncRunningCounts()
    {
        var active = _runner.ActiveProfileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _all)
            row.RunningCount = row.Group.Members.Count(active.Contains);
        // After mutating the underlying rows, re-pump Items so the
        // filtered slice picks up the new counts (they're observable
        // properties, but the search-result subset may differ).
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var allProfiles = await _profiles.ListAsync();
        // IDialogService.ShowGroupEditorAsync returns Task<bool> —
        // true = saved, false = cancelled. No null comparison.
        var saved = await _dialogs.ShowGroupEditorAsync(null, allProfiles);
        if (!saved) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task EditAsync(GroupRowVm? selected)
    {
        if (selected is null) return;
        // Pull a fresh copy with full member list — the row VM only
        // carries the count from List().
        var detailed = await _groups.GetAsync(selected.Group.Id);
        if (detailed is null) return;

        var allProfiles = await _profiles.ListAsync();
        var saved = await _dialogs.ShowGroupEditorAsync(detailed, allProfiles);
        if (!saved) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(GroupRowVm? selected)
    {
        if (selected is null) return;
        var ok = await _dialogs.ConfirmAsync(
            $"Delete group '{selected.Group.Name}'?",
            "The group itself is removed; member profiles are NOT touched.",
            "Delete",
            ConfirmSeverity.Danger);
        if (!ok) return;

        try
        {
            await _groups.DeleteAsync(selected.Group.Id);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Delete group #{Id} failed", selected.Group.Id);
        }
    }

    [RelayCommand]
    private async Task StartGroupAsync(GroupRowVm? selected)
    {
        if (selected is null) return;
        var detailed = await _groups.GetAsync(selected.Group.Id);
        if (detailed is null || detailed.Members.Count == 0) return;

        var ok = await _dialogs.ConfirmAsync(
            $"Start {detailed.Members.Count} profile(s) in '{detailed.Name}'?",
            "Each member launches its own Chrome instance with its own " +
            "user-data-dir and proxy. Group cap = " +
            (detailed.MaxParallel?.ToString() ?? "global default") + ".",
            "Start group");
        if (!ok) return;

        await Task.Run(async () =>
        {
            foreach (var name in detailed.Members)
            {
                var profile = await _profiles.GetAsync(name);
                if (profile is null) continue;
                try
                {
                    if (_runner.ActiveProfileNames.Contains(name)) continue;
                    await _runner.StartAsync(profile);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Group-start: '{Name}' failed", name);
                }
                // Tiny stagger so we don't fire 20 chromedriver spawns
                // in the same dispatcher tick — the box would meaningfully
                // saturate before any of them have a chance to claim
                // their unique --remote-debugging-port.
                await Task.Delay(150);
            }
        });
    }

    [RelayCommand]
    private async Task StopGroupAsync(GroupRowVm? selected)
    {
        if (selected is null) return;
        var detailed = await _groups.GetAsync(selected.Group.Id);
        if (detailed is null) return;

        var live = detailed.Members
            .Where(_runner.ActiveProfileNames.Contains)
            .ToList();
        if (live.Count == 0) return;

        await Task.Run(async () =>
        {
            foreach (var name in live)
            {
                try
                {
                    await _runner.StopAsync(name);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Group-stop: '{Name}' failed", name);
                }
            }
        });
    }
}

/// <summary>
/// Row VM that wraps a <see cref="ProfileGroup"/> with a mutable
/// running-count so the "▶ 3 / 8 running" footer updates live as
/// member profiles start/stop.
/// </summary>
public sealed partial class GroupRowVm : ObservableObject
{
    public ProfileGroup Group { get; }

    [ObservableProperty] private int _runningCount;

    public GroupRowVm(ProfileGroup group)
    {
        Group = group;
    }

    public string Name        => Group.Name;
    public string? Description => Group.Description;
    public int MemberCount     => Group.MemberCount;
    public int? MaxParallel    => Group.MaxParallel;

    public string CapLabel
        => Group.MaxParallel is { } cap ? $"cap {cap}" : "cap: global default";

    public bool HasRunning => RunningCount > 0;
    public string RunningLabel
        => HasRunning ? $"● {RunningCount} running" : "";

    partial void OnRunningCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(RunningLabel));
    }
}
