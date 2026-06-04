// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Common;

/// <summary>
/// Cross-cutting diagnostics toggles, all driven by environment
/// variables so they can be flipped without a rebuild or a settings
/// round-trip. Read once at process start and cached.
///
/// Knobs:
///   GHOSTSHELL_NET_TRACE = 1|true|yes|on
///       Turn on deep NETWORK tracing. Effects:
///         • the auth-proxy forwarder logs every connection (accept,
///           upstream dial result + latency, bytes each way, close
///           reason) at Debug/Trace;
///         • Serilog drops the GhostShell.Runtime.ProxyAuth /
///           GhostShell.Runtime.Browser categories to Verbose and adds
///           a dedicated rolling `net-YYYY-MM-DD.log` sink;
///         • each launched Chromium writes a full chrome netlog
///           (--log-net-log) under the logs dir.
///       Use this to chase "page won't load / ERR_EMPTY_RESPONSE /
///       proxy died" problems — the forwarder will say out loud when
///       the upstream proxy is unreachable.
///
///   GHOSTSHELL_LOG_LEVEL = Verbose|Debug|Information|Warning|Error
///       Override the global minimum log level (default Information).
///       Independent of NET_TRACE; NET_TRACE only widens the two
///       network categories so you can trace the network without
///       drowning in framework chatter.
/// </summary>
public static class Diagnostics
{
    private const string EnvNetTrace = "GHOSTSHELL_NET_TRACE";
    private const string EnvLogLevel = "GHOSTSHELL_LOG_LEVEL";

    /// <summary>True when deep network tracing is requested.</summary>
    public static bool NetworkTrace { get; } = IsTruthy(Environment.GetEnvironmentVariable(EnvNetTrace));

    /// <summary>Raw global log-level override (e.g. "Verbose"), or null
    /// when unset. Parsing into a concrete Serilog level happens in the
    /// App layer so Core stays free of a Serilog dependency.</summary>
    public static string? LogLevelOverride { get; } =
        NullIfBlank(Environment.GetEnvironmentVariable(EnvLogLevel));

    private static bool IsTruthy(string? v) =>
        v is not null &&
        (v.Equals("1", StringComparison.Ordinal) ||
         v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         v.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static string? NullIfBlank(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
