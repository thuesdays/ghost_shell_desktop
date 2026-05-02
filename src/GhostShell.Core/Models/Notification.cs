// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>Phase 29 — bell-drawer notification. Mirrors the web's
/// item shape (severity / title / body / action / action_arg) plus a
/// dismissed_at column so the desktop can hide entries without
/// deleting them.</summary>
public sealed record Notification
{
    public long   Id          { get; init; }
    public string Severity    { get; init; } = "info";   // info | warning | critical | success
    public string Title       { get; init; } = "";
    public string? Body       { get; init; }
    /// <summary>Click-target verb. Known values: open_profile,
    /// open_proxy, open_runs, open_scheduler, url. The bell drawer
    /// turns this into a Navigate / Process.Start call.</summary>
    public string? Action     { get; init; }
    public string? ActionArg  { get; init; }
    /// <summary>Tag for grouping/dedup (e.g. "run_failed", "update_check"). </summary>
    public string Source      { get; init; } = "manual";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? DismissedAt { get; init; }
    public bool IsDismissed => DismissedAt is not null;
}

public static class NotificationSeverity
{
    public const string Info     = "info";
    public const string Warning  = "warning";
    public const string Critical = "critical";
    public const string Success  = "success";

    public static IReadOnlyList<string> All => new[] { Critical, Warning, Info, Success };
    public static int Rank(string severity) => severity switch
    {
        Critical => 0,
        Warning  => 1,
        Info     => 2,
        Success  => 3,
        _        => 4,
    };
    public static bool IsValid(string severity) => All.Contains(severity);
}
