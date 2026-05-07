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
    public string? Email    => Item.Email;
    public string? ProfileName => Item.ProfileName;
    public string Status    => Item.Status;
    public DateTime UpdatedAt => Item.UpdatedAt;

    /// <summary>
    /// Phase 71 — decoded tags column for the Vault grid. The
    /// underlying <see cref="VaultItem.TagsJson"/> is a JSON array
    /// (["work", "client-acme"]); the grid wants a friendly
    /// "work, client-acme" string. Returns empty string when no
    /// tags so the cell renders blank instead of "[]".
    /// </summary>
    public string Tags
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Item.TagsJson)) return "";
            try
            {
                var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(Item.TagsJson);
                if (arr is null || arr.Length == 0) return "";
                return string.Join(", ", arr);
            }
            catch
            {
                // Corrupt JSON — surface raw payload so the user can
                // see + fix it rather than getting silent blank.
                return Item.TagsJson!;
            }
        }
    }

    /// <summary>
    /// Phase 71 — selection flag for multiselect-delete on the Vault
    /// page. Mirrors the proxies grid's checkbox column. Bound TwoWay
    /// to a checkbox in the row template; the page-level "Delete N
    /// selected" command counts <see cref="IsSelected"/>=true rows.
    /// </summary>
    [ObservableProperty] private bool _isSelected;

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
