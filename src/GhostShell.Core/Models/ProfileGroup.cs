// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// A named bag of profiles that can be batch-launched. Mirrors the
/// legacy <c>groups</c> table — group identity is the
/// auto-incremented <see cref="Id"/>, but <see cref="Name"/> is
/// UNIQUE so the UI can use either as a stable handle.
///
/// Membership lives in a separate table; see
/// <see cref="GhostShell.Core.Services.IProfileGroupService"/> for
/// the public API.
/// </summary>
public sealed class ProfileGroup
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// How many of this group's profiles may run concurrently.
    /// <c>null</c> = inherit the global runner cap. Overflow profiles
    /// stay queued (handled in the runner, not the data layer).
    /// </summary>
    public int? MaxParallel { get; init; }

    /// <summary>Profile names in this group. Materialised by the
    /// service when the caller asks for a single group; List-level
    /// queries return this as a count, not a names array.</summary>
    public IReadOnlyList<string> Members { get; init; } = Array.Empty<string>();

    /// <summary>How many members the group has. Cheap to compute
    /// at list time; populated even when <see cref="Members"/> is
    /// empty (the list-all path doesn't load names).</summary>
    public int MemberCount { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
