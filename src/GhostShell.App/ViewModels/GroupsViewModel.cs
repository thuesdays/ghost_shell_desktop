// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using System.Numerics;
using GhostShell.Core.Chains;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Core.Wallets;
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
    private readonly IVaultService        _vault;
    private readonly IScriptService       _scripts;
    private readonly IChainRpcClient      _rpc;
    private readonly ILogger<GroupsViewModel> _log;

    public GroupsViewModel(
        IProfileGroupService groups,
        IProfileService profiles,
        IProfileRunner runner,
        IDialogService dialogs,
        IVaultService vault,
        IScriptService scripts,
        IChainRpcClient rpc,
        ILogger<GroupsViewModel> log)
    {
        _groups   = groups;
        _profiles = profiles;
        _runner   = runner;
        _dialogs  = dialogs;
        _vault    = vault;
        _scripts  = scripts;
        _rpc      = rpc;
        _log      = log;

        foreach (var c in CuratedChainCatalog.Entries) AvailableChains.Add(c);
        SelectedChain = AvailableChains.FirstOrDefault(c => c.Id == CuratedChainCatalog.DefaultChainId);

        _runner.ActiveChanged += (_, _) =>
            Application.Current?.Dispatcher.BeginInvoke(SyncRunningCounts);
    }

    public ObservableCollection<GroupRowVm> Items { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _searchText = "";

    /// <summary>Full snapshot — search filters into <see cref="Items"/>.</summary>
    private readonly List<GroupRowVm> _all = new();

    // ─── Phase 56 — crypto-farm panel ─────────────────────────────────
    /// <summary>The group whose farm panel is shown (list selection).</summary>
    [ObservableProperty] private GroupRowVm? _selectedGroup;
    /// <summary>Per-member wallet readiness for the selected farm.</summary>
    public ObservableCollection<FarmMemberRowVm> FarmMembers { get; } = new();
    /// <summary>Scripts the operator can push across the farm.</summary>
    public ObservableCollection<Script> AvailableScripts { get; } = new();
    [ObservableProperty] private Script? _selectedFarmScript;
    [ObservableProperty] private string _farmSummary = "";
    [ObservableProperty] private bool _hasSelectedGroup;
    [ObservableProperty] private bool _vaultLocked;
    [ObservableProperty] private bool _isFarmBusy;
    /// <summary>Monotonic token — a newer farm load invalidates older in-flight
    /// ones so fire-and-forget selections can't interleave into FarmMembers.</summary>
    private int _farmLoadGen;

    /// <summary>Chains the operator can check balances on.</summary>
    public ObservableCollection<ChainDescriptor> AvailableChains { get; } = new();
    [ObservableProperty] private ChainDescriptor? _selectedChain;
    /// <summary>Cancels an in-flight balance sweep when the panel closes or a new sweep starts.</summary>
    private CancellationTokenSource? _balanceCts;

    public override async Task OnNavigatedToAsync()
    {
        await ReloadAsync();
        await LoadScriptsAsync();
    }

    private async Task LoadScriptsAsync()
    {
        try
        {
            var scripts = await _scripts.ListAsync();
            AvailableScripts.Clear();
            foreach (var sc in scripts) AvailableScripts.Add(sc);
        }
        catch (Exception ex) { _log.LogError(ex, "Farm: load scripts failed"); }
    }

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
            // ReloadAsync builds brand-new GroupRowVm instances, so a farm panel
            // open against an OLD row would point at an orphan. Re-resolve the
            // selection to the fresh row with the same id (or close the panel).
            if (SelectedGroup is { } sel)
                SelectedGroup = _all.FirstOrDefault(r => r.Group.Id == sel.Group.Id);
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
                catch (GhostShell.Core.Common.ProfileBusyException)
                {
                    // Phase 71oo — group member's launch is already in
                    // flight on another path (scheduler tick, Run-now,
                    // queue). Skip silently and continue with next
                    // member; an ERROR log line would falsely flag
                    // this as a problem.
                    _log.LogInformation(
                        "Group-start: '{Name}' skipped — already launching elsewhere",
                        name);
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

    // ─── Phase 56 — crypto-farm panel ─────────────────────────────────

    partial void OnSelectedGroupChanged(GroupRowVm? value)
        => _ = LoadFarmAsync(value);

    [RelayCommand]
    private void OpenFarm(GroupRowVm? group) => SelectedGroup = group;

    [RelayCommand]
    private void CloseFarm()
    {
        _balanceCts?.Cancel();
        _balanceCts?.Dispose();
        _balanceCts = null;
        SelectedGroup = null;
    }

    [RelayCommand]
    private async Task EditWalletSelectorsAsync() => await _dialogs.ShowWalletSelectorEditorAsync();

    /// <summary>Compute per-member wallet readiness for the selected farm by
    /// cross-referencing its members against the vault's crypto_wallet items.
    /// Addresses are non-secret (shown masked); passwords are checked for
    /// PRESENCE only and never surfaced.</summary>
    private async Task LoadFarmAsync(GroupRowVm? group)
    {
        // Invalidate any in-flight load (fire-and-forget selections must not
        // interleave). This runs on the UI thread before the first await.
        var gen = ++_farmLoadGen;
        HasSelectedGroup = group is not null;
        if (group is null) { FarmMembers.Clear(); FarmSummary = ""; return; }

        IsFarmBusy = true;
        try
        {
            var detailed = await _groups.GetAsync(group.Group.Id);
            var members = detailed?.Members?.ToList() ?? new List<string>();

            await _vault.RefreshStateAsync();
            var locked = !_vault.IsUnlocked;

            // Heavy work — DB list + one decrypt PER member — off the UI thread
            // so a 50-member farm doesn't stutter the panel. Build into a plain
            // list; only touch ObservableCollection after we marshal back.
            var statuses = await Task.Run(async () =>
            {
                var byProfile = new Dictionary<string, VaultItem>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var w in await _vault.ListAsync(kind: "crypto_wallet"))
                        if (!string.IsNullOrEmpty(w.ProfileName))
                            byProfile[w.ProfileName!] = w;
                }
                catch (Exception ex) { _log.LogError(ex, "Farm: list wallets failed"); }

                var list = new List<FarmMemberStatus>();
                foreach (var name in members)
                {
                    var hasWallet = byProfile.TryGetValue(name, out var item);
                    bool hasPassword = false;
                    string? address = null;

                    if (hasWallet && !locked)
                    {
                        try
                        {
                            var got = await _vault.GetClearAsync(item!.Id);
                            if (got is { } g)
                            {
                                g.clear.TryGetValue("address", out address);
                                hasPassword = g.clear.TryGetValue("wallet_password", out var pw)
                                              && !string.IsNullOrWhiteSpace(pw);
                            }
                        }
                        catch (Exception ex) { _log.LogDebug(ex, "Farm: read wallet for {Name} failed", name); }
                    }

                    var st = FarmReadiness.Evaluate(name, hasWallet, hasPassword, address);
                    if (hasWallet && locked)
                        st = st with { Reason = "vault locked — unlock to verify" };
                    list.Add(st);
                }
                return list;
            });

            // A newer selection started while we awaited → drop these results
            // (the newer load owns FarmMembers / IsFarmBusy now).
            if (gen != _farmLoadGen) return;

            VaultLocked = locked;
            FarmMembers.Clear();
            foreach (var st in statuses) FarmMembers.Add(new FarmMemberRowVm(st));

            // When locked, readiness is genuinely unknown — don't mislabel every
            // member as "no password". Show an explicit locked summary instead.
            FarmSummary = locked
                ? $"🔒 vault locked — unlock to verify {statuses.Count} member(s)"
                : FarmReadiness.Summarize(statuses).Label;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Farm: load failed for group {Id}", group.Group.Id);
            if (gen == _farmLoadGen) { FarmMembers.Clear(); FarmSummary = "⚠ failed to load farm — see logs"; }
        }
        finally { if (gen == _farmLoadGen) IsFarmBusy = false; }
    }

    /// <summary>Gate for the farm Run/Refresh commands — disabled while a load
    /// or mass-run is in flight (also notified from <see cref="OnIsFarmBusyChanged"/>).</summary>
    private bool CanFarmAct() => !IsFarmBusy;

    partial void OnIsFarmBusyChanged(bool value)
    {
        RunWalletTaskCommand.NotifyCanExecuteChanged();
        RefreshFarmCommand.NotifyCanExecuteChanged();
        FetchBalancesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanFarmAct))]
    private async Task RefreshFarmAsync()
    {
        if (IsFarmBusy) return;
        await LoadFarmAsync(SelectedGroup);
    }

    /// <summary>Fetch the native-coin balance of every member that has an
    /// address, on the selected chain, via read-only RPC (off the UI thread).</summary>
    [RelayCommand(CanExecute = nameof(CanFarmAct))]
    private async Task FetchBalancesAsync()
    {
        if (IsFarmBusy) return;
        var chain = SelectedChain;
        if (chain is null) return;

        // Snapshot the rows + addresses on the UI thread.
        var targets = FarmMembers.Where(m => !string.IsNullOrEmpty(m.AddressFull)).ToList();
        if (targets.Count == 0) return;

        // Cancellable: a dead RPC otherwise pins the panel. Dispose the prior
        // CTS before replacing (audit M3 — don't leak one per sweep).
        _balanceCts?.Cancel();
        _balanceCts?.Dispose();
        _balanceCts = new CancellationTokenSource();
        var token = _balanceCts.Token;

        IsFarmBusy = true;
        try
        {
            foreach (var m in FarmMembers) m.Balance = "…";

            // Audit (perf C1): fan out with bounded concurrency instead of a
            // serial members×15s wall. Continuations resume on the UI context
            // (entered on the UI thread), so writing m.Balance stays thread-safe.
            using var gate = new SemaphoreSlim(8);
            var tasks = targets.Select(async m =>
            {
                try { await gate.WaitAsync(token); }
                catch (OperationCanceledException) { return; }
                try
                {
                    var raw = await _rpc.GetNativeBalanceAsync(chain, m.AddressFull, token);
                    m.Balance = $"{ChainUnits.FormatBalance(raw, chain.Decimals)} {chain.NativeSymbol}";
                }
                catch (OperationCanceledException) { /* sweep cancelled */ }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Farm: balance fetch failed for {Addr}", m.AddressFull);
                    m.Balance = "rpc error";
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { /* sweep cancelled */ }
        finally { IsFarmBusy = false; }
    }

    /// <summary>Push the selected script to every farm member as its task and
    /// launch the group. Assignment is PERSISTED (a farm "remembers" its job),
    /// which the confirm dialog states plainly.</summary>
    [RelayCommand(CanExecute = nameof(CanFarmAct))]
    private async Task RunWalletTaskAsync()
    {
        if (IsFarmBusy) return;
        if (SelectedGroup is null) return;
        if (SelectedFarmScript is null)
        {
            await _dialogs.ConfirmAsync(
                "Pick a task first",
                "Choose a script in the farm panel's dropdown — it becomes the task " +
                "assigned to every member.",
                "OK");
            return;
        }

        var detailed = await _groups.GetAsync(SelectedGroup.Group.Id);
        var members = detailed?.Members?.ToList() ?? new List<string>();
        if (members.Count == 0) return;

        var script = SelectedFarmScript;
        var ok = await _dialogs.ConfirmAsync(
            $"Run '{script.Name}' across {members.Count} farm member(s)?",
            $"This ASSIGNS '{script.Name}' as the task for every member of " +
            $"'{detailed!.Name}' (persisted — the farm remembers its job) and launches " +
            "the idle ones. Cap = " +
            (detailed.MaxParallel?.ToString() ?? "global default") + ". " +
            "Each member uses its own profile, proxy, and bound wallet.",
            "Run on farm");
        if (!ok) return;

        IsFarmBusy = true;
        try
        {
            await Task.Run(async () =>
            {
                // 1) Assign the task to EVERY member first (explicit + complete,
                //    so the farm is uniformly retasked even if a launch is skipped).
                foreach (var name in members)
                {
                    try
                    {
                        var p = await _profiles.GetAsync(name);
                        if (p is null) continue;
                        if (p.AssignedScriptId != script.Id)
                            await _profiles.UpdateAsync(p with { AssignedScriptId = script.Id });
                    }
                    catch (Exception ex) { _log.LogError(ex, "Farm-run: assign '{Name}' failed", name); }
                }

                // 2) Launch the idle members (staggered so we don't spawn N
                //    chromedrivers in one tick).
                foreach (var name in members)
                {
                    if (_runner.ActiveProfileNames.Contains(name)) continue;
                    var p = await _profiles.GetAsync(name);
                    if (p is null) continue;
                    try
                    {
                        await _runner.StartAsync(p);
                    }
                    catch (GhostShell.Core.Common.ProfileBusyException)
                    {
                        _log.LogInformation("Farm-run: '{Name}' already launching — skipped", name);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Farm-run: '{Name}' failed", name);
                    }
                    await Task.Delay(150);
                }
            });
        }
        finally { IsFarmBusy = false; }

        await LoadFarmAsync(SelectedGroup);
    }
}

/// <summary>
/// One farm member's wallet-readiness row for the crypto-farm panel.
/// Wraps a pure <see cref="FarmMemberStatus"/> with UI-friendly glyph/brush
/// keys (resolved by the same ResourceKeyToBrush converter the rest of the
/// app uses).
/// </summary>
public sealed partial class FarmMemberRowVm : ObservableObject
{
    private readonly FarmMemberStatus _s;
    public FarmMemberRowVm(FarmMemberStatus s) => _s = s;

    public string Name          => _s.Name;
    public bool   Ready         => _s.Ready;
    public string AddressMasked => string.IsNullOrEmpty(_s.AddressMasked) ? "—" : _s.AddressMasked;
    public string AddressFull   => _s.AddressFull;
    public string Reason        => _s.Reason;
    public string StatusGlyph   => _s.Ready ? "✓" : "•";
    public string StatusBrushKey => _s.Ready ? "OkBrush" : "WarnBrush";
    public string StatusText    => _s.Ready ? "ready" : _s.Reason;

    /// <summary>Live native-coin balance (filled by Fetch balances); empty until then.</summary>
    [ObservableProperty] private string _balance = "";
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
