// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.IO;
using GhostShell.Core.Common;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace GhostShell.App.Logging;

/// <summary>
/// Single source of truth for how Ghost Shell logs. Wires Serilog
/// into the generic host so every <see cref="ILogger{T}"/> resolved
/// from DI funnels through the same pipeline.
///
/// Sinks:
///   • File   — `%LocalAppData%\GhostShell\logs\app-YYYY-MM-DD.log`,
///              daily rolling, 14-day retention, 50 MB hard cap per
///              file (rolls earlier when the cap is hit).
///   • Console — colored, condensed format.
///   • Debug   — Visual Studio debug pane.
///
/// Default level Information; we override Microsoft.* to Warning so
/// the framework noise doesn't drown app messages out. Each request
/// to enable verbose logging is one line in `app.json` (later).
/// </summary>
public static class LoggingSetup
{
    /// <summary>File path of the currently-active log (best-effort guess).</summary>
    public static string CurrentLogPath =>
        Path.Combine(AppPaths.LogsDir, $"app-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>File path of the dedicated network-trace log, written only
    /// when GHOSTSHELL_NET_TRACE is enabled. Empty otherwise.</summary>
    public static string CurrentNetLogPath =>
        Diagnostics.NetworkTrace
            ? Path.Combine(AppPaths.LogsDir, $"net-{DateTime.Now:yyyy-MM-dd}.log")
            : "";

    // The two runtime categories that carry network/proxy diagnostics.
    // Under net-trace they're dropped to Verbose and mirrored into the
    // dedicated net-*.log so the chatter stays out of the main app log.
    private static readonly string[] NetSources =
    {
        "GhostShell.Runtime.ProxyAuth",   // HttpConnectForwarder
        "GhostShell.Runtime.Browser",     // BrowserLauncher / runner / launch flow
    };

    public static IHostBuilder UseGhostShellLogging(this IHostBuilder builder)
    {
        return builder.UseSerilog((ctx, services, lc) =>
        {
            var logsDir = AppPaths.LogsDir;
            var logFilePattern = Path.Combine(logsDir, "app-.log");

            // Global minimum, optionally overridden via GHOSTSHELL_LOG_LEVEL.
            var globalLevel = ParseLevel(Diagnostics.LogLevelOverride) ?? LogEventLevel.Information;

            lc.MinimumLevel.Is(globalLevel)
              .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
              .MinimumLevel.Override("Microsoft.Hosting", LogEventLevel.Information)
              .MinimumLevel.Override("System", LogEventLevel.Warning)
              .Enrich.FromLogContext()
              .Enrich.WithProperty("App", "GhostShell")
              .Enrich.WithProperty("Pid", Environment.ProcessId)

              // ─── Console (visible when run from `dotnet run`) ───
              // restrictedToMinimumLevel keeps the per-source Verbose
              // overrides used by net-trace OUT of the general sinks —
              // they pass the root gate but only the dedicated net-*.log
              // (no restriction) captures them. Warnings (e.g. upstream
              // proxy unreachable) are >= Information so still show here.
              .WriteTo.Console(
                  restrictedToMinimumLevel: globalLevel,
                  outputTemplate:
                      "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")

              // ─── Visual Studio Debug Output (F5 sessions) ───
              .WriteTo.Debug(
                  restrictedToMinimumLevel: globalLevel,
                  outputTemplate:
                      "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")

              // ─── Rolling file under %LocalAppData%\GhostShell\logs ───
              // Daily rolling + size-cap. retainedFileCountLimit=14 means
              // we automatically prune anything older than ~2 weeks.
              .WriteTo.File(
                  path: logFilePattern,
                  restrictedToMinimumLevel: globalLevel,
                  rollingInterval: RollingInterval.Day,
                  retainedFileCountLimit: 14,
                  fileSizeLimitBytes: 50L * 1024 * 1024,
                  rollOnFileSizeLimit: true,
                  shared: true,
                  flushToDiskInterval: TimeSpan.FromSeconds(2),
                  outputTemplate:
                      "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] " +
                      "[pid:{Pid}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

            // ─── Network trace (GHOSTSHELL_NET_TRACE=1) ───────────────
            // Drop the network categories to Verbose and mirror them into
            // a dedicated net-*.log so "page won't load / proxy died"
            // issues leave a full, isolated trail (connection dial results,
            // upstream-unreachable warnings, bytes each way).
            if (Diagnostics.NetworkTrace)
            {
                foreach (var src in NetSources)
                    lc.MinimumLevel.Override(src, LogEventLevel.Verbose);

                var netFilePattern = Path.Combine(logsDir, "net-.log");
                lc.WriteTo.Logger(sub => sub
                    .Filter.ByIncludingOnly(IsNetEvent)
                    .WriteTo.File(
                        path: netFilePattern,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        fileSizeLimitBytes: 100L * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        shared: true,
                        flushToDiskInterval: TimeSpan.FromSeconds(1),
                        outputTemplate:
                            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] " +
                            "[pid:{Pid}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));
            }
        });
    }

    /// <summary>True when a log event originates from one of the network
    /// categories (matched by SourceContext prefix).</summary>
    private static bool IsNetEvent(LogEvent evt)
    {
        if (!evt.Properties.TryGetValue("SourceContext", out var prop) ||
            prop is not ScalarValue { Value: string src })
            return false;
        foreach (var s in NetSources)
            if (src.StartsWith(s, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Parse a GHOSTSHELL_LOG_LEVEL string into a Serilog level;
    /// null when unset/unrecognised (caller defaults to Information).</summary>
    private static LogEventLevel? ParseLevel(string? raw) =>
        Enum.TryParse<LogEventLevel>(raw, ignoreCase: true, out var lvl) ? lvl : null;
}
