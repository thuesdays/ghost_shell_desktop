// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Phase 57 — one recorded blockchain transaction. Populated by the
/// <c>record_tx</c> script step (the hash the script captured from the dApp
/// after a wallet popup confirm) and updated by <c>wait_tx</c> / background
/// polling. Read-only model — signing happens in the wallet, not here.
/// </summary>
public sealed record TxHistoryEntry
{
    public long Id { get; init; }
    /// <summary>Owning profile (farm member), or null for ad-hoc records.</summary>
    public string? ProfileName { get; init; }
    /// <summary>Chain descriptor id, e.g. "ethereum", "solana".</summary>
    public required string ChainId { get; init; }
    /// <summary>Sending wallet address (non-secret).</summary>
    public string? Address { get; init; }
    /// <summary>Transaction hash / Solana signature.</summary>
    public required string TxHash { get; init; }
    /// <summary>What the tx was: "send" / "approve" / "swap" / "mint" / "task" / …</summary>
    public string Kind { get; init; } = "tx";
    /// <summary>Normalised state: pending / success / failed / unknown.</summary>
    public string State { get; init; } = "pending";
    /// <summary>Human amount, e.g. "0.05 ETH" (optional).</summary>
    public string? ValueText { get; init; }
    public string? Note { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
