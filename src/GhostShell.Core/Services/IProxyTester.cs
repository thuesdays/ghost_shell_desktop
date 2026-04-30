// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Probes a proxy and returns latency + geo / IP-type classification.
/// Phase 2 ships a stub implementation in GhostShell.Runtime that
/// fakes results for UI testing; Phase 3 will replace with real
/// HTTP probes (ip-api.com / ipinfo / ipapi.co + own latency check).
/// </summary>
public interface IProxyTester
{
    Task<ProxyTestResult> TestAsync(Proxy proxy, CancellationToken ct = default);
}

/// <summary>
/// Outcome of one probe. <see cref="Ok"/> false means the probe
/// failed altogether (timeout, connection refused, auth rejected).
/// Even on Ok, individual fields may be null if the geo lookup
/// was skipped or rate-limited.
/// </summary>
public sealed class ProxyTestResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }

    public string? Ip { get; init; }
    public string? Country { get; init; }
    public string? CountryCode { get; init; }
    public string? City { get; init; }
    public string? Asn { get; init; }
    public string? Isp { get; init; }

    public IpType IpType { get; init; } = IpType.Unknown;
    public int? LatencyMs { get; init; }

    public DateTime At { get; init; } = DateTime.UtcNow;
}
