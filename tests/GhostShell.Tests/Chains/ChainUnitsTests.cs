// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Numerics;
using GhostShell.Core.Chains;
using Xunit;

namespace GhostShell.Tests.Chains;

/// <summary>Phase 57 — pure chain unit math (hex parse + base-unit ↔ token).</summary>
public sealed class ChainUnitsTests
{
    [Theory]
    [InlineData("0x1a", 26)]
    [InlineData("1a", 26)]
    [InlineData("0x0", 0)]
    [InlineData("0xff", 255)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("0xZZ", 0)]      // garbage → 0, not throw
    public void ParseHexQuantity_Works(string? hex, long expected)
        => Assert.Equal(new BigInteger(expected), ChainUnits.ParseHexQuantity(hex));

    [Fact]
    public void ParseHexQuantity_HandlesLargeWeiWithoutSignFlip()
    {
        // 1 ETH = 0xDE0B6B3A7640000 — high nibble 'D' must NOT read as negative.
        var wei = ChainUnits.ParseHexQuantity("0xde0b6b3a7640000");
        Assert.Equal(BigInteger.Parse("1000000000000000000"), wei);
        Assert.True(wei > 0);
    }

    [Fact]
    public void ToTokens_And_Format()
    {
        var oneFive = BigInteger.Parse("1500000000000000000"); // 1.5 ETH
        Assert.Equal(1.5m, ChainUnits.ToTokens(oneFive, 18));
        Assert.Equal("1.5", ChainUnits.FormatBalance(oneFive, 18));

        Assert.Equal("0", ChainUnits.FormatBalance(BigInteger.Zero, 18));

        var small = BigInteger.Parse("3400000000000000"); // 0.0034 ETH
        Assert.Equal("0.0034", ChainUnits.FormatBalance(small, 18));
    }

    [Fact]
    public void FormatBalance_TrimsToMaxDecimals_NoRoundingUp()
    {
        // 0.123456789 ETH, default 6 decimals, truncates (ToZero) → 0.123456
        var v = BigInteger.Parse("123456789000000000");
        Assert.Equal("0.123456", ChainUnits.FormatBalance(v, 18));
    }

    [Fact]
    public void WeiToGwei()
    {
        Assert.Equal(1m, ChainUnits.WeiToGwei(new BigInteger(1_000_000_000)));
        Assert.Equal(25m, ChainUnits.WeiToGwei(new BigInteger(25_000_000_000)));
    }

    [Theory]
    [InlineData("1.5", 18, "1500000000000000000")]
    [InlineData("0", 18, "0")]
    [InlineData("0.05", 9, "50000000")]   // Solana lamports
    public void TokensToBaseUnits(string amount, int decimals, string expected)
        => Assert.Equal(BigInteger.Parse(expected), ChainUnits.TokensToBaseUnits(amount, decimals));

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    public void TokensToBaseUnits_RejectsBadInput(string bad)
        => Assert.Throws<System.FormatException>(() => ChainUnits.TokensToBaseUnits(bad, 18));

    [Fact]
    public void FormatBalance_WhaleValue_NoDecimalOverflow()
    {
        // 1e30 wei = 1e12 ETH — overflows `decimal`; BigInteger path must cope.
        var huge = BigInteger.Pow(10, 30);
        Assert.Equal("1000000000000", ChainUnits.FormatBalance(huge, 18));
    }

    [Fact]
    public void TokensToBaseUnits_LargeAmount_NoDecimalOverflow()
    {
        // 1e11 tokens × 1e18 would overflow decimal — must stay in BigInteger.
        var r = ChainUnits.TokensToBaseUnits("100000000000", 18);
        Assert.Equal(BigInteger.Parse("100000000000") * BigInteger.Pow(10, 18), r);
    }
}
