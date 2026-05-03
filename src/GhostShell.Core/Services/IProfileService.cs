// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Read/write API for browser profiles. The Data project provides the
/// SQLite-backed implementation; the Runtime project will eventually
/// add launch-related calls on top of this.
/// </summary>
public interface IProfileService
{
    Task<IReadOnlyList<Profile>> ListAsync(CancellationToken ct = default);
    Task<Profile?> GetAsync(string name, CancellationToken ct = default);
    Task<Profile>  CreateAsync(Profile profile, CancellationToken ct = default);
    Task           UpdateAsync(Profile profile, CancellationToken ct = default);
    Task           DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Phase 59 — atomic increment of <c>run_count</c> + bump of
    /// <c>last_run_at</c> for the named profile. Called by the profile
    /// runner whenever a new run starts so the Profiles card's RUNS
    /// counter reflects reality. Uses a single UPDATE statement so two
    /// concurrent starts of the same profile never race the counter
    /// (the runner already serialises starts per-profile, but the SQL
    /// guarantee is cheaper than relying on that invariant). Returns
    /// silently if the profile doesn't exist.
    /// </summary>
    Task RecordRunStartedAsync(string name, DateTime startedAt, CancellationToken ct = default);

    /// <summary>
    /// Generate <paramref name="count"/> profiles with a shared prefix
    /// and a 3-digit numeric suffix, round-robining the proxy pool
    /// across the new rows. Skips existing names — caller gets a split
    /// of created vs. skipped so the bulk modal can report back.
    ///
    /// Mirrors the legacy <c>POST /api/profiles/bulk</c> endpoint.
    /// </summary>
    Task<BulkCreateProfilesResult> BulkCreateAsync(
        BulkCreateProfilesRequest req, CancellationToken ct = default);
}

/// <summary>
/// Inputs for <see cref="IProfileService.BulkCreateAsync"/>. Names
/// follow <c>{Prefix}{StartIndex:000}, {Prefix}{StartIndex+1:000}, …</c>;
/// collisions are skipped and reported in the result.
/// </summary>
public sealed record BulkCreateProfilesRequest(
    string                Prefix,
    int                   Count,
    int                   StartIndex,
    string?               Language,
    string?               TemplateId,
    IReadOnlyList<string> ProxyPool,
    bool                  EnrichOnFirstRun);

public sealed record BulkCreateProfilesResult(
    IReadOnlyList<Profile> Created,
    IReadOnlyList<string>  Skipped);
