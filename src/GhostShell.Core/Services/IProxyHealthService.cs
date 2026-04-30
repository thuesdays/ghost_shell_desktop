// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Reads and writes the proxy_health_events log. The log feeds the
/// timeline widget and the per-row "captchas / burns" counters in
/// the proxy table.
/// </summary>
public interface IProxyHealthService
{
    /// <summary>
    /// Events on or after <paramref name="since"/>, oldest first.
    /// Pass <c>null</c> for the last 7 days (the default the
    /// timeline widget uses).
    /// </summary>
    Task<IReadOnlyList<ProxyHealthEvent>> ListAsync(
        DateTime? since = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ProxyHealthEvent>> ListForProxyAsync(
        string proxySlug,
        DateTime? since = null,
        CancellationToken ct = default);

    Task RecordAsync(ProxyHealthEvent ev, CancellationToken ct = default);

    /// <summary>
    /// Aggregated counters per proxy (captchas, rotations, burns)
    /// since <paramref name="since"/>. Useful for the table's
    /// "captchas" column without N+1 queries.
    /// </summary>
    Task<IReadOnlyDictionary<string, ProxyHealthCounters>> CountersAsync(
        DateTime? since = null,
        CancellationToken ct = default);
}

public sealed record ProxyHealthCounters(
    int Rotations,
    int Captchas,
    int Burns,
    int FirstSeen);
