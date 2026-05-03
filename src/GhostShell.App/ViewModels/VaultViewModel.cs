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

    [ObservableProperty] private bool _isInitialized;
    [ObservableProperty] private bool _isUnlocked;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private string  _kindFilter = "All";
    [ObservableProperty] private bool    _isEmpty = true;
    [ObservableProperty] private int     _autoLockMinutes = 15;

    /// <summary>"All" + every kind — drives the kind filter dropdown.</summary>
    public IReadOnlyList<string> KindFilters
        => new[] { "All" }.Concat(VaultKinds.All).ToList();

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
            Items.Clear();
            if (!IsUnlocked) { IsEmpty = true; _totpTimer.Stop(); return; }
            var rows = await _vault.ListAsync(
                kind:    KindFilter == "All" ? null : KindFilter,
                search:  string.IsNullOrWhiteSpace(SearchText) ? null : SearchText);
            foreach (var r in rows)
            {
                var row = new VaultItemRow(r);
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
                Items.Add(row);
            }
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
    partial void OnKindFilterChanged(string value) => _ = ReloadAsync();

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
        var dlg = new VaultBulkImportDialog(_vault, profiles) { Owner = owner };
        if (dlg.ShowDialog() == true && dlg.CreatedCount > 0)
        {
            await _dialogs.ConfirmAsync(
                "Bulk import done",
                $"Created {dlg.CreatedCount} vault item(s). They'll resolve through " +
                "{{vault.SEED}}, {{vault.PASSWORD}} etc. for the profiles they're bound to.",
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
