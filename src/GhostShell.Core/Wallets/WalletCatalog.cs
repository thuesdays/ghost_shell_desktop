// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;

namespace GhostShell.Core.Wallets;

/// <summary>
/// Per-wallet selector overrides the operator edits at runtime (no recompile).
/// Extension popup DOMs change often; this is how a broken selector gets fixed
/// in the field. Shape (persisted as JSON under settings key
/// <c>wallet_selector_overrides</c>):
/// <code>
/// { "metamask": {
///     "extIds": ["nkbihfbeogaeaoehlefnkodbefgpgknn"],
///     "extraSelectors": { "confirm": ["[data-testid='new-confirm']"] }
/// }}
/// </code>
/// <paramref name="ExtraSelectors"/> are PREPENDED to every step of the named
/// flow (tried first); irrelevant ones simply never match, so there is no
/// fragile step-index coupling.
/// </summary>
public sealed record WalletOverride
{
    public string[]? ExtIds { get; init; }
    public Dictionary<string, string[]>? ExtraSelectors { get; init; }
}

/// <summary>walletId → override.</summary>
public sealed class WalletOverrideSet : Dictionary<string, WalletOverride>
{
    public WalletOverrideSet() : base(StringComparer.OrdinalIgnoreCase) { }
}

/// <summary>
/// Phase 57 — resolves a wallet descriptor with operator overrides applied on
/// top of <see cref="CuratedWalletCatalog"/>. Pure (no I/O) so it's fully
/// unit-tested; the caller supplies the parsed overrides.
/// </summary>
public static class WalletCatalog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Parse the overrides JSON. Tolerant: bad/empty input → empty set.</summary>
    public static WalletOverrideSet ParseOverrides(string? json)
    {
        var set = new WalletOverrideSet();
        if (string.IsNullOrWhiteSpace(json)) return set;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, WalletOverride>>(json, JsonOpts);
            if (raw is not null)
                foreach (var kv in raw)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null)
                    {
                        // Audit M2: flow keys ("confirm" etc.) are matched case-
                        // insensitively at Apply time, so normalise the operator's
                        // ExtraSelectors dict to OrdinalIgnoreCase — otherwise
                        // "Confirm" would silently never match.
                        var ov = kv.Value;
                        if (ov.ExtraSelectors is { Count: > 0 })
                            ov = ov with { ExtraSelectors = new Dictionary<string, string[]>(ov.ExtraSelectors, StringComparer.OrdinalIgnoreCase) };
                        set[kv.Key] = ov;
                    }
        }
        catch (JsonException) { /* tolerant — keep empty set */ }
        return set;
    }

    /// <summary>Serialize an override set back to indented JSON (for the editor).</summary>
    public static string SerializeOverrides(WalletOverrideSet set)
        => JsonSerializer.Serialize(set, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Resolve <paramref name="walletId"/> against the curated catalog,
    /// applying <paramref name="overrides"/> if present. Null if unknown.</summary>
    public static WalletDescriptor? Resolve(string? walletId, WalletOverrideSet? overrides)
    {
        var baseDesc = CuratedWalletCatalog.TryGet(walletId);
        if (baseDesc is null) return null;
        if (overrides is null || walletId is null || !overrides.TryGetValue(walletId, out var ov) || ov is null)
            return baseDesc;
        return Apply(baseDesc, ov);
    }

    /// <summary>Apply an override to a descriptor, returning a new merged copy.</summary>
    public static WalletDescriptor Apply(WalletDescriptor baseDesc, WalletOverride ov)
    {
        var extIds = ov.ExtIds is { Length: > 0 } ? ov.ExtIds : baseDesc.ExtIds;

        var flows = new Dictionary<string, WalletFlow>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in baseDesc.Flows)
        {
            var flow = kv.Value;
            string[]? extra = null;
            ov.ExtraSelectors?.TryGetValue(kv.Key, out extra);

            if (extra is { Length: > 0 })
            {
                var newSteps = new WalletFlowStep[flow.Steps.Length];
                for (var i = 0; i < flow.Steps.Length; i++)
                {
                    var step = flow.Steps[i];
                    // Prepend extras (tried first), de-dup, only for selector-driven steps.
                    if (step.Action is WalletAction.WaitFor or WalletAction.Click or WalletAction.Type)
                    {
                        var merged = extra.Concat(step.AnyOfSelectors)
                                          .Distinct(StringComparer.Ordinal)
                                          .ToArray();
                        newSteps[i] = step with { AnyOfSelectors = merged };
                    }
                    else newSteps[i] = step;
                }
                flow = flow with { Steps = newSteps };
            }
            flows[kv.Key] = flow;
        }

        return baseDesc with { ExtIds = extIds, Flows = flows };
    }
}
