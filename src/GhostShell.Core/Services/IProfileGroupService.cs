// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// CRUD over profile groups + their member sets. Mirrors the legacy
/// web's <c>/api/groups</c> endpoints. Group-level start/stop is NOT
/// in this surface — it lives on <see cref="IProfileRunner"/> as a
/// loop over the member names so the runner stays the single source
/// of truth for "is profile X live".
/// </summary>
public interface IProfileGroupService
{
    /// <summary>
    /// All groups, MemberCount populated, Members empty.
    /// Sorted alphabetically by Name (matches the legacy UI).
    /// </summary>
    Task<IReadOnlyList<ProfileGroup>> ListAsync(CancellationToken ct = default);

    /// <summary>One group with full Members list materialized.</summary>
    Task<ProfileGroup?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Create a new group + assign initial members in one
    /// transaction. Returns the persisted ProfileGroup with Id set.</summary>
    Task<ProfileGroup> CreateAsync(
        string name,
        string? description,
        int? maxParallel,
        IReadOnlyList<string> members,
        CancellationToken ct = default);

    /// <summary>Update name / description / cap. Members are NOT
    /// touched here — call <see cref="SetMembersAsync"/>
    /// separately for that.</summary>
    Task UpdateAsync(
        long id,
        string name,
        string? description,
        int? maxParallel,
        CancellationToken ct = default);

    /// <summary>Replace the group's member set in one transaction.
    /// Diff-based: rows added/removed as needed, no DELETE-ALL +
    /// INSERT-ALL churn.</summary>
    Task SetMembersAsync(
        long id,
        IReadOnlyList<string> members,
        CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);
}
