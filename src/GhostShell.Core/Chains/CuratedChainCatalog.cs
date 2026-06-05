// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Chains;

/// <summary>
/// Phase 57 — shipped list of chains the farm can read. Default RPC endpoints
/// are public/no-key so the feature works out of the box; the operator can
/// override any endpoint in settings (a busy farm should point at a paid RPC).
/// </summary>
public static class CuratedChainCatalog
{
    public static IReadOnlyList<ChainDescriptor> Entries { get; } = new[]
    {
        new ChainDescriptor
        {
            Id = "ethereum", Name = "Ethereum", Family = ChainFamily.Evm, ChainId = 1,
            RpcUrl = "https://eth.llamarpc.com", NativeSymbol = "ETH", Decimals = 18,
            ExplorerTxUrl = "https://etherscan.io/tx/{0}",
        },
        new ChainDescriptor
        {
            Id = "bsc", Name = "BNB Smart Chain", Family = ChainFamily.Evm, ChainId = 56,
            RpcUrl = "https://bsc-dataseed.binance.org", NativeSymbol = "BNB", Decimals = 18,
            ExplorerTxUrl = "https://bscscan.com/tx/{0}",
        },
        new ChainDescriptor
        {
            Id = "polygon", Name = "Polygon", Family = ChainFamily.Evm, ChainId = 137,
            RpcUrl = "https://polygon-rpc.com", NativeSymbol = "POL", Decimals = 18,
            ExplorerTxUrl = "https://polygonscan.com/tx/{0}",
        },
        new ChainDescriptor
        {
            Id = "arbitrum", Name = "Arbitrum One", Family = ChainFamily.Evm, ChainId = 42161,
            RpcUrl = "https://arb1.arbitrum.io/rpc", NativeSymbol = "ETH", Decimals = 18,
            ExplorerTxUrl = "https://arbiscan.io/tx/{0}",
        },
        new ChainDescriptor
        {
            Id = "base", Name = "Base", Family = ChainFamily.Evm, ChainId = 8453,
            RpcUrl = "https://mainnet.base.org", NativeSymbol = "ETH", Decimals = 18,
            ExplorerTxUrl = "https://basescan.org/tx/{0}",
        },
        new ChainDescriptor
        {
            Id = "optimism", Name = "OP Mainnet", Family = ChainFamily.Evm, ChainId = 10,
            RpcUrl = "https://mainnet.optimism.io", NativeSymbol = "ETH", Decimals = 18,
            ExplorerTxUrl = "https://optimistic.etherscan.io/tx/{0}",
        },
        new ChainDescriptor
        {
            Id = "solana", Name = "Solana", Family = ChainFamily.Solana, ChainId = 0,
            RpcUrl = "https://api.mainnet-beta.solana.com", NativeSymbol = "SOL", Decimals = 9,
            ExplorerTxUrl = "https://solscan.io/tx/{0}",
        },
    };

    public static ChainDescriptor? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var c in Entries)
            if (string.Equals(c.Id, id, System.StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
    }

    /// <summary>Default chain for UI/scripts when none is specified.</summary>
    public const string DefaultChainId = "ethereum";
}
