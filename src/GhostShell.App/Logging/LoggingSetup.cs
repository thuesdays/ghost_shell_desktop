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

    public static IHostBuilder UseGhostShellLogging(this IHostBuilder builder)
    {
        return builder.UseSerilog((ctx, services, lc) =>
        {
            var logsDir = AppPaths.LogsDir;
            var logFilePattern = Path.Combine(logsDir, "app-.log");

            lc.MinimumLevel.Information()
              .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
              .MinimumLevel.Override("Microsoft.Hosting", LogEventLevel.Information)
              .MinimumLevel.Override("System", LogEventLevel.Warning)
              .Enrich.FromLogContext()
              .Enrich.WithProperty("App", "GhostShell")
              .Enrich.WithProperty("Pid", Environment.ProcessId)

              // ─── Console (visible when run from `dotnet run`) ───
              .WriteTo.Console(
                  outputTemplate:
                      "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")

              // ─── Visual Studio Debug Output (F5 sessions) ───
              .WriteTo.Debug(
                  outputTemplate:
                      "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")

              // ─── Rolling file under %LocalAppData%\GhostShell\logs ───
              // Daily rolling + size-cap. retainedFileCountLimit=14 means
              // we automatically prune anything older than ~2 weeks.
              .WriteTo.File(
                  path: logFilePattern,
                  rollingInterval: RollingInterval.Day,
                  retainedFileCountLimit: 14,
                  fileSizeLimitBytes: 50L * 1024 * 1024,
                  rollOnFileSizeLimit: true,
                  shared: true,
                  flushToDiskInterval: TimeSpan.FromSeconds(2),
                  outputTemplate:
                      "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] " +
                      "[pid:{Pid}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        });
    }
}
