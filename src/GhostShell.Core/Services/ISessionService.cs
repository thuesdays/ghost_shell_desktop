// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Persistence for per-profile session snapshots. Mirrors the
/// legacy Python <c>cookie_pool</c> + <c>db.snapshot_*</c> APIs.
///
/// Snapshots are append-mostly: created automatically by
/// <see cref="SaveAsync"/> at end-of-run (when the runner detects a
/// clean exit) or manually from the Sessions page; deleted only by
/// the user via <see cref="DeleteAsync"/>. Profile deletion does
/// not cascade — snapshots stay around as a recovery resource.
///
/// Reads are split into a metadata-only <see cref="ListAsync"/>
/// (cheap; powers the Sessions list page) and a payload-fetching
/// <see cref="GetPayloadAsync"/> that's only called when the user
/// clicks Restore. JSON payloads can run to tens of KB; not paying
/// that cost for every list refresh is the point.
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Create a new snapshot row from a captured payload. Returns
    /// the assigned id. Empty payloads are still persisted so the
    /// list shows "saved (empty)" honestly — the runtime decides
    /// whether to skip the call entirely.
    /// </summary>
    Task<long> SaveAsync(
        string profileName,
        SessionPayload payload,
        long? runId,
        string trigger,
        string? reason = null,
        CancellationToken ct = default);

    /// <summary>
    /// Recent snapshots for one profile, newest first. Pass null
    /// <paramref name="profileName"/> for an all-profiles view.
    /// </summary>
    Task<IReadOnlyList<SessionSnapshot>> ListAsync(
        string? profileName = null, int limit = 100,
        CancellationToken ct = default);

    Task<SessionSnapshot?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Load the full cookie + storage payload for a snapshot.
    /// Returns null when the row was deleted between list and
    /// fetch (UI race; harmless).
    /// </summary>
    Task<SessionPayload?> GetPayloadAsync(long id, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Convenience: return the most recent snapshot for a profile,
    /// or null. Used by the runtime's auto-restore path.
    /// </summary>
    Task<SessionSnapshot?> GetLatestAsync(
        string profileName, CancellationToken ct = default);
}
