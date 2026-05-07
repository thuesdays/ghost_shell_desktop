// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V27 — Unified vault model (Phase 71).
///
/// The legacy vault model was kind-aware: each <c>VaultItem</c> picked
/// one of {account, email, social, crypto_wallet, api_key, …} from a
/// fixed catalog, and the secret-field schema was determined by that
/// kind. This forced users to create N items per profile (one for the
/// social account, one for the email, one for the wallet) which got
/// tedious and made script-side aliasing fragile.
///
/// Phase 71 collapses this into a single universal item per profile
/// with arbitrary user-defined fields. Each field carries its own
/// metadata (encrypted? TOTP-source?) so the runtime knows whether to:
///   • store it in the encrypted secrets blob vs. plaintext extras
///   • redact its cleartext value from script logs
///   • feed it through Totp.Compute() at <c>{{vault.X}}</c> resolution
///
/// Three new columns on <c>vault_items</c>:
///
///   • <c>email</c> — top-level searchable email column. Plaintext
///     metadata, like <c>identifier</c>; surfaced separately because
///     "email of this account" is a very common search axis.
///
///   • <c>field_meta_json</c> — JSON object mapping
///     <c>field_name → {"encrypted": bool, "is_totp": bool}</c> for
///     each custom field on the item. Defaults that aren't in the JSON
///     fall back to "encrypted=true, is_totp=false" so the safe path
///     is the default (we'd rather over-encrypt a non-secret than
///     accidentally expose a token).
///
///   • <c>extras_json</c> — JSON object holding non-encrypted custom
///     field values. The encrypted ones still live in <c>secrets_enc</c>;
///     this is for fields the user explicitly marked as plaintext
///     (e.g. "device_id", "user_agent_hint" — searchable, low-value).
///
/// All columns nullable. Existing rows untouched: their kind-aware
/// schema continues to work because the editor renders whatever keys
/// it finds in the secrets bag, regardless of whether they came from
/// the canonical Fields list or are user-defined.
///
/// Tolerant-statement path: a fresh DB created after this migration
/// runs may already have the columns from a future bootstrap; the
/// runner swallows duplicate-column errors.
/// </summary>
internal static class Migrations_V27
{
    internal static readonly string[] Statements =
    {
        // Top-level email column — searchable plaintext like identifier.
        "ALTER TABLE vault_items ADD COLUMN email TEXT;",

        // Per-field metadata: encrypted? is_totp?
        // JSON shape: {"discord_token": {"encrypted": true, "is_totp": false}, …}
        "ALTER TABLE vault_items ADD COLUMN field_meta_json TEXT;",

        // Plaintext custom-field values (encrypted ones stay in secrets_enc).
        // JSON shape: {"device_id": "abc-123", "user_agent_hint": "Chrome/120"}
        "ALTER TABLE vault_items ADD COLUMN extras_json TEXT;",

        // Index for fast email-search since it's a common filter axis.
        "CREATE INDEX IF NOT EXISTS idx_vault_items_email ON vault_items(email);",
    };
}
