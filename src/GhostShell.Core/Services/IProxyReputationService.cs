// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Scores a proxy's IP reputation (network-layer "burned-ness") so the
/// launch flow can warn or block before a burned IP wastes a run, and the
/// UI can colour-code the proxy table. Pure heuristic scoring is
/// synchronous and deterministic; <see cref="EvaluateAsync"/> additionally
/// folds in an optional external fraud-score provider.
/// </summary>
public interface IProxyReputationService
{
    /// <summary>Deterministic score from already-probed proxy fields
    /// (<see cref="Proxy.IpType"/>, <see cref="Proxy.Isp"/>,
    /// <see cref="Proxy.Asn"/>, <see cref="Proxy.Health"/>). No I/O.</summary>
    ProxyReputationReport Evaluate(Proxy proxy);

    /// <summary>Same as <see cref="Evaluate"/>, then blends in an external
    /// fraud-score provider when one is configured. Falls back to the pure
    /// heuristic if the provider is disabled or errors.</summary>
    Task<ProxyReputationReport> EvaluateAsync(Proxy proxy, CancellationToken ct = default);
}

/// <summary>
/// Pluggable external IP-reputation source (e.g. IPQualityScore,
/// Scamalytics). Implementations are no-ops unless an API key is
/// configured, so the product runs fully without one.
/// </summary>
public interface IIpReputationProvider
{
    /// <summary>True only when the provider has the config it needs
    /// (API key) to make a real call. When false, callers skip it.</summary>
    bool IsEnabled { get; }

    /// <summary>Look up an IP. Returns null when disabled, on error, or on a
    /// non-conclusive answer — callers must treat null as "no signal".</summary>
    Task<ExternalReputation?> LookupAsync(string ip, CancellationToken ct = default);
}

/// <summary>Normalised answer from an external reputation provider.</summary>
public sealed record ExternalReputation(
    int FraudScore,   // 0 (clean) … 100 (fraudulent)
    bool IsProxy,
    bool IsVpn,
    string? Source);
