// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Numerics;
using GhostShell.Core.Chains;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 57 — read-only blockchain RPC. Signing stays in the wallet extension
/// (popup), so this never touches keys — it only reads balance / gas / nonce /
/// tx status to drive farm pre-flight checks, the balance column, and
/// transaction-status polling. All amounts are integer base units
/// (wei / lamports) as <see cref="BigInteger"/>.
/// </summary>
public interface IChainRpcClient
{
    /// <summary>Native-coin balance in base units (wei / lamports).</summary>
    Task<BigInteger> GetNativeBalanceAsync(ChainDescriptor chain, string address, CancellationToken ct = default);

    /// <summary>Current gas price in wei (EVM only; 0 for Solana).</summary>
    Task<BigInteger> GetGasPriceWeiAsync(ChainDescriptor chain, CancellationToken ct = default);

    /// <summary>Transaction count / nonce for an address (EVM only; 0 for Solana).</summary>
    Task<long> GetTransactionCountAsync(ChainDescriptor chain, string address, bool pending = true, CancellationToken ct = default);

    /// <summary>Confirmation state of a transaction by hash/signature.</summary>
    Task<TxState> GetTransactionStateAsync(ChainDescriptor chain, string txHash, CancellationToken ct = default);
}
