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

    /// <summary>
    /// Expanded per-probe test results serialised to JSON. Contains
    /// an array of <see cref="SelfCheckTestResult"/> objects, one for
    /// each named probe (navigator.userAgent, screen.width, etc.).
    /// Renders as individual test cards in the UI. Defaults to "[]"
    /// for backwards compatibility with older runs.
    /// </summary>
    public string TestsJson { get; init; } = "[]";
}

/// <summary>
/// A single named probe test within a self-check result. Each test has
/// an expected value (from the DeviceTemplateBuilder payload), an
/// actual value (from live JS execution), and a status (pass/warn/fail/skip).
/// The UI renders these as individual cards with color-coded status
/// indicators, category icons, and diff views.
/// </summary>
public sealed record SelfCheckTestResult
{
    /// <summary>
    /// Unique probe identifier, e.g. "navigator.userAgent", "screen.width".
    /// Used as the test's primary key and for stable ordering.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable label for the probe, e.g. "User-Agent", "Screen Width".
    /// Rendered in the test card header.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Category grouping for related probes. One of: "navigator", "screen",
    /// "timezone", "webgl", "canvas", "audio", "fonts", "network", "automation".
    /// Used to assign category icons and for filtering/grouping in the UI.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Test outcome: "pass" (value matches expected), "warn" (close but
    /// not exact match), "fail" (value doesn't match), or "skip" (probe
    /// couldn't run). Determines the color of the status pill in the UI.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Severity level of the failure. One of: "critical", "important",
    /// "warning", "info". Critical tests are the most important to fix;
    /// info tests are optional refinements. Defaults to "info".
    /// </summary>
    public string Severity { get; init; } = "info";

    /// <summary>
    /// What the fingerprint payload (DeviceTemplateBuilder) says this
    /// probe should return. Null if the probe has no expected value.
    /// </summary>
    public string? Expected { get; init; }

    /// <summary>
    /// What the live browser actually reported when the probe ran.
    /// Null if the probe failed or returned no value.
    /// </summary>
    public string? Actual { get; init; }

    /// <summary>
    /// Extra explanation when status != "pass". Examples:
    /// "Mismatch: user-agent claims Chrome 135 but browser reports 134",
    /// "Canvas rendering failed: SecurityError",
    /// "WebGL not available". Shown as dimmed subtext under the test label.
    /// </summary>
    public string? Detail { get; init; }
}
