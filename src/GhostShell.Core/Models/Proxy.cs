// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// One proxy endpoint. Source of truth is the URL (scheme + creds +
/// host + port). Diagnostic state — country, latency, ip type — is
/// populated by <see cref="GhostShell.Core.Services.IProxyTester"/>
/// runs and read-only from the form's perspective.
/// </summary>
public sealed class Proxy
{
    public required string Slug { get; init; }
    public string? Name { get; init; }
    public required string Url { get; init; }

    public bool IsRotating { get; init; }
    public string? RotationApiUrl { get; init; }
    public string? RotationProvider { get; init; }
    public string? RotationApiKey { get; init; }

    public bool IsDefault { get; init; }
    public string? Notes { get; init; }

    // ─── Diagnostics state (last test result) ───
    public string? LastIp { get; init; }
    public string? Country { get; init; }
    public string? CountryCode { get; init; }
    public string? City { get; init; }
    public string? Asn { get; init; }
    public string? Isp { get; init; }
    public IpType IpType { get; init; } = IpType.Unknown;
    public int? LatencyMs { get; init; }
    public ProxyHealth Health { get; init; } = ProxyHealth.Unknown;
    public DateTime? LastCheckedAt { get; init; }

    /// <summary>Number of profiles currently bound to this proxy.</summary>
    public int ProfileCount { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public enum ProxyHealth
{
    Unknown,
    Healthy,
    Warning,
    Critical,
}

/// <summary>
/// IP-type classification used by detection sites. Datacenter IPs
/// look "burned" to most fingerprint trackers; residential / mobile
/// get a free pass. We persist the diagnostic to colour-code the
/// table without re-probing.
/// </summary>
public enum IpType
{
    Unknown,
    Datacenter,
    Residential,
    Mobile,
}
