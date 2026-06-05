// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>Overall stealth standing for a profile.</summary>
public enum StealthBand
{
    /// <summary>Detectable — coherence issues or a runtime leak.</summary>
    Weak,
    /// <summary>Decent, with caveats.</summary>
    Fair,
    /// <summary>Coherent fingerprint + clean runtime.</summary>
    Strong,
}

/// <summary>
/// Unified per-profile stealth score: the static fingerprint coherence
/// (computed offline from the device template) blended with the optional live
/// self-check (exit IP, WebRTC leak, runtime probe coherence). One number the
/// user can read before trusting a profile with sensitive work.
/// </summary>
public sealed record StealthReport(
    int Score,                        // 0-100
    StealthBand Band,
    int CoherenceScore,               // static fingerprint coherence
    int? RuntimeScore,                // live self-check, null if never run
    IReadOnlyList<string> TopIssues,
    string Summary);
