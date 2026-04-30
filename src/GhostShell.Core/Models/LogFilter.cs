// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.RegularExpressions;

namespace GhostShell.Core.Models;

/// <summary>
/// Pure filter predicates for log entries — extracted from the
/// Logs ViewModel so they can be unit-tested without spinning up
/// the WPF dispatcher. The VM holds the live state (current
/// LevelFilter, SourceFilter, etc.) and projects it into a
/// <see cref="LogFilterCriteria"/> snapshot per scan.
///
/// Lives in Core so the test project doesn't need a transitive
/// reference to the WPF App project just to reach this slice.
/// </summary>
public static class LogFilter
{
    /// <summary>
    /// True if the entry should be visible under the supplied
    /// criteria. RAW continuations bypass the level + time filters
    /// (they ride along with their parent entry); other filters
    /// still apply.
    /// </summary>
    public static bool Passes(LogEntry e, LogFilterCriteria c, Regex? compiledRegex = null)
    {
        if (e.Level != "RAW")
        {
            if (c.MinLevel is { } lvl && e.SemanticLevel < lvl) return false;
            if (c.TimeRange is { } range
                && e.Timestamp != DateTime.MinValue
                && e.Timestamp < c.Now - range)
                return false;
        }

        var srcNeedle = c.SourceContains?.Trim();
        if (!string.IsNullOrEmpty(srcNeedle)
            && !(e.Source?.Contains(srcNeedle, StringComparison.OrdinalIgnoreCase) ?? false))
            return false;

        var profileNeedle = c.ProfileContains?.Trim();
        if (!string.IsNullOrEmpty(profileNeedle)
            && !e.Message.Contains(profileNeedle, StringComparison.OrdinalIgnoreCase))
            return false;

        var msgNeedle = c.SearchText?.Trim();
        if (!string.IsNullOrEmpty(msgNeedle))
        {
            if (c.UseRegex)
            {
                if (compiledRegex is null) return false;
                try
                {
                    if (!compiledRegex.IsMatch(e.Message)) return false;
                }
                catch (RegexMatchTimeoutException) { return false; }
            }
            else if (!e.Message.Contains(msgNeedle, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Frozen snapshot of the filter state. Pass-by-value so tests
/// don't need to drive a live ViewModel; the ViewModel builds one
/// of these on the fly when Reproject runs.
/// </summary>
public readonly record struct LogFilterCriteria(
    LogLevel? MinLevel,
    string?   SourceContains,
    string?   ProfileContains,
    string?   SearchText,
    bool      UseRegex,
    TimeSpan? TimeRange,
    DateTime  Now);
