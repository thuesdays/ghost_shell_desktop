// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// One parsed log line from the Serilog file sink. The format is
/// fixed by <c>LoggingSetup.cs</c>:
///
/// <code>
/// [yyyy-MM-dd HH:mm:ss.fff +zz:zz LVL] [pid:N] Source: Message
/// </code>
///
/// Multi-line entries (exception stack traces) are appended to
/// <see cref="Message"/> with embedded newlines. The parser is
/// resilient to malformed lines — a line that doesn't match the
/// expected shape becomes an entry with <see cref="Level"/> =
/// <c>"RAW"</c> and the entire string in <see cref="Message"/>,
/// so we never lose data.
/// </summary>
public sealed record LogEntry(
    DateTime  Timestamp,
    string    Level,        // VRB / DBG / INF / WAR / ERR / FTL / RAW
    int?      Pid,
    string?   Source,       // "GhostShell.Runtime.Browser.RealProfileRunner"
    string    Message)
{
    /// <summary>Cheap "info" / "warning" / "error" / "info" mapping for UI colour.</summary>
    public LogLevel SemanticLevel => Level switch
    {
        "FTL" => LogLevel.Error,
        "ERR" => LogLevel.Error,
        "WAR" => LogLevel.Warning,
        "INF" => LogLevel.Information,
        "DBG" => LogLevel.Debug,
        "VRB" => LogLevel.Trace,
        _     => LogLevel.Information,
    };

    /// <summary>Short last-segment of the source for compact display.</summary>
    public string ShortSource =>
        string.IsNullOrEmpty(Source) ? ""
        : Source.Substring(Source.LastIndexOf('.') + 1);
}

public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
}
