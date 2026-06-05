// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Wallets;
using Xunit;

namespace GhostShell.Tests.Wallets;

/// <summary>
/// Phase 56 — pins the shipped wallet catalog: the reference wallets exist,
/// their extension IDs are valid Chrome IDs, every flow step is well-formed,
/// and popup-URL matching routes a chrome-extension URL to the right wallet.
/// Live popup driving isn't unit-testable; this locks the config that drives it.
/// </summary>
public sealed class CuratedWalletCatalogTests
{
    [Theory]
    [InlineData("metamask")]
    [InlineData("okx")]
    [InlineData("phantom")]
    [InlineData("rabby")]
    [InlineData("backpack")]
    public void Catalog_HasReferenceWallets(string id)
    {
        var w = CuratedWalletCatalog.TryGet(id);
        Assert.NotNull(w);
        Assert.NotEmpty(w!.ExtIds);
        Assert.NotEmpty(w.Flows);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive_AndNullSafe()
    {
        Assert.NotNull(CuratedWalletCatalog.TryGet("MetaMask"));
        Assert.NotNull(CuratedWalletCatalog.TryGet("OKX"));
        Assert.Null(CuratedWalletCatalog.TryGet("does-not-exist"));
        Assert.Null(CuratedWalletCatalog.TryGet(null));
        Assert.Null(CuratedWalletCatalog.TryGet("  "));
    }

    [Fact]
    public void ExtIds_AreValidChromeIds()
    {
        foreach (var w in CuratedWalletCatalog.Entries)
            foreach (var id in w.ExtIds)
            {
                Assert.Equal(32, id.Length);
                Assert.All(id, c => Assert.InRange(c, 'a', 'p')); // Chrome IDs are a–p
            }
    }

    [Theory]
    [InlineData("metamask")]
    [InlineData("okx")]
    [InlineData("phantom")]
    [InlineData("rabby")]
    [InlineData("backpack")]
    public void EveryWallet_HasUnlockConnectConfirm(string id)
    {
        var w = CuratedWalletCatalog.TryGet(id)!;
        Assert.NotNull(w.GetFlow(WalletFlow.Unlock));
        Assert.NotNull(w.GetFlow(WalletFlow.Connect));
        Assert.NotNull(w.GetFlow(WalletFlow.Confirm));
    }

    [Fact]
    public void AllFlowSteps_AreWellFormed()
    {
        foreach (var w in CuratedWalletCatalog.Entries)
            foreach (var flow in w.Flows.Values)
            {
                Assert.NotEmpty(flow.Steps);
                foreach (var step in flow.Steps)
                    Assert.True(step.IsWellFormed,
                        $"{w.Id}/{flow.Name}: malformed step '{step.Description}' ({step.Action})");
            }
    }

    [Fact]
    public void UnlockFlow_OpensExtensionPage_AndTypesVaultPassword()
    {
        foreach (var w in CuratedWalletCatalog.Entries)
        {
            var unlock = w.GetFlow(WalletFlow.Unlock)!;
            Assert.True(unlock.OpensExtensionPage, $"{w.Id} unlock must open the ext page as a tab");

            var typed = unlock.Steps.FirstOrDefault(s => s.Action == WalletAction.Type);
            Assert.NotNull(typed);
            Assert.Equal(CuratedWalletCatalog.PasswordPlaceholder, typed!.Value);
        }
    }

    [Fact]
    public void ConfirmFlow_AttachesToPopupWindow_NotExtensionPage()
    {
        foreach (var w in CuratedWalletCatalog.Entries)
        {
            var confirm = w.GetFlow(WalletFlow.Confirm)!;
            Assert.False(confirm.OpensExtensionPage,
                $"{w.Id} confirm must attach to the wallet-spawned popup window");
        }
    }

    [Fact]
    public void MatchesPopupUrl_MatchesOwnExtension_RejectsOthers()
    {
        var mm = CuratedWalletCatalog.TryGet("metamask")!;
        var ext = mm.ExtIds[0];
        Assert.True(mm.MatchesPopupUrl($"chrome-extension://{ext}/notification.html"));
        Assert.True(mm.MatchesPopupUrl($"chrome-extension://{ext}/home.html#confirm-transaction"));
        Assert.False(mm.MatchesPopupUrl("https://app.uniswap.org/"));
        Assert.False(mm.MatchesPopupUrl("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/x.html"));
        Assert.False(mm.MatchesPopupUrl(null));
        Assert.False(mm.MatchesPopupUrl(""));
    }

    [Fact]
    public void MatchByPopupUrl_RoutesToCorrectWallet()
    {
        var okx = CuratedWalletCatalog.TryGet("okx")!;
        var hit = CuratedWalletCatalog.MatchByPopupUrl($"chrome-extension://{okx.ExtIds[0]}/popup.html");
        Assert.NotNull(hit);
        Assert.Equal("okx", hit!.Id);

        Assert.Null(CuratedWalletCatalog.MatchByPopupUrl("https://example.com"));
        Assert.Null(CuratedWalletCatalog.MatchByPopupUrl(null));
    }

    [Fact]
    public void ExtIds_AreUniqueAcrossCatalog()
    {
        var all = CuratedWalletCatalog.Entries.SelectMany(w => w.ExtIds).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
