// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Wallets;
using Xunit;

namespace GhostShell.Tests.Wallets;

/// <summary>
/// Phase 56 — pure farm-readiness logic for the crypto-farm panel: a member is
/// ready only with BOTH a bound wallet and a stored password; addresses are
/// masked; the summary buckets members correctly.
/// </summary>
public sealed class FarmReadinessTests
{
    [Fact]
    public void Ready_WhenWalletAndPasswordPresent()
    {
        var s = FarmReadiness.Evaluate("p1", hasWallet: true, hasPassword: true,
            address: "0x1234567890abcdef1234567890abcdefABCDEF12");
        Assert.True(s.Ready);
        Assert.Equal("", s.Reason);
        Assert.Equal("0x1234…EF12", s.AddressMasked);
    }

    [Fact]
    public void NotReady_WhenNoWallet()
    {
        var s = FarmReadiness.Evaluate("p1", hasWallet: false, hasPassword: false, address: null);
        Assert.False(s.Ready);
        Assert.Contains("wallet", s.Reason, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", s.AddressMasked);
    }

    [Fact]
    public void NotReady_WhenWalletButNoPassword()
    {
        var s = FarmReadiness.Evaluate("p1", hasWallet: true, hasPassword: false, address: "0xabc");
        Assert.False(s.Ready);
        Assert.Contains("password", s.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("0xABC", "0xABC")]              // short — shown as-is
    [InlineData("0x1234567890", "0x1234567890")] // 12 chars — at the boundary, as-is
    [InlineData("0x1234567890A", "0x1234…890A")]  // 13 chars — masked
    public void MaskAddress_ShortensLongOnly(string input, string expected)
        => Assert.Equal(expected, FarmReadiness.MaskAddress(input));

    [Fact]
    public void MaskAddress_TrimsWhitespace()
        => Assert.Equal("", FarmReadiness.MaskAddress("   "));

    [Fact]
    public void Summarize_BucketsMembers()
    {
        var members = new[]
        {
            FarmReadiness.Evaluate("a", true,  true,  "0x1111111111111111"),
            FarmReadiness.Evaluate("b", true,  true,  "0x2222222222222222"),
            FarmReadiness.Evaluate("c", true,  false, "0x3333333333333333"), // no password
            FarmReadiness.Evaluate("d", false, false, null),                  // no wallet
        };
        var sum = FarmReadiness.Summarize(members);
        Assert.Equal(4, sum.Total);
        Assert.Equal(2, sum.Ready);
        Assert.Equal(1, sum.NeedsWallet);
        Assert.Equal(1, sum.NeedsPassword);
        Assert.Contains("2/4 ready", sum.Label);
    }

    [Fact]
    public void Summarize_Empty_IsZeroAndLabelled()
    {
        var sum = FarmReadiness.Summarize(System.Array.Empty<FarmMemberStatus>());
        Assert.Equal(0, sum.Total);
        Assert.Equal("no members", sum.Label);
    }
}
