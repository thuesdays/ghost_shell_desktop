// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Common;
using GhostShell.Core.Models;
using Microsoft.Extensions.Logging;

namespace GhostShell.Core.Logging;

/// <summary>
/// Tails the current Serilog daily file under
/// <c>%LocalAppData%\GhostShell\logs\app-YYYY-MM-DD.log</c>.
///
/// Strategy:
///   • On <see cref="StartAsync"/>: seek to end-of-file MINUS the
///     last <c>backfillBytes</c> (default 1 MiB) so the page opens
///     with recent context already populated, then continue from
///     EOF.
///   • Periodic poll (<c>PollInterval</c>) — read any bytes added
///     since the last position, split on newlines, parse each line
///     and fan out to <see cref="EntryAppended"/>.
///   • Day-rollover: when the date changes the underlying path
///     changes too. We re-resolve the current path on every tick
///     and reset the read cursor when it differs from the last one.
///
/// Safety:
///   • Opens with <c>FileShare.ReadWrite</c> so Serilog can keep
///     writing while we read.
///   • Each call gets its own <see cref="FileStream"/> handle —
///     no shared mutable state outside the cursor + last-path.
///
/// Concurrency contract:
///   • Single instance per app. Start once, Dispose once.
///   • <c>EntryAppended</c> handlers run on the polling thread —
///     consumers must marshal to UI dispatcher themselves.
///
/// Lives in Core (not App) so the test project can target it
/// without pulling in the WPF / net8.0-windows reference graph.
/// </summary>
public sealed class LogTail : IAsyncDisposable
{
    public static readonly TimeSpan PollInterval     = TimeSpan.FromMilliseconds(750);
    public static readonly long     DefaultBackfill  = 1L * 1024 * 1024; // 1 MiB

    private readonly ILogger<LogTail> _log;
    private readonly long _backfillBytes;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _currentPath;
    private long _cursor;
    private bool _disposed;

    public event Action<LogEntry>? EntryAppended;

    public LogTail(ILogger<LogTail> log, long? backfillBytes = null)
    {
        _log           = log;
        _backfillBytes = backfillBytes ?? DefaultBackfill;
    }

    public bool IsRunning => _loop is not null && !_loop.IsCompleted;

    public Task StartAsync()
    {
        if (_loop is not null)
            throw new InvalidOperationException("LogTail already started.");

        _cts  = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch { /* swallow */ }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* expected — loop unwinds via cancellation */ }
        }
        _cts?.Dispose();
    }

    // ─────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // Don't let a transient I/O hiccup kill the tailer —
                // log + carry on. Log file rotations and day boundaries
                // can briefly produce file-not-found while Serilog
                // re-opens.
                _log.LogDebug(ex, "LogTail tick failed; will retry");
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Tick()
    {
        var path = ResolveCurrentLogPath();
        if (string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path)) return;

        // Detect day-rollover (path changed since last tick): reset
        // cursor to the new file's start (minus backfill).
        if (!string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            _currentPath = path;
            var info     = new FileInfo(path);
            _cursor      = Math.Max(0, info.Length - _backfillBytes);
        }

        // Open exclusively for read but tolerant to Serilog's
        // ongoing write. shared = ReadWrite.
        using var fs = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _cursor)
        {
            // File was truncated (rare — size-based rollover) — start
            // from current end so we don't replay history we already
            // emitted.
            _cursor = fs.Length;
            return;
        }

        if (fs.Length == _cursor) return; // nothing new

        fs.Seek(_cursor, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var entry = LogParser.Parse(line);
            EntryAppended?.Invoke(entry);
        }
        _cursor = fs.Position;
    }

    private static string? ResolveCurrentLogPath()
    {
        // Serilog rolls daily with the pattern app-yyyy-MM-dd.log.
        // We could read LoggingSetup.CurrentLogPath but that points
        // at the seed pattern, not the resolved current file. The
        // simplest robust approach: pick the newest .log under the
        // logs directory.
        var dir = AppPaths.LogsDir;
        if (!Directory.Exists(dir)) return null;

        return new DirectoryInfo(dir)
            .EnumerateFiles("app-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }
}
