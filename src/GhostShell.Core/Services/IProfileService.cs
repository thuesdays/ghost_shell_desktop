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
