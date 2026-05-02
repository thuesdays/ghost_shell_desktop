// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>Phase 28 — one (profile, domain, hour) row in traffic_stats.</summary>
public sealed record TrafficSample
{
    public string ProfileName { get; init; } = "";
    public string Domain      { get; init; } = "";
    public string HourBucket  { get; init; } = "";  // YYYY-MM-DD HH local
    public long   Bytes       { get; init; }
    public long   ReqCount    { get; init; }
    public long?  RunId       { get; init; }
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>Phase 28 — single increment we want to MERGE into the table
/// on the next flush. The collector accumulates these in memory then
/// hands a batch to <c>ITrafficService.WriteSamplesAsync</c>.</summary>
public sealed record TrafficDelta
{
    public string ProfileName { get; init; } = "";
    public string Domain      { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.Now; // local — used to derive bucket
    public long   Bytes       { get; init; }
    public long   ReqCount    { get; init; }
    public long?  RunId       { get; init; }
}

/// <summary>Phase 28 — total + per-time-step roll-up returned by the
/// summary endpoint. Powers the dashboard stat-bar + line chart.</summary>
public sealed record TrafficSummary
{
    public long TotalBytes    { get; init; }
    public long TotalRequests { get; init; }
    public int  ProfileCount  { get; init; }
    public int  DomainCount   { get; init; }
    public IReadOnlyList<TrafficTimePoint> Timeseries { get; init; } = Array.Empty<TrafficTimePoint>();
}

public sealed record TrafficTimePoint
{
    /// <summary>Bucket label, "YYYY-MM-DD HH" (hour bucket) or "YYYY-MM-DD" (day bucket).</summary>
    public string Time { get; init; } = "";
    public long Bytes    { get; init; }
    public long Requests { get; init; }
}

public sealed record TrafficByProfile
{
    public string ProfileName { get; init; } = "";
    public long   Bytes        { get; init; }
    public long   Requests     { get; init; }
    public int    DomainCount  { get; init; }
}

public sealed record TrafficByDomain
{
    public string Domain      { get; init; } = "";
    public long   Bytes        { get; init; }
    public long   Requests     { get; init; }
    public int    ProfileCount { get; init; }
}
