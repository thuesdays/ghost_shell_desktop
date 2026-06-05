// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V29 — Phase 57 crypto-farm transaction history.
///
/// Records blockchain transactions the farm scripts capture after a wallet
/// popup confirm (the dApp/wallet owns the hash; the script reads it off the
/// page via <c>record_tx</c>). <c>wait_tx</c> and background polling update the
/// <c>state</c> column via RPC. Read-only side of crypto — no keys, no signing.
///
/// Unique on (chain_id, tx_hash) so re-recording the same hash updates rather
/// than duplicates. Tolerant: CREATE TABLE / INDEX IF NOT EXISTS are safe to
/// re-run.
/// </summary>
internal static class Migrations_V29
{
    internal static readonly string[] Statements =
    {
        """
        CREATE TABLE IF NOT EXISTS tx_history (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_name  TEXT,
            chain_id      TEXT NOT NULL,
            address       TEXT,
            tx_hash       TEXT NOT NULL,
            kind          TEXT NOT NULL DEFAULT 'tx',
            state         TEXT NOT NULL DEFAULT 'pending',
            value_text    TEXT,
            note          TEXT,
            created_at    TEXT NOT NULL,
            updated_at    TEXT NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS ix_tx_history_chain_hash ON tx_history (chain_id, tx_hash);",
        "CREATE INDEX IF NOT EXISTS ix_tx_history_profile ON tx_history (profile_name);",
        "CREATE INDEX IF NOT EXISTS ix_tx_history_created ON tx_history (created_at DESC);",
    };
}
