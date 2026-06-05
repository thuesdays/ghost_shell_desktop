// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Chains;
using Xunit;

namespace GhostShell.Tests.Chains;

public sealed class CuratedChainCatalogTests
{
    [Theory]
    [InlineData("ethereum")]
    [InlineData("bsc")]
    [InlineData("polygon")]
    [InlineData("arbitrum")]
    [InlineData("base")]
    [InlineData("optimism")]
    [InlineData("solana")]
    public void HasReferenceChains(string id)
    {
        var c = CuratedChainCatalog.TryGet(id);
        Assert.NotNull(c);
        Assert.StartsWith("http", c!.RpcUrl);
        Assert.False(string.IsNullOrWhiteSpace(c.NativeSymbol));
        Assert.True(c.Decimals > 0);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive_AndNullSafe()
    {
        Assert.NotNull(CuratedChainCatalog.TryGet("Ethereum"));
        Assert.Null(CuratedChainCatalog.TryGet("dogechain-x"));
        Assert.Null(CuratedChainCatalog.TryGet(null));
    }

    [Fact]
    public void DefaultChain_Exists()
        => Assert.NotNull(CuratedChainCatalog.TryGet(CuratedChainCatalog.DefaultChainId));

    [Fact]
    public void Solana_IsSolanaFamily_EvmAreEvm()
    {
        Assert.Equal(ChainFamily.Solana, CuratedChainCatalog.TryGet("solana")!.Family);
        Assert.Equal(ChainFamily.Evm, CuratedChainCatalog.TryGet("ethereum")!.Family);
    }

    [Fact]
    public void TxLink_FormatsHash()
    {
        var eth = CuratedChainCatalog.TryGet("ethereum")!;
        Assert.Equal("https://etherscan.io/tx/0xabc", eth.TxLink("0xabc"));
        Assert.Equal("", eth.TxLink(""));
    }
}
