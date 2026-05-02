// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// One credential vault entry. Metadata fields are stored plaintext,
/// secrets are stored as an encrypted blob (<see cref="SecretsEnc"/>).
/// The vault service decrypts <see cref="SecretsEnc"/> only when the
/// master password is unlocked and only when the caller asks for it.
///
/// Phase 24 — mirrors the legacy web schema 1:1 so a future migration
/// tool can move rows either way. Each <see cref="Kind"/> determines
/// which fields the secrets JSON expects (see <see cref="VaultKinds"/>).
/// </summary>
public sealed record VaultItem
{
    public long Id { get; init; }

    /// <summary>Human-readable label, e.g. "GitHub — main account".</summary>
    public required string Name { get; init; }

    /// <summary>One of <see cref="VaultKinds.All"/>.</summary>
    public string Kind { get; init; } = "account";

    /// <summary>Optional service slug — google / github / aws / binance / etc.</summary>
    public string? Service { get; init; }

    /// <summary>Non-secret human-readable identifier (email, address,
    /// client_id). Lives outside <see cref="SecretsEnc"/> so it can
    /// be searched/filtered without unlocking the vault.
    ///
    /// IMPORTANT: this is NOT necessarily the same as the encrypted
    /// "username" field for kind="account". Identifier is the
    /// SEARCHABLE label (and may be exposed in UI lists / DB
    /// backups); secrets["username"] is the actual encrypted login.
    /// Many entries set both to the same value; some don't (e.g. an
    /// API key entry might use a vendor name as Identifier and a
    /// distinct client_id inside secrets).</summary>
    public string? Identifier { get; init; }

    /// <summary>Encrypted JSON blob — kind-specific secret fields.
    /// AES-GCM ciphertext with the format:
    ///   [12-byte nonce][N bytes ciphertext][16-byte auth tag]
    /// Decrypted only by <c>IVaultService.GetClearAsync</c>.</summary>
    public byte[]? SecretsEnc { get; init; }

    /// <summary>Optional profile binding — when set, the entry is
    /// scoped to one profile. Useful for multi-account setups.</summary>
    public string? ProfileName { get; init; }

    /// <summary>active | banned | locked | needs_review | disabled.</summary>
    public string Status { get; init; } = "active";

    /// <summary>JSON array of free-form tags ("work", "client-acme", …).</summary>
    public string? TagsJson { get; init; }

    /// <summary>Free-form note shown alongside the entry.</summary>
    public string? Notes { get; init; }

    /// <summary>Timestamp of the most recent script-driven read.</summary>
    public DateTime? LastUsedAt { get; init; }

    /// <summary>Timestamp of the most recent successful login flow.</summary>
    public DateTime? LastLoginAt { get; init; }

    /// <summary>ok | failed | captcha | 2fa_required.</summary>
    public string? LastLoginStatus { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// Vault kinds (mirrors web). Each kind has a fixed set of field keys
/// the secrets JSON must contain; UI uses this catalog to render the
/// correct form, and the script placeholder resolver uses it to know
/// which fields are gettable via <c>{{vault.id.field}}</c>.
/// </summary>
public static class VaultKinds
{
    public sealed record KindSpec(string Id, string Label, string[] Fields, string Icon);

    public static IReadOnlyList<string> All => new[]
    {
        "account", "email", "social", "crypto_wallet",
        "api_key", "totp_only", "note", "custom",
    };

    /// <summary>Allowed values for <see cref="VaultItem.Status"/>. The
    /// vault service rejects unknown statuses on create/update.</summary>
    public static IReadOnlyList<string> Statuses => new[]
    {
        "active", "banned", "locked", "needs_review", "disabled",
    };

    public static bool IsValidKind(string? kind)
        => kind is not null && All.Contains(kind, StringComparer.Ordinal);

    public static bool IsValidStatus(string? status)
        => status is not null && Statuses.Contains(status, StringComparer.Ordinal);

    public static IReadOnlyList<KindSpec> Catalog { get; } = new[]
    {
        new KindSpec("account",       "Account / Login", new[] {
            "username", "password", "totp_secret", "recovery"
        }, "🔐"),
        new KindSpec("email",         "Email account",   new[] {
            "username", "password", "imap_host", "smtp_host", "totp_secret"
        }, "📧"),
        new KindSpec("social",        "Social media",    new[] {
            "username", "password", "totp_secret", "session_cookie"
        }, "💬"),
        new KindSpec("crypto_wallet", "Crypto wallet",   new[] {
            "address", "seed_phrase", "wallet_password", "private_key", "derivation_path"
        }, "🪙"),
        new KindSpec("api_key",       "API key",         new[] {
            "key", "secret", "region"
        }, "🔑"),
        new KindSpec("totp_only",     "TOTP only",       new[] {
            "totp_secret"
        }, "⏱"),
        new KindSpec("note",          "Secure note",     new[] {
            "body"
        }, "📝"),
        new KindSpec("custom",        "Custom",          Array.Empty<string>(), "🧩"),
    };

    public static KindSpec? Get(string id)
    {
        foreach (var k in Catalog)
            if (string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase))
                return k;
        return null;
    }
}
