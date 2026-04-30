// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>One coherence-validator check result.</summary>
public sealed record FingerprintCheck
{
    public required string Id          { get; init; }
    public required string Title       { get; init; }
    public required string Detail      { get; init; }
    public required FingerprintCheckStatus Status { get; init; }
    public required FingerprintCheckSeverity Severity { get; init; }
}

public enum FingerprintCheckStatus
{
    Pass,
    Warn,
    Fail,
    /// <summary>Not applicable / not implemented yet.</summary>
    Skip,
}

public enum FingerprintCheckSeverity
{
    /// <summary>Bot-blocking signal — sites WILL detect.</summary>
    Critical,
    /// <summary>Adds risk — heuristic detectors may flag.</summary>
    Warning,
    /// <summary>Informational only.</summary>
    Info,
}

/// <summary>
/// Aggregated fingerprint score for a profile. Mirrors the legacy web's
/// scorecard surface — overall score 0-100, four sub-scores by category,
/// list of individual check results.
/// </summary>
public sealed record FingerprintScore
{
    /// <summary>Overall 0-100. ≥85 EXCELLENT, ≥75 OK, <75 RISKY.</summary>
    public required int Overall { get; init; }
    public required string Label { get; init; } // "EXCELLENT" / "OK" / "RISKY"

    public int Identity   { get; init; }
    public int Hardware   { get; init; }
    public int Network    { get; init; }
    public int Automation { get; init; }

    public int CriticalIssues { get; init; }
    public int Warnings       { get; init; }

    public required IReadOnlyList<FingerprintCheck> Checks { get; init; }
}
