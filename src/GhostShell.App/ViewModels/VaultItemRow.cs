// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using CommunityToolkit.Mvvm.ComponentModel;
using GhostShell.Core.Models;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 26 — wrapper around <see cref="VaultItem"/> that exposes
/// observable TOTP fields. The vault page's DataGrid binds to instances
/// of this VM rather than the bare model so we can repaint the TOTP
/// code + countdown every second without rebuilding the row.
///
/// All metadata fields are pass-through accessors over the underlying
/// item record. The TOTP fields stay null/blank for non-TOTP rows.
/// </summary>
public sealed partial class VaultItemRow : ObservableObject
{
    public VaultItem Item { get; private set; }

    public VaultItemRow(VaultItem item) { Item = item; }

    public long Id          => Item.Id;
    public string Name      => Item.Name;
    public string Kind      => Item.Kind;
    public string? Service  => Item.Service;
    public string? Identifier => Item.Identifier;
    public string? ProfileName => Item.ProfileName;
    public string Status    => Item.Status;
    public DateTime UpdatedAt => Item.UpdatedAt;

    /// <summary>True when this row's kind is TOTP-only OR the row's
    /// secrets contain a totp_secret field. The page surfaces a live
    /// 6-digit code in the TOTP column for these.</summary>
    [ObservableProperty] private bool _isTotp;

    /// <summary>Live 6-digit TOTP code, repainted by the page-level
    /// timer. Empty when not applicable or when the vault is locked.</summary>
    [ObservableProperty] private string _totpCode = "";

    /// <summary>Seconds remaining in the current 30-second TOTP window.</summary>
    [ObservableProperty] private int _totpRemaining;

    /// <summary>Stable secret seed (base32) used to compute the code.
    /// Held only while the vault is unlocked; cleared on lock.</summary>
    private string? _totpSecret;

    public void SetTotpSecret(string? base32Seed)
    {
        _totpSecret = string.IsNullOrWhiteSpace(base32Seed) ? null : base32Seed.Trim();
        IsTotp = _totpSecret is not null;
        if (!IsTotp)
        {
            TotpCode = "";
            TotpRemaining = 0;
        }
    }

    /// <summary>Refresh <see cref="TotpCode"/> + <see cref="TotpRemaining"/>
    /// from the current UTC clock. No-ops when not a TOTP row.</summary>
    public void RefreshTotp()
    {
        if (_totpSecret is null) return;
        try
        {
            var (code, remaining) = GhostShell.Core.Vault.Totp.Compute(_totpSecret);
            // Only fire change notifications when the value actually
            // changed — the DataGrid's text binding doesn't repaint on
            // identical assignments, but skipping the property setter
            // shaves overhead on rows with hundreds of refreshes/min.
            if (TotpCode != code) TotpCode = code;
            if (TotpRemaining != remaining) TotpRemaining = remaining;
        }
        catch
        {
            // Bad seed (couldn't base32-decode). Hide the column data.
            if (TotpCode != "") TotpCode = "";
            if (TotpRemaining != 0) TotpRemaining = 0;
        }
    }

    public void Replace(VaultItem item) { Item = item; OnPropertyChanged(string.Empty); }
}
