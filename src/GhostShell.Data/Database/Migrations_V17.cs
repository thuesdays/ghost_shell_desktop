// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V17 — Credential Vault (Phase 24).
///
/// Mirrors the legacy web project's vault feature:
///   • <c>vault_items</c> stores per-credential metadata + encrypted
///     secrets blob (<c>secrets_enc</c>). Each row corresponds to one
///     login / API key / wallet / TOTP entry.
///   • <c>vault_config</c> stores the master-password salt + a
///     verifier ciphertext used to validate unlock attempts.
///
/// Crypto is symmetric: PBKDF2-HMAC-SHA256 (200k iterations) on the
/// user's passphrase yields a 32-byte key. AES-GCM wraps each entry's
/// secrets JSON with a per-entry 12-byte nonce + 16-byte auth tag.
/// The verifier is a known plaintext encrypted with the master key —
/// failing to decrypt = wrong password.
///
/// Profile deletion cascades to vault items via a manual cleanup
/// path in the service layer (no FK constraints — keeps the schema
/// flexible, mirrors the existing pattern from V13).
/// </summary>
internal static class Migrations_V17
{
    internal static readonly string[] Statements =
    {
        """
        CREATE TABLE IF NOT EXISTS vault_items (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            name              TEXT    NOT NULL,
            kind              TEXT    NOT NULL DEFAULT 'account',
            service           TEXT,
            identifier        TEXT,
            secrets_enc       BLOB,
            profile_name      TEXT,
            status            TEXT    NOT NULL DEFAULT 'active',
            tags_json         TEXT,
            notes             TEXT,
            last_used_at      TEXT,
            last_login_at     TEXT,
            last_login_status TEXT,
            created_at        TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
            updated_at        TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP)
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_vault_items_kind         ON vault_items(kind);",
        "CREATE INDEX IF NOT EXISTS idx_vault_items_service      ON vault_items(service);",
        "CREATE INDEX IF NOT EXISTS idx_vault_items_status       ON vault_items(status);",
        "CREATE INDEX IF NOT EXISTS idx_vault_items_profile_name ON vault_items(profile_name);",

        // Master-password configuration. One row per key (salt,
        // verifier, kdf_iter, initialized_at). Stored separately so
        // it survives if vault_items is wiped.
        """
        CREATE TABLE IF NOT EXISTS vault_config (
            key       TEXT PRIMARY KEY,
            value     BLOB,
            updated_at TEXT NOT NULL DEFAULT (CURRENT_TIMESTAMP)
        );
        """,
    };
}
