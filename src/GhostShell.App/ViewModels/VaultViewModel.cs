// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 25 — vault page VM. Mirrors the existing per-page pattern
/// (BaseViewModel + ObservableCollection items + filter state). The
/// page reflects the lock state via <see cref="IVaultService.IsUnlocked"/>;
/// when locked, only the unlock button is interactive and the items
/// list shows a locked placeholder.
/// </summary>
public sealed partial class VaultViewModel : BaseViewModel
{
    private readonly IVaultService _vault;
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private readonly ILogger<VaultViewModel> _log;

    public VaultViewModel(
        IVaultService vault,
        IProfileService profiles,
        IDialogService dialogs,
        ILogger<VaultViewModel> log)
    {
        _vault    = vault;
        _profiles = profiles;
        _dialogs  = dialogs;
        _log      = log;
        _vault.LockStateChanged += OnVaultLockStateChanged;

        // Phase 26 — TOTP repaint loop. Ticks every second so countdown
        // numbers + code rollovers (every 30s) reflect the current
        // wall-clock value. Rows that aren't TOTP no-op cheaply.
        _totpTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _totpTimer.Tick += (_, _) =>
        {
            foreach (var r in Items) r.RefreshTotp();
        };
    }

    public ObservableCollection<VaultItemRow> Items { get; } = new();

    private readonly DispatcherTimer _totpTimer;

    /// <summary>
    /// Phase 71c audit fix — track per-row PropertyChanged handlers
    /// so we can explicitly unsubscribe on each reload. Without this,
    /// every ReloadAsync installed a fresh closure that the previous
    /// reload couldn't match in -=, slowly leaking handlers (and the
    /// captured row/collection refs) across the session.
    /// </summary>
    private readonly List<(VaultItemRow Row, System.ComponentModel.PropertyChangedEventHandler Handler)>
        _rowHandlers = new();

    [ObservableProperty] private bool _isInitialized;
    [ObservableProperty] private bool _isUnlocked;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private bool    _isEmpty = true;
    [ObservableProperty] private int     _autoLockMinutes = 15;

    /// <summary>
    /// Phase 71i — the page filter dropdown switched from
    /// kind-based ("All / account / social / …") to tag-based now
    /// that every new item is "universal". The list is dynamic:
    /// rebuilt from <see cref="Items"/>' Tags strings on every reload.
    /// "All" is always first; the rest are distinct tag names sorted
    /// alphabetically. Selecting a tag filters Items in-memory.
    /// </summary>
    [ObservableProperty] private string _tagFilter = "All";

    /// <summary>Distinct tag list for the filter dropdown.
    /// Recomputed by <see cref="ReloadAsync"/> after Items is
    /// repopulated.</summary>
    public ObservableCollection<string> TagFilters { get; } = new() { "All" };

    partial void OnTagFilterChanged(string value)
    {
        // Re-apply filter without re-querying the DB — _allItems holds
        // the unfiltered snapshot from the latest ReloadAsync.
        ApplyTagFilter();
    }

    /// <summary>
    /// Phase 71 — count of currently-selected rows for the multiselect
    /// delete UI. Exposes a bool flag derived view (HasSelection) so
    /// the View can bind the red "Delete N" button's visibility +
    /// enabled state without a converter.
    /// </summary>
    [ObservableProperty] private int _selectedCount;
    public bool HasSelection => SelectedCount > 0;
    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsAllSelected));
    }

    /// <summary>
    /// Phase 71d — header-checkbox state for "select all" in the
    /// Vault grid. Three-state binding:
    ///   • <c>true</c>  → every row is checked.
    ///   • <c>false</c> → no row is checked.
    ///   • <c>null</c>  → some-but-not-all rows are checked
    ///                    (indeterminate visual).
    /// Setter is invoked when the user clicks the header checkbox;
    /// it propagates the new value to every row's
    /// <see cref="VaultItemRow.IsSelected"/>. <see cref="_suppressBubble"/>
    /// guards against the per-row PropertyChanged handlers re-entering
    /// IsAllSelected midway through the bulk-set loop, which would
    /// otherwise keep flipping the value to indeterminate as each
    /// row updated.
    /// </summary>
    private bool _suppressBubble;
    public bool? IsAllSelected
    {
        get
        {
            if (Items.Count == 0) return false;
            if (SelectedCount == 0) return false;
            if (SelectedCount == Items.Count) return true;
            return null;
        }
        set
        {
            // Indeterminate clicks come from the user cycling through
            // the third state; treat that as "select none" for ergonomics.
            var target = value == true;
            _suppressBubble = true;
            try
            {
                foreach (var r in Items) r.IsSelected = target;
            }
            finally { _suppressBubble = false; }
            SelectedCount = Items.Count(i => i.IsSelected);
            OnPropertyChanged(nameof(IsAllSelected));
        }
    }

    /// <summary>
    /// Phase 71i — full unfiltered snapshot from the most recent
    /// <see cref="ReloadAsync"/>. <see cref="ApplyTagFilter"/> rebuilds
    /// <see cref="Items"/> from this list when the user changes
    /// <see cref="TagFilter"/>, avoiding a DB round-trip.
    /// </summary>
    private List<VaultItemRow> _allItems = new();

    public override async Task OnNavigatedToAsync()
    {
        await _vault.RefreshStateAsync();
        IsInitialized = _vault.IsInitialized;
        IsUnlocked    = _vault.IsUnlocked;
        // Phase 26 — load auto-lock setting. We read it on every page
        // visit so an out-of-band update (e.g. another instance) takes
        // effect on next render.
        try { AutoLockMinutes = await _vault.GetAutoLockMinutesAsync(); }
        catch { /* leave default */ }
        await ReloadAsync();
    }

    public override Task OnNavigatedFromAsync()
    {
        // Stop the per-second timer when leaving the page so it doesn't
        // tick against an off-screen VM (matches the SchedulerViewModel
        // pattern that motivated NavigationService.OnNavigatedFromAsync
        // in the first place).
        _totpTimer.Stop();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            await _vault.RefreshStateAsync();
            IsInitialized = _vault.IsInitialized;
            IsUnlocked    = _vault.IsUnlocked;
            // Phase 71c audit fix — explicitly unsubscribe each row's
            // PropertyChanged handler before clearing. Without this,
            // every reload installed a fresh closure capturing the row
            // + Items collection without releasing the previous one,
            // leaking handler delegates across reloads. We track the
            // installed delegates in _rowHandlers so we can detach
            // them precisely.
            foreach (var (row, handler) in _rowHandlers)
                row.PropertyChanged -= handler;
            _rowHandlers.Clear();
            Items.Clear();
            if (!IsUnlocked) { IsEmpty = true; _totpTimer.Stop(); return; }
            // Phase 71i — kind filter dropped (everything is universal).
            // Pull all matching rows; tag filter is applied in-memory by
            // ApplyTagFilter() after we build the row VMs.
            var rows = await _vault.ListAsync(
                kind:    null,
                search:  string.IsNullOrWhiteSpace(SearchText) ? null : SearchText);
            // Phase 71 — reset selection-count snapshot. ReloadAsync
            // wipes Items, so the count goes to 0 and stays there
            // until the user re-checks rows in the rebuilt grid.
            SelectedCount = 0;
            _allItems = new List<VaultItemRow>(rows.Count);
            foreach (var r in rows)
            {
                var row = new VaultItemRow(r);
                // Phase 71 — bubble per-row IsSelected changes into
                // the page-level SelectedCount so the bulk-delete
                // button toggles visibility correctly. Track the
                // delegate in _rowHandlers so the next ReloadAsync
                // can detach it cleanly (Phase 71c audit fix).
                System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
                {
                    // Phase 71d — skip the recount during bulk select-all
                    // (the IsAllSelected setter does the count once after
                    // the loop). Saves O(N²) work when toggling 500 rows.
                    if (_suppressBubble) return;
                    if (args.PropertyName == nameof(VaultItemRow.IsSelected))
                        SelectedCount = Items.Count(i => i.IsSelected);
                };
                row.PropertyChanged += handler;
                _rowHandlers.Add((row, handler));
                // For TOTP-capable kinds, fetch + cache the secret seed
                // so the per-second timer can compute codes locally
                // without hitting the DB on every tick.
                if (string.Equals(r.Kind, "totp_only", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var pair = await _vault.GetClearAsync(r.Id);
                        if (pair is not null && pair.Value.clear.TryGetValue("totp_secret", out var seed))
                            row.SetTotpSecret(seed);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex,
                            "Couldn't load TOTP secret for vault row #{Id}", r.Id);
                    }
                }
                row.RefreshTotp();
                _allItems.Add(row);
            }
            // Phase 71i — rebuild the tag filter dropdown + apply current
            // selection. ApplyTagFilter populates Items from _allItems
            // honouring TagFilter (defaults to "All" on first load).
            RebuildTagFilters();
            ApplyTagFilter();
            IsEmpty = Items.Count == 0;
            // Restart the timer only if there is at least one TOTP row
            // — otherwise we'd burn a Dispatcher tick every second for
            // nothing on plain credential vaults.
            if (Items.Any(r => r.IsTotp)) _totpTimer.Start();
            else                          _totpTimer.Stop();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Vault reload failed");
            await _dialogs.ConfirmAsync("Vault load failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
        finally { IsBusy = false; }
    }

    partial void OnSearchTextChanged(string? value) => _ = ReloadAsync();
    // Phase 71i — kind filter removed; OnTagFilterChanged is declared
    // alongside the [ObservableProperty] above and triggers an
    // in-memory re-filter via ApplyTagFilter (no DB round-trip needed).

    [RelayCommand]
    private async Task UnlockAsync()
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var dlg = new VaultUnlockDialog(_vault) { Owner = owner };
        if (dlg.ShowDialog() == true && dlg.Success)
            await ReloadAsync();
    }

    [RelayCommand]
    private void Lock()
    {
        _vault.Lock();
        Items.Clear();
        IsUnlocked = false;
        IsEmpty = true;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!IsUnlocked) { await UnlockAsync(); if (!IsUnlocked) return; }
        var owner = System.Windows.Application.Current?.MainWindow;
        var profiles = await _profiles.ListAsync();
        var dlg = new VaultItemEditorDialog(
            _vault, existing: null, existingClear: null, profiles)
            { Owner = owner };
        if (dlg.ShowDialog() == true)
            await ReloadAsync();
    }

    /// <summary>
    /// Phase 69 — open the bulk-import dialog. Lets the user paste CSV
    /// (or fetch a Google Sheet) and create N vault items in one pass,
    /// auto-binding each to the profile named in its <c>profile_name</c>
    /// column. After commit we reload so the new rows show up + surface
    /// a count toast so the user sees how many landed.
    /// </summary>
    [RelayCommand]
    private async Task BulkImportAsync()
    {
        if (!IsUnlocked) { await UnlockAsync(); if (!IsUnlocked) return; }
        var owner = System.Windows.Application.Current?.MainWindow;
        var profiles = await _profiles.ListAsync();
        var dlg = new VaultBulkImportDialog(_vault, profiles, _profiles) { Owner = owner };
        if (dlg.ShowDialog() == true && dlg.CreatedCount > 0)
        {
            // Phase 70 — also mention auto-created profiles when present
            // so the user knows new rows landed on the Profiles page.
            var profileSuffix = dlg.CreatedProfilesCount > 0
                ? $" + {dlg.CreatedProfilesCount} new profile(s) auto-created"
                : "";
            await _dialogs.ConfirmAsync(
                "Bulk import done",
                $"Created {dlg.CreatedCount} vault item(s){profileSuffix}. " +
                "They'll resolve through {{vault.SEED}}, {{vault.PASSWORD}}, " +
                "{{vault.<custom_field>}} etc. for the profiles they're bound to.",
                "OK", ConfirmSeverity.Success);
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task EditAsync(VaultItemRow? row)
    {
        if (row is null) return;
        var item = row.Item;
        if (!IsUnlocked) { await UnlockAsync(); if (!IsUnlocked) return; }
        var owner = System.Windows.Application.Current?.MainWindow;
        IReadOnlyDictionary<string, string>? clear = null;
        try
        {
            var pair = await _vault.GetClearAsync(item.Id);
            clear = pair?.clear;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't decrypt vault item #{Id}", item.Id);
            await _dialogs.ConfirmAsync(
                "Decrypt failed",
                "Couldn't decrypt this item — the master key may have changed since it was created. " + ex.Message,
                "OK", ConfirmSeverity.Error);
            return;
        }
        var profiles = await _profiles.ListAsync();
        var dlg = new VaultItemEditorDialog(_vault, item, clear, profiles) { Owner = owner };
        if (dlg.ShowDialog() == true)
            await ReloadAsync();
    }

    /// <summary>
    /// Phase 71i — recompute the distinct tag list from the latest
    /// snapshot. Preserves the user's current selection when possible
    /// (so re-selecting "twitter" survives a reload as long as
    /// at least one item still carries that tag); otherwise falls
    /// back to "All".
    /// </summary>
    private void RebuildTagFilters()
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _allItems)
        {
            if (string.IsNullOrWhiteSpace(r.Tags)) continue;
            foreach (var t in r.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrEmpty(t)) tags.Add(t);
            }
        }

        var prev = TagFilter;
        TagFilters.Clear();
        TagFilters.Add("All");
        foreach (var t in tags.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            TagFilters.Add(t);

        // Restore prior selection if still available; otherwise reset
        // to "All" so the dropdown isn't stuck on an invisible value.
        if (!string.IsNullOrEmpty(prev) && TagFilters.Contains(prev))
            TagFilter = prev;
        else
            TagFilter = "All";
    }

    /// <summary>
    /// Phase 71i — rebuild <see cref="Items"/> from <see cref="_allItems"/>
    /// honouring the current <see cref="TagFilter"/>. "All" passes
    /// every row; a specific tag matches when the row's
    /// <see cref="VaultItemRow.Tags"/> string contains that tag as a
    /// comma-separated entry (case-insensitive).
    /// </summary>
    private void ApplyTagFilter()
    {
        Items.Clear();
        var sel = TagFilter;
        var allTags = string.IsNullOrEmpty(sel) || string.Equals(sel, "All", StringComparison.OrdinalIgnoreCase);
        foreach (var r in _allItems)
        {
            if (allTags) { Items.Add(r); continue; }
            var tagSet = (r.Tags ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tagSet.Any(t => string.Equals(t, sel, StringComparison.OrdinalIgnoreCase)))
                Items.Add(r);
        }
        IsEmpty = Items.Count == 0;
        SelectedCount = Items.Count(i => i.IsSelected);
    }

    [RelayCommand]
    private async Task DeleteAsync(VaultItemRow? row)
    {
        if (row is null) return;
        var item = row.Item;
        var ok = await _dialogs.ConfirmAsync(
            "Delete vault item",
            $"Permanently delete '{item.Name}' ({item.Kind})? Cannot be undone.",
            "Delete", ConfirmSeverity.Warning);
        if (!ok) return;
        await _vault.DeleteAsync(item.Id);
        await ReloadAsync();
    }

    /// <summary>
    /// Phase 71 — bulk delete every row whose <see cref="VaultItemRow.IsSelected"/>
    /// is true. Mirrors the proxies grid's "Delete N selected" UX:
    /// red button visible only when at least one row is checked,
    /// confirms with the count, deletes serially through
    /// <see cref="IVaultService.DeleteAsync"/>.
    /// </summary>
    [RelayCommand]
    private async Task BulkDeleteAsync()
    {
        var picked = Items.Where(r => r.IsSelected).ToList();
        if (picked.Count == 0) return;

        var ok = await _dialogs.ConfirmAsync(
            "Delete selected vault items",
            $"Permanently delete {picked.Count} vault item(s)? This cannot be undone.\n\n" +
            string.Join("\n", picked.Take(5).Select(r => "  · " + r.Name))
            + (picked.Count > 5 ? $"\n  · …and {picked.Count - 5} more" : ""),
            "Delete", ConfirmSeverity.Warning);
        if (!ok) return;

        foreach (var r in picked)
        {
            try { await _vault.DeleteAsync(r.Item.Id); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Bulk-delete vault item #{Id} failed", r.Item.Id);
            }
        }
        await ReloadAsync();
    }

    /// <summary>Phase 26 — open the rotate-passphrase modal. The dialog
    /// verifies the current passphrase, derives a new key, and re-encrypts
    /// every item in one transaction. We reload afterwards so the
    /// updated_at timestamps in the grid reflect the rotation.</summary>
    [RelayCommand]
    private async Task ChangeMasterPasswordAsync()
    {
        if (!IsUnlocked) { await UnlockAsync(); if (!IsUnlocked) return; }
        var owner = System.Windows.Application.Current?.MainWindow;
        var dlg = new VaultChangePasswordDialog(_vault) { Owner = owner };
        if (dlg.ShowDialog() == true && dlg.Success)
        {
            await _dialogs.ConfirmAsync(
                "Passphrase rotated",
                "All items have been re-encrypted under the new passphrase. " +
                "Make sure your password manager is updated.",
                "OK", ConfirmSeverity.Success);
            await ReloadAsync();
        }
    }

    // Phase 26 — auto-lock minutes is bound TwoWay; persist on every
    // change. Source generator emits OnAutoLockMinutesChanged for us.
    partial void OnAutoLockMinutesChanged(int value)
    {
        _ = PersistAutoLockAsync(value);
    }

    private async Task PersistAutoLockAsync(int minutes)
    {
        try { await _vault.SetAutoLockMinutesAsync(minutes); }
        catch (Exception ex) { _log.LogWarning(ex, "Couldn't persist auto-lock setting"); }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        var first = await _dialogs.ConfirmAsync(
            "Reset vault?",
            "This wipes EVERY vault item AND the master passphrase. There is no undo. Are you sure?",
            "Yes, wipe", ConfirmSeverity.Error);
        if (!first) return;

        // Phase 26 — we now expose VerifiedPassphrase from the unlock
        // dialog, so we can feed it straight into ResetAsync. The service
        // verifies it again under the gate (defence in depth — handles
        // the case where the user dismissed an auto-lock between the
        // dialog and this call).
        var owner = System.Windows.Application.Current?.MainWindow;
        var pwDlg = new VaultUnlockDialog(_vault) { Owner = owner };
        if (pwDlg.ShowDialog() != true || !pwDlg.Success || pwDlg.VerifiedPassphrase is null) return;

        try
        {
            await _vault.ResetAsync(pwDlg.VerifiedPassphrase);
            await _dialogs.ConfirmAsync(
                "Vault wiped",
                "Every credential and the master passphrase have been removed. " +
                "Use Unlock → First-time setup to start fresh.",
                "OK", ConfirmSeverity.Info);
            await ReloadAsync();
        }
        catch (UnauthorizedAccessException)
        {
            await _dialogs.ConfirmAsync(
                "Reset failed",
                "The current passphrase didn't match. The vault was not modified.",
                "OK", ConfirmSeverity.Error);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Vault reset failed");
            await _dialogs.ConfirmAsync("Reset failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    private void OnVaultLockStateChanged(object? sender, EventArgs e)
    {
        // Marshal to UI thread — the event may fire from a worker.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        // Phase 26 audit fix — surface async-lambda failures via the
        // logger instead of the swallowing fire-and-forget Action ctor.
        // Without this an exception during ReloadAsync (e.g. a transient
        // SQLite lock) would leave the page stuck on stale state with
        // no breadcrumb in the log.
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                IsUnlocked = _vault.IsUnlocked;
                IsInitialized = _vault.IsInitialized;
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "VaultViewModel.OnVaultLockStateChanged reload failed");
            }
        }));
    }
}
