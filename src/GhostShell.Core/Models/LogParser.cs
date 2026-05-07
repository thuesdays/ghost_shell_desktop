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
    //
    // Phase 71cc — the level alternation now includes WRN (Serilog's
    // 3-char abbreviation for Warning). The original regex used
    // "WAR" which doesn't match Serilog's `{Level:u3}` output —
    // every Warning line silently fell through to the RAW path,
    // logged with timestamp 00:00:00 in the Logs export. Same fix
    // applied to ERR/FTL forms (already matched).
    private static readonly Regex Line = new(
        @"^\[(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}\s+[+-]\d{2}:\d{2})\s+" +
        @"(?<lvl>VRB|DBG|INF|WRN|ERR|FTL)\]\s+" +
        @"(?:\[pid:(?<pid>\d+)\]\s+)?" +
        @"(?:(?<src>[^:]+):\s+)?" +
        @"(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Phase 71cc — secondary regex for RAW-classified lines that
    // STILL contain a parseable head (rare race when the file was
    // partially written + reread). If we can extract the embedded
    // timestamp we project it onto the entry instead of the
    // 00:00:00 default — keeps export sort order intact.
    private static readonly Regex EmbeddedTs = new(
        @"\[(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}\s+[+-]\d{2}:\d{2})\s+" +
        @"(?<lvl>VRB|DBG|INF|WRN|ERR|FTL)\]",
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
            // Phase 71cc — even for unparseable lines try to extract
            // an embedded ISO timestamp so the export's [HH:mm:ss]
            // column shows the real time instead of 00:00:00. Common
            // case: a line that has the ISO head but trailing text
            // doesn't match our column shape.
            var embedded = EmbeddedTs.Match(line);
            DateTime ts2 = DateTime.MinValue;
            if (embedded.Success
                && DateTimeOffset.TryParse(embedded.Groups["ts"].Value,
                       CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto2))
            {
                ts2 = dto2.LocalDateTime;
            }
            return new LogEntry(ts2, "RAW", null, null, line);
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
