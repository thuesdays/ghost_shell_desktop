// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Read/regenerate a profile's active fingerprint and run coherence
/// checks against it. The actual payload is regenerated on every
/// browser launch (it's deterministic from profile name + salt) — this
/// service computes the same payload at any time so the UI can score
/// it without launching a browser.
///
/// Regenerate: bump the per-profile <c>fp_regen_salt</c> column to a
/// fresh value. Next launch + every coherence-check call uses the new
/// salt, producing a fresh-but-still-deterministic fingerprint.
///
/// Reshuffle: keep the salt, just re-roll the noise seeds (a more
/// targeted change — useful when a particular detector flagged the
/// canvas hash but the rest of the profile is fine).
/// </summary>
public interface IFingerprintService
{
    /// <summary>
    /// Compute the current fingerprint score for a profile. Builds the
    /// deterministic payload from current profile state, runs
    /// coherence checks, returns aggregated score + sub-scores +
    /// per-check results.
    /// </summary>
    Task<FingerprintScore> GetScoreAsync(string profileName, CancellationToken ct = default);

    /// <summary>Regenerate by bumping the fingerprint regen salt.
    /// Returns the fresh score.</summary>
    Task<FingerprintScore> RegenerateAsync(string profileName, CancellationToken ct = default);

    /// <summary>Reshuffle the noise seeds without regenerating the
    /// rest of the payload. Updates a separate noise-salt column so
    /// the canvas/WebGL/audio jitter changes but UA/hardware/etc.
    /// stay the same.</summary>
    Task<FingerprintScore> ReshuffleAsync(string profileName, CancellationToken ct = default);

    /// <summary>
    /// Persist the current fingerprint as an audit row. Useful for the
    /// History tab on the FP page so users can see how their score has
    /// trended over time.
    /// </summary>
    Task LogAuditAsync(string profileName, int score, string templateId, CancellationToken ct = default);

    /// <summary>Recent audit rows for the profile (newest first).</summary>
    Task<IReadOnlyList<FingerprintAuditEntry>> ListAuditsAsync(
        string profileName, int limit = 50, CancellationToken ct = default);
}

public sealed record FingerprintAuditEntry(
    long Id,
    string ProfileName,
    DateTime GeneratedAt,
    int Score,
    string TemplateId,
    string? Note);

/// <summary>
/// Persistence layer for fingerprint salts + audit log. Split from
/// <see cref="IFingerprintService"/> the same way IWarmupHistoryService
/// is split from IWarmupService — interface here in Core so Runtime
/// can call it without taking a reference on Data.
/// </summary>
public interface IFingerprintAuditService
{
    /// <summary>Update <c>profiles.fp_regen_salt</c>.</summary>
    Task SetRegenSaltAsync(string profileName, string salt, CancellationToken ct = default);

    /// <summary>Update <c>profiles.fp_noise_salt</c>.</summary>
    Task SetNoiseSaltAsync(string profileName, string salt, CancellationToken ct = default);

    /// <summary>Append a row to <c>fingerprint_audits</c>.</summary>
    Task LogAsync(string profileName, int score, string templateId,
                  string? note = null, CancellationToken ct = default);

    /// <summary>Recent audit rows for a profile (newest first).</summary>
    Task<IReadOnlyList<FingerprintAuditEntry>> ListAsync(
        string profileName, int limit = 50, CancellationToken ct = default);
}
