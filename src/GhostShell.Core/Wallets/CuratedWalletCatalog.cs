// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Wallets;

/// <summary>
/// Phase 56 — shipped catalog of wallet descriptors for crypto-farm
/// automation. MetaMask is the reference (its <c>data-testid</c> selectors are
/// stable across releases); OKX / Phantom / Rabby / Backpack ship best-effort
/// selector sets that the operator can tune.
///
/// Selectors are deliberately MULTI-candidate (<see cref="WalletFlowStep.AnyOfSelectors"/>):
/// the driver acts on the first one present, so a single descriptor survives
/// minor DOM churn and covers a couple of UI versions at once.
/// </summary>
public static class CuratedWalletCatalog
{
    // ─── shared placeholders ──────────────────────────────────────────
    /// <summary>The unlock password is supplied as this placeholder so the
    /// script engine resolves it from the profile-bound vault item.</summary>
    public const string PasswordPlaceholder = "{{vault.PASS}}";

    public static IReadOnlyList<WalletDescriptor> Entries { get; } = new[]
    {
        MetaMask(),
        Okx(),
        Phantom(),
        Rabby(),
        Backpack(),
    };

    /// <summary>Case-insensitive lookup by descriptor id (e.g. "metamask").</summary>
    public static WalletDescriptor? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var w in Entries)
            if (string.Equals(w.Id, id, System.StringComparison.OrdinalIgnoreCase))
                return w;
        return null;
    }

    /// <summary>Find the descriptor whose extension serves <paramref name="url"/>.</summary>
    public static WalletDescriptor? MatchByPopupUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        foreach (var w in Entries)
            if (w.MatchesPopupUrl(url))
                return w;
        return null;
    }

    // ─── builders ─────────────────────────────────────────────────────

    private static WalletFlowStep WaitFor(string desc, params string[] sel)
        => new() { Action = WalletAction.WaitFor, AnyOfSelectors = sel, Description = desc };

    private static WalletFlowStep Click(string desc, params string[] sel)
        => new() { Action = WalletAction.Click, AnyOfSelectors = sel, Description = desc, DelayMs = 350 };

    private static WalletFlowStep ClickOptional(string desc, params string[] sel)
        => new() { Action = WalletAction.Click, AnyOfSelectors = sel, Description = desc, Optional = true, DelayMs = 350 };

    private static WalletFlowStep TypePassword(params string[] sel)
        => new() { Action = WalletAction.Type, AnyOfSelectors = sel, Value = PasswordPlaceholder, Description = "type wallet password" };

    // ─── MetaMask (reference) ─────────────────────────────────────────
    private static WalletDescriptor MetaMask() => new()
    {
        Id = "metamask",
        Name = "MetaMask",
        Chain = WalletChain.Evm,
        ExtIds = new[] { "nkbihfbeogaeaoehlefnkodbefgpgknn" },
        HomePage = "home.html",
        Flows = new Dictionary<string, WalletFlow>
        {
            [WalletFlow.Unlock] = new()
            {
                Name = WalletFlow.Unlock,
                OpensExtensionPage = true,
                Steps = new[]
                {
                    WaitFor("unlock password field", "#password", "[data-testid='unlock-password']", "input[type='password']"),
                    TypePassword("#password", "[data-testid='unlock-password']", "input[type='password']"),
                    Click("unlock button", "[data-testid='unlock-submit']", "button[type='submit']"),
                },
            },
            [WalletFlow.Connect] = new()
            {
                Name = WalletFlow.Connect,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    ClickOptional("select all accounts", "[data-testid='choose-account-list-operate-all-check']"),
                    Click("next / connect", "[data-testid='page-container-footer-next']", "[data-testid='confirm-btn']", "button[type='submit']"),
                    ClickOptional("confirm connect", "[data-testid='page-container-footer-next']", "[data-testid='confirm-btn']"),
                },
            },
            [WalletFlow.Confirm] = new()
            {
                Name = WalletFlow.Confirm,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    WaitFor("confirm dialog", "[data-testid='page-container-footer-next']", "[data-testid='confirm-footer-button']", "button[type='submit']"),
                    Click("confirm transaction", "[data-testid='confirm-footer-button']", "[data-testid='page-container-footer-next']", "button[type='submit']"),
                },
            },
            [WalletFlow.Approve] = new()
            {
                Name = WalletFlow.Approve,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    ClickOptional("use default spend cap", "[data-testid='custom-spending-cap-input']"),
                    Click("approve / next", "[data-testid='page-container-footer-next']", "[data-testid='confirm-footer-button']", "button[type='submit']"),
                    ClickOptional("confirm approval", "[data-testid='confirm-footer-button']", "[data-testid='page-container-footer-next']"),
                },
            },
        },
    };

    // ─── OKX Wallet ───────────────────────────────────────────────────
    private static WalletDescriptor Okx() => new()
    {
        Id = "okx",
        Name = "OKX Wallet",
        Chain = WalletChain.Multi,
        ExtIds = new[] { "mcohilncbfahbmgdjkbpemcciiolgcge" },
        HomePage = "home.html",
        Flows = new Dictionary<string, WalletFlow>
        {
            [WalletFlow.Unlock] = new()
            {
                Name = WalletFlow.Unlock,
                OpensExtensionPage = true,
                Steps = new[]
                {
                    WaitFor("unlock password field", "input[type='password']", "[data-testid='okd-input']"),
                    TypePassword("input[type='password']", "[data-testid='okd-input']"),
                    Click("unlock button", "button[type='submit']", "button.okui-btn"),
                },
            },
            [WalletFlow.Connect] = new()
            {
                Name = WalletFlow.Connect,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    Click("connect", "button[type='submit']", "button.okui-btn", "button[data-testid='okd-button']"),
                },
            },
            [WalletFlow.Confirm] = new()
            {
                Name = WalletFlow.Confirm,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    WaitFor("confirm dialog", "button[type='submit']", "button.okui-btn"),
                    Click("confirm transaction", "button[type='submit']", "button.okui-btn"),
                },
            },
            [WalletFlow.Approve] = new()
            {
                Name = WalletFlow.Approve,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    Click("approve", "button[type='submit']", "button.okui-btn"),
                },
            },
        },
    };

    // ─── Phantom (Solana) ─────────────────────────────────────────────
    private static WalletDescriptor Phantom() => new()
    {
        Id = "phantom",
        Name = "Phantom",
        Chain = WalletChain.Solana,
        ExtIds = new[] { "bfnaelmomeimhlpmgjnjophhpkkoljpa" },
        HomePage = "popup.html",
        Flows = new Dictionary<string, WalletFlow>
        {
            [WalletFlow.Unlock] = new()
            {
                Name = WalletFlow.Unlock,
                OpensExtensionPage = true,
                Steps = new[]
                {
                    WaitFor("unlock password field", "input[type='password']", "[data-testid='unlock-form-password-input']"),
                    TypePassword("input[type='password']", "[data-testid='unlock-form-password-input']"),
                    Click("unlock button", "[data-testid='unlock-form-submit-button']", "button[type='submit']"),
                },
            },
            [WalletFlow.Connect] = new()
            {
                Name = WalletFlow.Connect,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    Click("connect", "[data-testid='primary-button']", "button[type='submit']"),
                },
            },
            [WalletFlow.Confirm] = new()
            {
                Name = WalletFlow.Confirm,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    WaitFor("confirm dialog", "[data-testid='primary-button']", "button[type='submit']"),
                    Click("approve transaction", "[data-testid='primary-button']", "button[type='submit']"),
                },
            },
            [WalletFlow.Approve] = new()
            {
                Name = WalletFlow.Approve,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    Click("approve", "[data-testid='primary-button']", "button[type='submit']"),
                },
            },
        },
    };

    // ─── Rabby ────────────────────────────────────────────────────────
    private static WalletDescriptor Rabby() => new()
    {
        Id = "rabby",
        Name = "Rabby Wallet",
        Chain = WalletChain.Evm,
        ExtIds = new[] { "acmacodkjbdgmoleebolmdjonilkdbch" },
        HomePage = "index.html",
        Flows = new Dictionary<string, WalletFlow>
        {
            [WalletFlow.Unlock] = new()
            {
                Name = WalletFlow.Unlock,
                OpensExtensionPage = true,
                Steps = new[]
                {
                    WaitFor("unlock password field", "input[type='password']", "#password"),
                    TypePassword("input[type='password']", "#password"),
                    Click("unlock button", "button[type='submit']", ".ant-btn-primary"),
                },
            },
            [WalletFlow.Connect] = new()
            {
                Name = WalletFlow.Connect,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    Click("connect", "button[type='submit']", ".ant-btn-primary"),
                },
            },
            [WalletFlow.Confirm] = new()
            {
                Name = WalletFlow.Confirm,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    WaitFor("confirm dialog", "button[type='submit']", ".ant-btn-primary"),
                    Click("sign / confirm", "button[type='submit']", ".ant-btn-primary"),
                },
            },
            [WalletFlow.Approve] = new()
            {
                Name = WalletFlow.Approve,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    Click("approve", "button[type='submit']", ".ant-btn-primary"),
                },
            },
        },
    };

    // ─── Backpack (Solana / EVM) ──────────────────────────────────────
    private static WalletDescriptor Backpack() => new()
    {
        Id = "backpack",
        Name = "Backpack",
        Chain = WalletChain.Multi,
        ExtIds = new[] { "aflkmfhebedbjioipglgcbcmnbpgliof" },
        HomePage = "popup.html",
        Flows = new Dictionary<string, WalletFlow>
        {
            [WalletFlow.Unlock] = new()
            {
                Name = WalletFlow.Unlock,
                OpensExtensionPage = true,
                Steps = new[]
                {
                    WaitFor("unlock password field", "input[type='password']", "[name='password']"),
                    TypePassword("input[type='password']", "[name='password']"),
                    Click("unlock button", "button[type='submit']"),
                },
            },
            [WalletFlow.Connect] = new()
            {
                Name = WalletFlow.Connect,
                OpensExtensionPage = false,
                Steps = new[] { Click("connect", "button[type='submit']") },
            },
            [WalletFlow.Confirm] = new()
            {
                Name = WalletFlow.Confirm,
                OpensExtensionPage = false,
                Steps = new[]
                {
                    WaitFor("confirm dialog", "button[type='submit']"),
                    Click("approve transaction", "button[type='submit']"),
                },
            },
            [WalletFlow.Approve] = new()
            {
                Name = WalletFlow.Approve,
                OpensExtensionPage = false,
                Steps = new[] { Click("approve", "button[type='submit']") },
            },
        },
    };
}
