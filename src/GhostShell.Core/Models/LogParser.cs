// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Text.RegularExpressions;

namespace GhostShell.Core.Models;

/// <summary>
/// Parses lines from the Serilog file sink. Format pinned by
/// <c>LoggingSetup.cs</c>:
///
///   [2026-04-29 22:55:09.250 +03:00 INF] [pid:11692] Source: Message
///
/// Multi-line entries (exception stack traces) follow the head line
/// and start with whitespace; the parser leaves them as separate
/// "RAW" entries which the consumer can fold back into the previous
/// entry's <see cref="LogEntry.Message"/> if desired.
///
/// The regex is anchored at the start of the line so we don't
/// accidentally match a "[" appearing inside a message.
/// </summary>
public static class LogParser
{
    // Group order: 1=ts, 2=lvl, 3=pid, 4=source, 5=message.
    // Whitespace tolerant so a future format tweak (different
    // padding) stays compatible.
    private static readonly Regex Line = new(
        @"^\[(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}\s+[+-]\d{2}:\d{2})\s+" +
        @"(?<lvl>VRB|DBG|INF|WAR|ERR|FTL)\]\s+" +
        @"(?:\[pid:(?<pid>\d+)\]\s+)?" +
        @"(?:(?<src>[^:]+):\s+)?" +
        @"(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parse one line. Never returns null — falls back to a "RAW"
    /// entry for unparseable lines so consumers can render them as-is.
    /// </summary>
    public static LogEntry Parse(string line)
    {
        if (string.IsNullOrEmpty(line))
            return new LogEntry(DateTime.MinValue, "RAW", null, null, "");

        var m = Line.Match(line);
        if (!m.Success)
        {
            // Continuation line (stack-trace) or unrecognised — keep
            // the raw text so the UI can still render it.
            return new LogEntry(DateTime.MinValue, "RAW", null, null, line);
        }

        var tsRaw = m.Groups["ts"].Value;
        // Serilog's "+03:00" is a UTC-offset; DateTimeOffset is the
        // right home for that. We project to local DateTime for the
        // UI — the file already encodes timezone.
        var ts = DateTimeOffset.TryParse(tsRaw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var dto)
            ? dto.LocalDateTime
            : DateTime.MinValue;

        int? pid = null;
        if (m.Groups["pid"].Success && int.TryParse(m.Groups["pid"].Value, out var p))
            pid = p;

        string? source = m.Groups["src"].Success
            ? m.Groups["src"].Value.Trim()
            : null;

        return new LogEntry(
            Timestamp: ts,
            Level:     m.Groups["lvl"].Value,
            Pid:       pid,
            Source:    source,
            Message:   m.Groups["msg"].Value);
    }
}
