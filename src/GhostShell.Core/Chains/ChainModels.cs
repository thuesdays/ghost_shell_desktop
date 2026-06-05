// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Chains;

/// <summary>Chain family — decides the RPC dialect (EVM JSON-RPC vs Solana).</summary>
public enum ChainFamily
{
    Evm,
    Solana,
}

/// <summary>Confirmation state of a transaction, normalised across chains.</summary>
public enum TxState
{
    Unknown,
    Pending,
    Success,
    Failed,
}

/// <summary>
/// Phase 57 — a blockchain network the farm can read from. Read-only metadata:
/// signing stays in the wallet extension (popup), so we never need keys here —
/// only an RPC endpoint to check balances / gas / tx status, and an explorer
/// URL template to show the user a clickable link.
/// </summary>
public sealed record ChainDescriptor
{
    /// <summary>Stable key, e.g. "ethereum", "bsc", "solana".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public ChainFamily Family { get; init; } = ChainFamily.Evm;

    /// <summary>EIP-155 chain id (EVM only; 0 for Solana).</summary>
    public long ChainId { get; init; }

    /// <summary>Public JSON-RPC endpoint. User-overridable via settings.</summary>
    public required string RpcUrl { get; init; }

    public required string NativeSymbol { get; init; }
    /// <summary>Native-unit decimals (EVM = 18, Solana = 9).</summary>
    public int Decimals { get; init; } = 18;

    /// <summary>Explorer tx URL with a single <c>{0}</c> hash placeholder.</summary>
    public string ExplorerTxUrl { get; init; } = "";

    /// <summary>Build a clickable explorer link for a tx hash (empty if none).</summary>
    public string TxLink(string hash)
        => string.IsNullOrEmpty(ExplorerTxUrl) || string.IsNullOrEmpty(hash)
            ? ""
            : string.Format(ExplorerTxUrl, hash);
}
