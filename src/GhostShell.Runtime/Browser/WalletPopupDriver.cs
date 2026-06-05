// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using GhostShell.Core.Services;
using GhostShell.Core.Wallets;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Phase 56 — drives a browser-extension wallet popup (MetaMask, OKX, …)
/// through a named <see cref="WalletFlow"/> (unlock / connect / confirm /
/// approve). This is the foundation for crypto-farm mass actions: every
/// higher-level task (swap, claim, mint) ultimately needs the wallet popup
/// confirmed, and that is exactly what this class does — reliably, across
/// many profiles.
///
/// <para><b>Why a dedicated driver?</b> A wallet's transaction-confirmation
/// dialog is a SEPARATE browser window (<c>notification.html</c>) the wallet
/// spawns when the dApp calls <c>eth_sendTransaction</c>. The page's own
/// <c>document.querySelector</c> can't see it. We must enumerate window
/// handles, switch into the wallet window, act with TRUSTED input
/// (<c>isTrusted:true</c> via CDP), then switch back so the user's tab isn't
/// yanked. The unlock flow is different: the toolbar action-popup isn't a
/// Selenium-addressable window, so we open the wallet's full page as a tab
/// (and close it again when done).</para>
///
/// Stateless — no mutable instance fields, safe to share across runs.
/// </summary>
public sealed class WalletPopupDriver
{
    /// <summary>
    /// Run <paramref name="flowName"/> for <paramref name="wallet"/> against the
    /// live <paramref name="session"/>. <paramref name="resolveValue"/> turns a
    /// step's placeholder (e.g. <c>{{vault.PASS}}</c>) into cleartext — the
    /// caller wires this to the script engine's vault-aware interpolation so
    /// secrets never live in this class.
    /// </summary>
    public async Task RunFlowAsync(
        IBrowserSession session,
        WalletDescriptor wallet,
        string flowName,
        Func<string, string> resolveValue,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        var flow = wallet.GetFlow(flowName)
            ?? throw new WalletFlowException($"wallet '{wallet.Id}' has no '{flowName}' flow");

        // Remember where the user was so we ALWAYS return there — and track a
        // tab WE opened (unlock) so we can close it instead of leaking it.
        var origin = await SafeCurrentHandleAsync(session, ct);
        string? openedTab = null;

        try
        {
            string? target;
            if (flow.OpensExtensionPage)
            {
                openedTab = await OpenExtensionTabAsync(session, wallet, ct);
                target = openedTab;
            }
            else
            {
                var firstTimeout = flow.Steps.Length > 0 ? flow.Steps[0].TimeoutMs : 15_000;
                target = await FindWalletWindowAsync(
                    session, wallet, origin,
                    TimeSpan.FromMilliseconds(Math.Max(5_000, firstTimeout)), log, ct);
            }

            if (target is null)
                throw new WalletFlowException(
                    $"{wallet.Name}: could not find the wallet window for '{flowName}' " +
                    "(no extension popup appeared — did the dApp trigger the request?)");

            await session.SwitchToWindowAsync(target, ct);
            log?.LogInformation("Wallet {Wallet}: running '{Flow}' ({Steps} steps)",
                wallet.Name, flowName, flow.Steps.Length);

            for (var i = 0; i < flow.Steps.Length; i++)
                await RunStepAsync(session, wallet, flow.Steps[i], i + 1, resolveValue, log, ct);
        }
        finally
        {
            // Close the tab we opened for unlock (don't leak one per run).
            if (!string.IsNullOrEmpty(openedTab))
            {
                try
                {
                    await session.SwitchToWindowAsync(openedTab, ct);
                    await session.ExecuteScriptAsync("window.close();", null, ct);
                }
                catch (Exception ex) { log?.LogDebug(ex, "wallet driver: closing unlock tab failed (harmless)"); }
            }
            // Restore the user's original tab on EVERY path (success, no-match,
            // mid-flow throw). Tolerate a vanished handle (confirm windows self-close).
            if (!string.IsNullOrEmpty(origin))
            {
                try { await session.SwitchToWindowAsync(origin, ct); }
                catch (Exception ex) { log?.LogDebug(ex, "wallet driver: restore origin tab failed (harmless)"); }
            }
        }
    }

    // ─── window acquisition ───────────────────────────────────────────

    /// <summary>Open the wallet's full page as a new tab (unlock flow) and
    /// switch to it. Returns the new handle, or null if it never appeared.</summary>
    private static async Task<string?> OpenExtensionTabAsync(
        IBrowserSession session, WalletDescriptor wallet, CancellationToken ct)
    {
        var url = $"chrome-extension://{wallet.ExtIds[0]}/{wallet.HomePage}";
        var before = new HashSet<string>(await session.GetWindowHandlesAsync(ct));
        // Web content can't window.open() a chrome-extension:// URL, but the
        // driver CAN navigate to one — so open a blank tab, then navigate it.
        await session.ExecuteScriptAsync("window.open('about:blank','_blank');", null, ct);

        var newHandle = await PollForNewHandleAsync(session, before, TimeSpan.FromSeconds(8), ct);
        if (newHandle is null) return null;
        await session.SwitchToWindowAsync(newHandle, ct);
        await session.NavigateAsync(url, ct);
        return newHandle;
    }

    private static async Task<string?> PollForNewHandleAsync(
        IBrowserSession session, HashSet<string> before, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var h in await session.GetWindowHandlesAsync(ct))
                if (!before.Contains(h)) return h;
            await Task.Delay(200, ct);
        }
        return null;
    }

    /// <summary>
    /// Find the wallet's confirmation popup window. Probes each handle AT MOST
    /// ONCE (cached by handle) so we don't flicker the user's tabs every tick,
    /// and prefers a notification/popup page over a plain home page (so a
    /// lingering unlock tab can't be mistaken for the confirm window).
    /// </summary>
    private static async Task<string?> FindWalletWindowAsync(
        IBrowserSession session, WalletDescriptor wallet, string? origin,
        TimeSpan budget, ILogger? log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        var probed = new HashSet<string>();   // handles we've already inspected
        string? fallback = null;              // a wallet page that isn't a notification

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var h in await session.GetWindowHandlesAsync(ct))
            {
                if (h == origin || probed.Contains(h)) continue;
                probed.Add(h);
                try
                {
                    await session.SwitchToWindowAsync(h, ct);
                    var url = (await session.ExecuteScriptAsync("return location.href;", null, ct))?.ToString();
                    if (!wallet.MatchesPopupUrl(url)) continue;

                    // Prefer the transaction notification window; keep a home/
                    // popup page only as a fallback if nothing better appears.
                    if (LooksLikeNotification(url))
                    {
                        log?.LogDebug("Wallet {Wallet}: matched popup window {Url}", wallet.Name, url);
                        return h;
                    }
                    fallback ??= h;
                }
                catch (Exception ex)
                {
                    log?.LogTrace(ex, "wallet driver: probe window {Handle} failed", h);
                }
            }
            if (fallback is not null) return fallback;
            await Task.Delay(300, ct);
        }
        return fallback;
    }

    private static bool LooksLikeNotification(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        var u = url.ToLowerInvariant();
        return u.Contains("notification") || u.Contains("confirm") ||
               u.Contains("approve") || u.Contains("request") || u.Contains("popup");
    }

    // ─── step execution ───────────────────────────────────────────────

    private static async Task RunStepAsync(
        IBrowserSession session, WalletDescriptor wallet, WalletFlowStep step,
        int ordinal, Func<string, string> resolveValue, ILogger? log, CancellationToken ct)
    {
        switch (step.Action)
        {
            case WalletAction.Delay:
                await Task.Delay(Math.Max(0, step.DelayMs), ct);
                return;

            case WalletAction.Press:
                await session.TrustedPressKeyAsync(step.Key ?? "Enter", ct);
                if (step.DelayMs > 0) await Task.Delay(step.DelayMs, ct);
                return;

            case WalletAction.WaitFor:
            {
                var hit = await WaitForFirstVisibleAsync(session, step.AnyOfSelectors, step.TimeoutMs, ct);
                if (hit is null && !step.Optional)
                    throw new WalletFlowException(
                        $"{wallet.Name} step {ordinal} ({step.Description}): none of " +
                        $"[{string.Join(", ", step.AnyOfSelectors)}] appeared in {step.TimeoutMs}ms");
                return;
            }

            case WalletAction.Click:
            {
                var sel = await WaitForFirstVisibleAsync(session, step.AnyOfSelectors, step.TimeoutMs, ct);
                if (sel is null)
                {
                    if (step.Optional) { log?.LogDebug("Wallet {Wallet} step {N}: optional click skipped ({Desc})", wallet.Name, ordinal, step.Description); return; }
                    throw new WalletFlowException(
                        $"{wallet.Name} step {ordinal} ({step.Description}): no clickable element matched");
                }
                await session.TrustedClickAsync(sel, 1, "left", ct);
                if (step.DelayMs > 0) await Task.Delay(step.DelayMs, ct);
                return;
            }

            case WalletAction.Type:
            {
                var sel = await WaitForFirstVisibleAsync(session, step.AnyOfSelectors, step.TimeoutMs, ct);
                if (sel is null)
                {
                    if (step.Optional) return;
                    throw new WalletFlowException(
                        $"{wallet.Name} step {ordinal} ({step.Description}): no input matched");
                }
                var value = resolveValue(step.Value ?? "");
                // Fail-closed: never type an unresolved placeholder (e.g. a wallet
                // with no bound password) — that would feed the literal
                // "{{vault.PASS}}" as a password and lock the wallet after retries.
                if (value.Contains("{{"))
                    throw new WalletFlowException(
                        $"{wallet.Name} step {ordinal} ({step.Description}): value did not resolve " +
                        $"('{step.Value}'). Bind a crypto_wallet vault item with a wallet_password to this " +
                        "profile, or pass an explicit 'password' param.");
                await session.TrustedTypeAsync(sel, value, 40, 150, ct);
                if (step.DelayMs > 0) await Task.Delay(step.DelayMs, ct);
                return;
            }

            default:
                throw new WalletFlowException($"unknown wallet action {step.Action}");
        }
    }

    /// <summary>Poll until one of <paramref name="candidates"/> is present +
    /// has a non-zero box; return that selector, or null on timeout.</summary>
    private static async Task<string?> WaitForFirstVisibleAsync(
        IBrowserSession session, string[] candidates, int timeoutMs, CancellationToken ct)
    {
        if (candidates.Length == 0) return null;
        var json = JsonSerializer.Serialize(candidates);
        var js = $$"""
            return (function(){
              var sels = {{json}};
              for (var i = 0; i < sels.length; i++) {
                var el = document.querySelector(sels[i]);
                if (el) {
                  var r = el.getBoundingClientRect();
                  if (r.width > 0 && r.height > 0) return sels[i];
                }
              }
              return null;
            })();
        """;
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(250, timeoutMs));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var hit = await session.ExecuteScriptAsync(js, null, ct);
            if (hit is string s && !string.IsNullOrEmpty(s)) return s;
            await Task.Delay(250, ct);
        }
        return null;
    }

    private static async Task<string?> SafeCurrentHandleAsync(IBrowserSession session, CancellationToken ct)
    {
        try { return await session.GetCurrentWindowHandleAsync(ct); }
        catch { return null; }
    }
}
