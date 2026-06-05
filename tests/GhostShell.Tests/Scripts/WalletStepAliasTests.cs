// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Runtime.Scripts;
using Xunit;

namespace GhostShell.Tests.Scripts;

/// <summary>
/// Phase 56 audit fix (C2) — wallet steps reference {{vault.PASS}} from the
/// catalog (C# code), not the script JSON. CollectVaultAliases must therefore
/// force the wallet aliases whenever a wallet step is present, so the runner
/// pre-resolves the unlock password (else the literal placeholder would be
/// typed as the password and lock the wallet).
/// </summary>
public sealed class WalletStepAliasTests
{
    [Theory]
    [InlineData("wallet_unlock")]
    [InlineData("wallet_connect")]
    [InlineData("wallet_confirm")]
    [InlineData("wallet_approve")]
    [InlineData("wallet_flow")]
    public void WalletStep_ForcesPassAndAddrAliases(string stepType)
    {
        var json = "[{\"type\":\"" + stepType + "\",\"params\":{\"wallet\":\"metamask\"}}]";
        var aliases = ScriptRunner.CollectVaultAliases(json);
        Assert.Contains("PASS", aliases);
        Assert.Contains("ADDR", aliases);
    }

    [Fact]
    public void NonWalletScript_DoesNotForceWalletAliases()
    {
        var json = """[{"type":"navigate","params":{"url":"https://example.com"}}]""";
        var aliases = ScriptRunner.CollectVaultAliases(json);
        Assert.DoesNotContain("PASS", aliases);
        Assert.DoesNotContain("ADDR", aliases);
    }

    [Fact]
    public void ExplicitVaultPlaceholders_StillCollected()
    {
        var json = """[{"type":"type","params":{"value":"{{vault.SEED}}"}}]""";
        var aliases = ScriptRunner.CollectVaultAliases(json);
        Assert.Contains("SEED", aliases);
    }

    [Fact]
    public void WalletStep_IsCaseInsensitive()
    {
        var json = """[{"type":"WALLET_UNLOCK"}]""";
        var aliases = ScriptRunner.CollectVaultAliases(json);
        Assert.Contains("PASS", aliases);
    }
}
