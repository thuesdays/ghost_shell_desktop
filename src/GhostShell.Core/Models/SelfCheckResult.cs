// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Outcome of a single network-layer self-check probe. Materialised
/// per profile launch (or per Nth launch if cadence is configured).
/// Mirrors the legacy Python web's <c>self_check</c> result row.
/// </summary>
public sealed record SelfCheckResult
{
    public long Id { get; init; }
    public required string ProfileName { get; init; }
    public long? RunId { get; init; }
    public required DateTime RanAt { get; init; }

    public string? ExitIp     { get; init; }
    public string? GeoCountry { get; init; }
    public string? GeoCity    { get; init; }

    public bool   WebRtcLeaked  { get; init; }
    public string? WebRtcLocalIp { get; init; }

    public string? TimezoneActual   { get; init; }
    public string? TimezoneExpected { get; init; }

    public string? UaActual { get; init; }

    /// <summary>0-100 health score derived from the probe results.</summary>
    public int Score { get; init; }

    /// <summary>Free-form note (failure mode, partial probe, etc.).</summary>
    public string? Note { get; init; }

    /// <summary>Raw probe payload as JSON for debugging.</summary>
    public string? RawJson { get; init; }
}
