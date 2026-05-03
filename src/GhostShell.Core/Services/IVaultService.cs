// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 24 — credential vault facade.
///
/// Lifecycle:
///   • <see cref="IsInitialized"/> — first time setup needed?
///   • <see cref="IsUnlocked"/>    — master key is in memory?
///   • <see cref="InitializeAsync"/> — set master passphrase the first time.
///   • <see cref="UnlockAsync"/>     — derive key + verify on subsequent opens.
///   • <see cref="Lock"/>            — wipe in-memory key.
///
/// CRUD ops require the vault to be unlocked. Read of a single item's
/// secrets goes through <see cref="GetClearAsync"/> which decrypts the
/// blob; the regular <see cref="ListAsync"/> / <see cref="GetAsync"/>
/// path returns metadata + ciphertext only.
///
/// Script integration calls <see cref="ResolveAsync"/> to translate
/// {(vault, id, field)} placeholder references into clear values for
/// a single run.
/// </summary>
public interface IVaultService
{
    bool IsInitialized { get; }
    bool IsUnlocked    { get; }

    /// <summary>Fired whenever lock/unlock state flips so VMs can
    /// refresh "vault locked" badges in the UI.
    ///
    /// IMPORTANT: subscribers MUST NOT call back into the vault
    /// synchronously from this handler — the implementation fires
    /// the event after releasing its internal gate, but a re-entrant
    /// call from a subscriber can still create races. Marshal to the
    /// UI thread via Dispatcher.BeginInvoke if you need to refresh
    /// view-model state, then perform vault calls async.</summary>
    event EventHandler? LockStateChanged;

    Task<bool> RefreshStateAsync(CancellationToken ct = default);

    /// <summary>Set the master passphrase the first time. Throws if
    /// vault already initialized.</summary>
    Task InitializeAsync(string masterPassphrase, CancellationToken ct = default);

    /// <summary>Verify the passphrase against the stored verifier.
    /// Returns false on wrong password (no exception); throws only on
    /// schema/IO errors.</summary>
    Task<bool> UnlockAsync(string masterPassphrase, CancellationToken ct = default);

    /// <summary>Wipe the derived key from memory. Idempotent.</summary>
    void Lock();

    /// <summary>DESTRUCTIVE — drops every vault_items row + clears
    /// vault_config. Returns silently if not initialized.</summary>
    Task ResetAsync(string currentPassphrase, CancellationToken ct = default);

    Task<IReadOnlyList<VaultItem>> ListAsync(
        string? kind = null, string? service = null, string? status = null,
        string? profileName = null, string? search = null,
        CancellationToken ct = default);

    Task<VaultItem?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Get + decrypt secrets. Returns null on missing item.
    /// Throws if vault is locked.</summary>
    Task<(VaultItem item, IReadOnlyDictionary<string, string> clear)?>
        GetClearAsync(long id, CancellationToken ct = default);

    /// <summary>Insert. Encrypts <paramref name="clearSecrets"/> under
    /// the in-memory master key. Throws if locked.</summary>
    Task<VaultItem> CreateAsync(
        VaultItem item,
        IReadOnlyDictionary<string, string> clearSecrets,
        CancellationToken ct = default);

    /// <summary>Update metadata + (optionally) re-encrypt secrets.
    /// Pass <paramref name="clearSecrets"/> = null to keep the
    /// existing ciphertext untouched.</summary>
    Task UpdateAsync(
        VaultItem item,
        IReadOnlyDictionary<string, string>? clearSecrets,
        CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    Task TouchUsedAsync(long id, CancellationToken ct = default);

    /// <summary>Resolve a set of (vault-id, field) references to clear
    /// values for one script run. Failed lookups (missing item / locked
    /// / bad field) drop silently — caller's interpolation falls back
    /// to leaving the placeholder verbatim. Returns a nested dict:
    /// <c>{ "12": { "username": "...", "password": "..." } }</c>.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>
        ResolveAsync(IEnumerable<(long Id, string Field)> refs, CancellationToken ct = default);

    /// <summary>
    /// Phase 69 — resolve profile-scoped aliases (<c>{{vault.SEED}}</c>,
    /// <c>{{vault.PRIVKEY}}</c>, etc) to their cleartext values for one
    /// run. For each alias, walks <see cref="VaultAliases.All"/> to find
    /// the (kind, field) pair, then ListAsync(profileName, kind) to find
    /// the bound vault item, decrypts secrets, and returns the field.
    /// Failed lookups (no bound item, vault locked, missing field) are
    /// dropped silently — the runner's interpolation falls back to
    /// leaving the placeholder verbatim. Returns alias→cleartext.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAliasesAsync(
        string profileName,
        IEnumerable<string> aliases,
        CancellationToken ct = default);

    // ─── Phase 26 — auto-lock + master-password rotation ────────────

    /// <summary>How long the vault stays unlocked after the last user
    /// activity, in minutes. 0 = disabled. Default 15. Stored in
    /// <c>vault_config</c> so it survives app restarts.</summary>
    Task<int> GetAutoLockMinutesAsync(CancellationToken ct = default);

    /// <summary>Persist the auto-lock idle timeout. <paramref name="minutes"/>
    /// is clamped to [0, 24*60]. Pass 0 to disable auto-lock.</summary>
    Task SetAutoLockMinutesAsync(int minutes, CancellationToken ct = default);

    /// <summary>UTC tick of the last user activity that should reset the
    /// idle countdown. Bumped by <see cref="NotifyActivity"/> and by every
    /// successful CRUD call on the vault itself.</summary>
    DateTime LastActivityUtc { get; }

    /// <summary>Tell the vault that the user just did something (mouse,
    /// key, vault op). Cheap — just stamps <see cref="LastActivityUtc"/>.</summary>
    void NotifyActivity();

    /// <summary>Re-encrypt every vault item under a new master passphrase.
    /// Verifies <paramref name="oldPassphrase"/> first; throws
    /// <see cref="UnauthorizedAccessException"/> on mismatch. The whole
    /// rotation runs inside one DB transaction so a partial failure
    /// can't leave the vault in a half-rotated state.</summary>
    Task ChangeMasterPasswordAsync(
        string oldPassphrase,
        string newPassphrase,
        CancellationToken ct = default);
}
