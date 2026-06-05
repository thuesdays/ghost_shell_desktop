// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Chains;
using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>Phase 57 — persistence for recorded blockchain transactions.</summary>
public interface ITxHistoryService
{
    /// <summary>Insert a record; returns its new id. Idempotent on
    /// (chain_id, tx_hash) — re-recording the same hash updates rather than
    /// duplicates.</summary>
    Task<long> RecordAsync(TxHistoryEntry entry, CancellationToken ct = default);

    /// <summary>Update the normalised state of a record by id.</summary>
    Task UpdateStateAsync(long id, TxState state, CancellationToken ct = default);

    /// <summary>Update state by (chain, hash) — used by background pollers.</summary>
    Task UpdateStateByHashAsync(string chainId, string txHash, TxState state, CancellationToken ct = default);

    /// <summary>Recent entries, newest first, optionally filtered.</summary>
    Task<IReadOnlyList<TxHistoryEntry>> ListAsync(
        string? profileName = null, string? chainId = null, int limit = 200,
        CancellationToken ct = default);
}
