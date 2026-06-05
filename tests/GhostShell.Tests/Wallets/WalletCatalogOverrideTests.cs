// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Wallets;
using Xunit;

namespace GhostShell.Tests.Wallets;

/// <summary>Phase 57 — operator selector-override parsing + merge.</summary>
public sealed class WalletCatalogOverrideTests
{
    [Fact]
    public void ParseOverrides_TolerantOnGarbage()
    {
        Assert.Empty(WalletCatalog.ParseOverrides(null));
        Assert.Empty(WalletCatalog.ParseOverrides(""));
        Assert.Empty(WalletCatalog.ParseOverrides("{ not json"));
    }

    [Fact]
    public void Resolve_NoOverrides_ReturnsCuratedBase()
    {
        var w = WalletCatalog.Resolve("metamask", null);
        Assert.NotNull(w);
        Assert.Equal("metamask", w!.Id);
    }

    [Fact]
    public void Resolve_UnknownWallet_IsNull()
        => Assert.Null(WalletCatalog.Resolve("nope", null));

    [Fact]
    public void Apply_ExtraSelectors_ArePrependedToFlowSteps()
    {
        var json = """
            { "metamask": { "extraSelectors": { "confirm": ["[data-testid='NEW']"] } } }
        """;
        var set = WalletCatalog.ParseOverrides(json);
        var w = WalletCatalog.Resolve("metamask", set)!;

        var confirm = w.GetFlow(WalletFlow.Confirm)!;
        // The new selector must be present and tried first on a selector step.
        var firstSelStep = confirm.Steps.First(s =>
            s.Action is WalletAction.WaitFor or WalletAction.Click or WalletAction.Type);
        Assert.Equal("[data-testid='NEW']", firstSelStep.AnyOfSelectors[0]);

        // The unlock flow (not overridden) is untouched.
        var unlock = w.GetFlow(WalletFlow.Unlock)!;
        Assert.DoesNotContain("[data-testid='NEW']", unlock.Steps[0].AnyOfSelectors);
    }

    [Fact]
    public void Apply_ExtraSelectors_FlowKeyIsCaseInsensitive()
    {
        // Audit M2: an operator who writes "Confirm" (capitalised) must still match
        // the lowercase flow key, not silently no-op.
        var json = """
            { "metamask": { "extraSelectors": { "Confirm": ["[data-testid='CI']"] } } }
        """;
        var w = WalletCatalog.Resolve("metamask", WalletCatalog.ParseOverrides(json))!;
        var firstSelStep = w.GetFlow(WalletFlow.Confirm)!.Steps.First(s =>
            s.Action is WalletAction.WaitFor or WalletAction.Click or WalletAction.Type);
        Assert.Equal("[data-testid='CI']", firstSelStep.AnyOfSelectors[0]);
    }

    [Fact]
    public void Apply_ExtIdsOverride_Replaces()
    {
        var json = """
            { "metamask": { "extIds": ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"] } }
        """;
        var w = WalletCatalog.Resolve("metamask", WalletCatalog.ParseOverrides(json))!;
        Assert.Single(w.ExtIds);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", w.ExtIds[0]);
        Assert.True(w.MatchesPopupUrl("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/x.html"));
    }

    [Fact]
    public void Apply_DoesNotDuplicateExistingSelector()
    {
        // Re-add a selector MetaMask's confirm already has → must dedupe.
        var existing = CuratedWalletCatalog.TryGet("metamask")!
            .GetFlow(WalletFlow.Confirm)!.Steps
            .First(s => s.AnyOfSelectors.Length > 0).AnyOfSelectors[0];

        var json = $$"""
            { "metamask": { "extraSelectors": { "confirm": ["{{existing}}"] } } }
        """;
        var w = WalletCatalog.Resolve("metamask", WalletCatalog.ParseOverrides(json))!;
        var step = w.GetFlow(WalletFlow.Confirm)!.Steps.First(s => s.AnyOfSelectors.Contains(existing));
        Assert.Equal(1, step.AnyOfSelectors.Count(s => s == existing));
    }

    [Fact]
    public void RoundTrip_SerializeParse()
    {
        var set = WalletCatalog.ParseOverrides(
            "{ \"okx\": { \"extraSelectors\": { \"unlock\": [\".x\"] } } }");
        var json = WalletCatalog.SerializeOverrides(set);
        var again = WalletCatalog.ParseOverrides(json);
        Assert.True(again.ContainsKey("okx"));
    }
}
