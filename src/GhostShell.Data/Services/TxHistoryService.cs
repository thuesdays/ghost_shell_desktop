// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Chains;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>Phase 57 — SQLite-backed transaction-history store (tx_history).</summary>
public sealed class TxHistoryService : ITxHistoryService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<TxHistoryService> _log;

    public TxHistoryService(DatabaseConnection db, ILogger<TxHistoryService> log)
    {
        _db  = db;
        _log = log;
    }

    private const string SelectColumns = """
        id,
        profile_name AS ProfileName,
        chain_id     AS ChainId,
        address      AS Address,
        tx_hash      AS TxHash,
        kind         AS Kind,
        state        AS State,
        value_text   AS ValueText,
        note         AS Note,
        created_at   AS CreatedAt,
        updated_at   AS UpdatedAt
    """;

    public async Task<long> RecordAsync(TxHistoryEntry entry, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        // Atomic upsert + id return in ONE statement (SQLite 3.35+ supports
        // RETURNING) — no separate SELECT, no gap a concurrent writer could
        // race. ON CONFLICT...RETURNING yields the existing row's id on update.
        const string upsert = """
            INSERT INTO tx_history
                (profile_name, chain_id, address, tx_hash, kind, state, value_text, note, created_at, updated_at)
            VALUES
                (@ProfileName, @ChainId, @Address, @TxHash, @Kind, @State, @ValueText, @Note, @CreatedAt, @UpdatedAt)
            ON CONFLICT(chain_id, tx_hash) DO UPDATE SET
                profile_name = excluded.profile_name,
                address      = excluded.address,
                kind         = excluded.kind,
                state        = excluded.state,
                value_text   = excluded.value_text,
                note         = excluded.note,
                updated_at   = excluded.updated_at
            RETURNING id;
        """;
        var p = new
        {
            entry.ProfileName,
            entry.ChainId,
            entry.Address,
            entry.TxHash,
            Kind = string.IsNullOrWhiteSpace(entry.Kind) ? "tx" : entry.Kind,
            State = NormaliseState(entry.State),
            entry.ValueText,
            entry.Note,
            CreatedAt = entry.CreatedAt == default ? now : entry.CreatedAt,
            UpdatedAt = now,
        };
        var id = await _db.QueueAsync(c => c.ExecuteScalarAsync<long>(upsert, p), ct);
        _log.LogInformation("tx recorded #{Id} {Chain} {Hash} state={State}", id, entry.ChainId, Short(entry.TxHash), p.State);
        return id;
    }

    public Task UpdateStateAsync(long id, TxState state, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "UPDATE tx_history SET state = @s, updated_at = @t WHERE id = @id;",
            new { s = StateText(state), t = DateTime.UtcNow, id }), ct);

    public Task UpdateStateByHashAsync(string chainId, string txHash, TxState state, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "UPDATE tx_history SET state = @s, updated_at = @t WHERE chain_id = @c AND tx_hash = @h;",
            new { s = StateText(state), t = DateTime.UtcNow, c = chainId, h = txHash }), ct);

    public async Task<IReadOnlyList<TxHistoryEntry>> ListAsync(
        string? profileName = null, string? chainId = null, int limit = 200, CancellationToken ct = default)
    {
        var lim = Math.Clamp(limit, 1, 2000);
        var sql = $"""
            SELECT {SelectColumns} FROM tx_history
            WHERE (@profile IS NULL OR profile_name = @profile)
              AND (@chain IS NULL OR chain_id = @chain)
            ORDER BY created_at DESC
            LIMIT {lim};
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<TxHistoryEntry>(
            sql, new { profile = profileName, chain = chainId }), ct);
        return rows.ToList();
    }

    /// <summary>Canonical lower-case state string for a <see cref="TxState"/>.</summary>
    public static string StateText(TxState state) => state switch
    {
        TxState.Pending => "pending",
        TxState.Success => "success",
        TxState.Failed  => "failed",
        _               => "unknown",
    };

    /// <summary>Coerce an arbitrary caller-supplied state into the canonical set.</summary>
    private static string NormaliseState(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "success" => "success",
        "failed"  => "failed",
        "unknown" => "unknown",
        _         => "pending",
    };

    private static string Short(string h) => h.Length <= 12 ? h : $"{h[..8]}…{h[^4..]}";
}
