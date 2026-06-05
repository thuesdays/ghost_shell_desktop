// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// How "burned" a proxy IP looks to network-level anti-bot systems
/// (Google's "this network is blocked" page, Cloudflare's challenge,
/// etc.). This is the <b>network</b> layer of detection — independent of,
/// and evaluated BEFORE, the browser fingerprint. A pristine fingerprint
/// on a burned datacenter IP still gets walled.
/// </summary>
public enum ReputationBand
{
    /// <summary>Looks like a real residential/mobile user — safe to run.</summary>
    Clean,
    /// <summary>Mixed signals — usable, but expect occasional challenges.</summary>
    Suspect,
    /// <summary>Datacenter / flagged ASN / known-proxy — Google &amp; co. will
    /// challenge or hard-block. Don't use for sensitive targets.</summary>
    Burned,
}

/// <summary>
/// Result of scoring a <see cref="Proxy"/>'s IP reputation. Produced by
/// <see cref="GhostShell.Core.Services.IProxyReputationService"/> and consumed by
/// the launch gate (warn/block) and the UI (badge + tooltip).
/// </summary>
public sealed record ProxyReputationReport(
    int Score,
    ReputationBand Band,
    IpType IpType,
    IReadOnlyList<string> Reasons,
    string Recommendation)
{
    /// <summary>0 (pristine residential) … 100 (fully burned datacenter).</summary>
    public int Score { get; init; } = Score;
}
