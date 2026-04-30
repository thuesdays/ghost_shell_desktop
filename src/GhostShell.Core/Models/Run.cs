// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// One execution of a profile. Mirrors the legacy `runs` table —
/// we keep the same column semantics (exit_code = null = running,
/// 0 = success, anything else = failed) so legacy data can be
/// imported one-to-one in a future phase.
/// </summary>
public sealed class Run
{
    public long Id { get; init; }
    public required string ProfileName { get; init; }

    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }

    /// <summary>null while running; 0 = success; anything else = failure code.</summary>
    public int? ExitCode { get; init; }

    public int TotalQueries { get; init; }
    public int TotalAds { get; init; }
    public int Captchas { get; init; }

    /// <summary>Last error message if the run failed.</summary>
    public string? LastError { get; init; }

    /// <summary>
    /// 30-second heartbeat refreshed by the watchdog while a session
    /// is alive. Used to flag "wedged" runs (PID alive but heartbeat
    /// stale → process is hung). Null on rows pre-Phase-4 or finished
    /// runs.
    /// </summary>
    public DateTime? HeartbeatAt { get; init; }

    /// <summary>
    /// Short tag describing how the run ended. One of: <c>clean</c>,
    /// <c>external_close</c>, <c>crash</c>, <c>launch_failed</c>,
    /// <c>user_marked_failed</c>, <c>cancelled</c>. Null on still-
    /// running rows. Distinct from <see cref="LastError"/> (free-
    /// form) and <see cref="ExitCode"/> (numeric).
    /// </summary>
    public string? StopReason { get; init; }

    // ─── Derived helpers (computed from the fields above; not persisted) ───

    public bool IsRunning => FinishedAt is null && ExitCode is null;
    public bool IsSuccess => ExitCode == 0;
    public bool IsFailed  => ExitCode is not null && ExitCode != 0;

    public TimeSpan? Duration =>
        StartedAt != default && FinishedAt is not null
            ? FinishedAt.Value - StartedAt
            : null;

    /// <summary>Compact status label suitable for table cells.</summary>
    public string StatusLabel =>
        IsRunning ? "running"
        : IsSuccess ? "OK"
        : ExitCode!.Value.ToString();
}
