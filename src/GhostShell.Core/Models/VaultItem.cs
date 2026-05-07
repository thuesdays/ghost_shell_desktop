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

    // ─── Phase 71 — Unified vault model ────────────────────────────
    // The legacy kind-aware schema (account/social/crypto_wallet/…) is
    // collapsed into a universal item that can hold arbitrary fields.
    // The three columns below add:
    //   • a top-level searchable Email (plaintext, like Identifier)
    //   • a per-field metadata JSON (encrypted? is_totp?)
    //   • a plaintext extras JSON for fields the user explicitly
    //     marked as non-encrypted (rare)
    // Existing items keep working — these columns simply default to
    // null and the runtime falls back to "encrypted=true, is_totp=false"
    // when meta is missing for a key.

    /// <summary>
    /// Phase 71 — searchable email column. Renders as a top-level
    /// field in the universal editor. Stored plaintext like
    /// <see cref="Identifier"/> so the user can filter the vault list
    /// by email without unlocking. The matching <c>email_password</c>
    /// secret lives in <see cref="SecretsEnc"/>.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Phase 71 — per-field metadata, JSON object of shape:
    /// <c>{"discord_token": {"encrypted": true, "is_totp": false}, …}</c>.
    /// Defaults when key absent: encrypted=true, is_totp=false.
    /// Fields marked <c>encrypted=false</c> are stored plaintext in
    /// <see cref="ExtrasJson"/> instead of <see cref="SecretsEnc"/>.
    /// Fields marked <c>is_totp=true</c> are returned as live OTP
    /// codes (computed via <c>Totp.Compute</c>) at
    /// <c>{{vault.X}}</c> resolution time, never as raw seeds.
    /// </summary>
    public string? FieldMetaJson { get; init; }

    /// <summary>
    /// Phase 71 — plaintext custom-field values. JSON object of shape
    /// <c>{"device_id": "abc-123", "user_agent_hint": "Chrome/120"}</c>.
    /// Encrypted custom fields stay in <see cref="SecretsEnc"/>; this
    /// column holds only the explicit-plaintext ones. Optional;
    /// default-encrypted custom fields produce no row here.
    /// </summary>
    public string? ExtrasJson { get; init; }

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
        // Phase 71 — "universal" is the new default. All legacy kinds
        // remain valid so pre-existing items keep working; new items
        // created via the editor / bulk import default to universal.
        "universal",
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
        // Phase 71 — universal: no fixed-schema secrets. The editor
        // renders Name / Email / Email Password / Profile / Status /
        // Tags / Notes plus any custom fields the user adds via
        // "+ Add custom field". email_password is the only baked-in
        // secret; everything else is dynamic.
        new KindSpec("universal",     "Universal",       new[] {
            "email_password"
        }, "🗝"),
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

/// <summary>
/// Phase 69 — profile-scoped vault aliases. Lets scripts reference a
/// profile's bound credential without hardcoding numeric vault IDs:
///   {{vault.SEED}}    → profile's crypto_wallet.seed_phrase
///   {{vault.PRIVKEY}} → profile's crypto_wallet.private_key
///   {{vault.PASS}}    → profile's crypto_wallet.wallet_password
///   {{vault.USERNAME}}→ profile's account.username
///   {{vault.PASSWORD}}→ profile's account.password
///   {{vault.TOTP}}    → first item with a totp_secret field
/// At resolution time, the runner finds the vault item where
/// <see cref="VaultItem.ProfileName"/> matches the running profile +
/// <see cref="VaultItem.Kind"/> matches the alias's expected kind,
/// decrypts secrets, and returns the alias's mapped field. Lets a
/// single script run unchanged across 100 profiles, each pulling its
/// own seed phrase / login from its bound vault entry.
/// </summary>
public static class VaultAliases
{
    public sealed record AliasSpec(string Alias, string Kind, string Field);

    /// <summary>The full alias catalog. Add entries here when a new
    /// profile-bound credential type emerges.</summary>
    public static readonly IReadOnlyList<AliasSpec> All = new[]
    {
        // Crypto wallet — most common bulk-import case.
        new AliasSpec("SEED",      "crypto_wallet", "seed_phrase"),
        new AliasSpec("PRIVKEY",   "crypto_wallet", "private_key"),
        new AliasSpec("PASS",      "crypto_wallet", "wallet_password"),
        new AliasSpec("ADDR",      "crypto_wallet", "address"),
        new AliasSpec("DERIV",     "crypto_wallet", "derivation_path"),
        // Account — username/password login flows.
        new AliasSpec("USERNAME",  "account",       "username"),
        new AliasSpec("PASSWORD",  "account",       "password"),
        // Email-specific (gmail/outlook/yahoo).
        new AliasSpec("EMAIL",     "email",         "username"),
        new AliasSpec("EMAILPASS", "email",         "password"),
        // 2FA — kind="" means any kind that has a totp_secret field.
        new AliasSpec("TOTP",      "",              "totp_secret"),
        // Social media — username/password + optional session cookie.
        new AliasSpec("SOCIAL",    "social",        "username"),
        new AliasSpec("SOCIALPASS","social",        "password"),
    };

    private static readonly Dictionary<string, AliasSpec> _byAlias =
        All.ToDictionary(a => a.Alias, a => a, StringComparer.OrdinalIgnoreCase);

    public static AliasSpec? Get(string alias)
        => _byAlias.TryGetValue(alias ?? "", out var spec) ? spec : null;

    public static bool IsKnown(string alias) => _byAlias.ContainsKey(alias ?? "");
}

/// <summary>
/// Phase 71 — per-field metadata for a vault item's custom keys. The
/// JSON column <c>vault_items.field_meta_json</c> serializes a
/// dictionary of <c>field_name → VaultFieldMeta</c>.
///
/// Two flags drive runtime behaviour:
///   • <see cref="Encrypted"/> — when true (default), the value lives
///     in the encrypted secrets blob and is redacted in script logs.
///     When false, the value lives plaintext in
///     <see cref="VaultItem.ExtrasJson"/> and is logged verbatim
///     (think: tags, hints, public IDs).
///   • <see cref="IsTotp"/> — when true, the stored value is a TOTP
///     seed (Base32). At <c>{{vault.X}}</c> resolution time the
///     runtime calls <c>Totp.Compute</c> on it and returns the live
///     6-digit code, not the raw seed. Implies <c>Encrypted=true</c>.
/// </summary>
public sealed record VaultFieldMeta
{
    public bool Encrypted { get; init; } = true;
    public bool IsTotp    { get; init; } = false;

    /// <summary>Default for fields with no explicit metadata. Errs on
    /// the side of secrecy — we'd rather over-encrypt a non-secret
    /// than accidentally expose a token.</summary>
    public static VaultFieldMeta Default { get; } = new() { Encrypted = true, IsTotp = false };
}

/// <summary>
/// Phase 71 — the universal vault item's "official" plaintext metadata
/// + always-encrypted secrets. All other custom fields go through
/// <see cref="VaultFieldMeta"/> at insert time. This list is the source
/// of truth for the editor's default rendering and for the bulk-import
/// dropdown's "core" choices that shouldn't be marked as custom.
/// </summary>
public static class VaultUniversal
{
    /// <summary>Plaintext metadata fields (stored as their own DB
    /// columns or in the searchable index). Never encrypted.</summary>
    public static IReadOnlyList<string> PlaintextFields => new[]
    {
        "name", "email", "profile_name", "status", "tags", "notes",
    };

    /// <summary>Built-in encrypted secret fields that the universal
    /// editor surfaces by default. <c>email_password</c> is the only
    /// pre-baked one — every other secret is user-added via "+ Add
    /// custom field" or bulk import.</summary>
    public static IReadOnlyList<string> EncryptedFields => new[]
    {
        "email_password",
    };

    /// <summary>True if the field is one of the canonical universal
    /// keys and should therefore not be treated as a "custom" field
    /// (i.e. doesn't need a row in <see cref="VaultItem.FieldMetaJson"/>).</summary>
    public static bool IsCanonical(string field) =>
        PlaintextFields.Contains(field, StringComparer.OrdinalIgnoreCase) ||
        EncryptedFields.Contains(field, StringComparer.OrdinalIgnoreCase);
}
