// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Wallets;

/// <summary>
/// Phase 56 — crypto-wallet automation primitives. These describe HOW to
/// drive a browser-extension wallet's popup (MetaMask, OKX, Phantom, …)
/// through the three farm-critical flows: <c>unlock</c>, <c>connect</c>,
/// and <c>confirm</c> (plus <c>approve</c>).
///
/// The whole point is <b>config over code</b>: extension popup DOMs change
/// almost every release, so the selectors live here as data with multiple
/// fallback candidates per step rather than as hardcoded C# branches. When a
/// wallet ships a UI change the fix is a selector edit, not a recompile.
/// </summary>
public enum WalletAction
{
    /// <summary>Wait until one of the step's selectors is present + visible.</summary>
    WaitFor,
    /// <summary>Trusted-click the first visible candidate selector.</summary>
    Click,
    /// <summary>Trusted-type <see cref="WalletFlowStep.Value"/> into the first visible candidate.</summary>
    Type,
    /// <summary>Press a single key (<see cref="WalletFlowStep.Key"/>) against the focused element.</summary>
    Press,
    /// <summary>Sleep <see cref="WalletFlowStep.DelayMs"/> (settle / animation).</summary>
    Delay,
}

/// <summary>Chain family — drives which vault fields / tx model apply.</summary>
public enum WalletChain
{
    Evm,
    Solana,
    Multi,
}

/// <summary>
/// One atomic instruction in a wallet flow. A step lists SEVERAL candidate
/// CSS selectors (<see cref="AnyOfSelectors"/>); the driver acts on the first
/// one that is present + visible. <see cref="Optional"/> steps that match
/// nothing are skipped instead of failing the flow (e.g. a "Next" page that
/// only appears on first connect).
/// </summary>
public sealed record WalletFlowStep
{
    public required WalletAction Action { get; init; }

    /// <summary>Candidate CSS selectors, tried in order. First visible wins.</summary>
    public string[] AnyOfSelectors { get; init; } = System.Array.Empty<string>();

    /// <summary>For <see cref="WalletAction.Type"/> — literal text or a
    /// placeholder such as <c>{{vault.PASS}}</c> resolved by the caller.</summary>
    public string? Value { get; init; }

    /// <summary>For <see cref="WalletAction.Press"/> — e.g. <c>Enter</c>.</summary>
    public string? Key { get; init; }

    /// <summary>Per-step wait budget in ms (default 15 s).</summary>
    public int TimeoutMs { get; init; } = 15_000;

    /// <summary>A miss is skipped, not an error.</summary>
    public bool Optional { get; init; }

    /// <summary>For <see cref="WalletAction.Delay"/>, or extra settle after the action.</summary>
    public int DelayMs { get; init; }

    /// <summary>Human-readable label used in logs.</summary>
    public string Description { get; init; } = "";

    /// <summary>True when the step is structurally usable (has selectors for
    /// selector-driven actions, a key for Press, or a delay for Delay).</summary>
    public bool IsWellFormed => Action switch
    {
        WalletAction.WaitFor => AnyOfSelectors.Length > 0,
        WalletAction.Click   => AnyOfSelectors.Length > 0,
        WalletAction.Type    => AnyOfSelectors.Length > 0 && !string.IsNullOrEmpty(Value),
        WalletAction.Press   => !string.IsNullOrEmpty(Key),
        WalletAction.Delay   => DelayMs > 0,
        _ => false,
    };
}

/// <summary>A named sequence of <see cref="WalletFlowStep"/>s.</summary>
public sealed record WalletFlow
{
    public required string Name { get; init; }
    public required WalletFlowStep[] Steps { get; init; }

    /// <summary>
    /// True for flows that must OPEN the wallet's full page as a tab first
    /// (unlock) — the toolbar action-popup isn't a Selenium-addressable
    /// window. False for flows that ATTACH to a wallet-spawned popup window
    /// (connect/confirm/approve), which IS enumerated in WindowHandles.
    /// </summary>
    public bool OpensExtensionPage { get; init; }

    /// <summary>Flow names the catalog uses as conventional keys.</summary>
    public const string Unlock  = "unlock";
    public const string Connect = "connect";
    public const string Confirm = "confirm";
    public const string Approve = "approve";
}

/// <summary>
/// A wallet extension and how to drive its farm-critical flows. Identified by
/// one or more 32-char Chrome extension IDs so a popup window can be matched
/// by its <c>chrome-extension://&lt;id&gt;/…</c> URL.
/// </summary>
public sealed record WalletDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public WalletChain Chain { get; init; } = WalletChain.Evm;

    /// <summary>Known Chrome Web Store IDs for this wallet (popup-URL match).</summary>
    public required string[] ExtIds { get; init; }

    /// <summary>Extension page opened (as a tab) for the unlock flow.</summary>
    public string HomePage { get; init; } = "home.html";

    /// <summary>Flows keyed by name (unlock/connect/confirm/approve).</summary>
    public required IReadOnlyDictionary<string, WalletFlow> Flows { get; init; }

    /// <summary>True when <paramref name="url"/> is a page served by this
    /// wallet's extension (any of <see cref="ExtIds"/>).</summary>
    public bool MatchesPopupUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        foreach (var id in ExtIds)
        {
            if (url.StartsWith($"chrome-extension://{id}/", System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public WalletFlow? GetFlow(string name)
        => Flows.TryGetValue(name, out var f) ? f : null;
}

/// <summary>Thrown when a required wallet flow step finds nothing to act on.</summary>
public sealed class WalletFlowException : System.Exception
{
    public WalletFlowException(string message) : base(message) { }
}
