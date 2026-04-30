// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Aggregated attention item shown in the bell drawer. Mirrors the
/// payload shape of /api/notifications in the legacy dashboard so
/// the same builder logic ports cleanly.
/// </summary>
public sealed class AppNotification
{
    public required string Id { get; init; }
    public Severity Severity { get; init; } = Severity.Info;

    public required string Title { get; init; }
    public string? Body { get; init; }

    /// <summary>What page the user is sent to on click ("profile", "proxy", etc.).</summary>
    public string? Action { get; init; }

    /// <summary>Optional argument to the action (e.g. profile name).</summary>
    public string? ActionArg { get; init; }

    public DateTime CreatedAt { get; init; }
}

public enum Severity
{
    Info,
    Warning,
    Critical,
}
