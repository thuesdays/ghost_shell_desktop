// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.Logging;

/// <summary>
/// Wires every "uncaught exception" entry-point into the logger so
/// crashes leave a paper trail before the process dies. Three sources
/// matter for a WPF + async app:
///
///   • AppDomain.UnhandledException     — non-UI threads, terminator.
///   • Application.DispatcherUnhandled  — UI thread; e.Handled keeps
///                                         the app alive when safe.
///   • TaskScheduler.UnobservedTask     — async paths whose Task was
///                                         never awaited and faulted.
/// </summary>
public static class GlobalExceptionHandler
{
    public static void Install(Application app, ILogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                logger.LogCritical(ex, "Unhandled AppDomain exception (terminating={Terminating})",
                    e.IsTerminating);
            else
                logger.LogCritical("Unhandled AppDomain non-Exception object: {Obj}", e.ExceptionObject);
        };

        app.DispatcherUnhandledException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unhandled Dispatcher (UI thread) exception");
            // Keep the UI alive — most dispatcher exceptions are
            // recoverable (binding errors, click handler bugs).
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved Task exception");
            e.SetObserved();
        };
    }
}
