// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text;
using GhostShell.Core.Common;
using GhostShell.Core.Logging;
using GhostShell.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GhostShell.Tests.Logging;

/// <summary>
/// LogTail integration tests against the real filesystem. We build
/// a temp folder, point AppPaths.LogsDir at it via the env-override,
/// write Serilog-shaped lines to a fake daily file, and verify the
/// tail surfaces them.
///
/// Polling interval is 750ms in production; the tests wait up to
/// 5s to absorb cold-start jitter on slow CI VMs.
/// </summary>
public class LogTailTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _previousLogsOverride;

    public LogTailTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ghostshell-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // AppPaths.LogsDir reads from the GHOSTSHELL_DATA_DIR env var
        // — the LogTail probes <data>/logs/ for the active log. We
        // create that dir and a synthetic app-YYYY-MM-DD.log under it.
        _previousLogsOverride = Environment.GetEnvironmentVariable("GHOSTSHELL_DATA_DIR") ?? "";
        Environment.SetEnvironmentVariable("GHOSTSHELL_DATA_DIR", _tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "logs"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GHOSTSHELL_DATA_DIR", _previousLogsOverride);
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string LogFilePath() =>
        Path.Combine(_tempDir, "logs", $"app-{DateTime.Today:yyyy-MM-dd}.log");

    private static string MakeLine(string level, string source, string message,
        DateTime? ts = null) =>
        $"[{(ts ?? DateTime.Now):yyyy-MM-dd HH:mm:ss.fff zzz} {level}] " +
        $"[pid:1234] {source}: {message}";

    [Fact]
    public async Task Append_EmitsParsedEntries()
    {
        var path = LogFilePath();
        await File.WriteAllTextAsync(path,
            MakeLine("INF", "Test.Source", "first line") + "\n",
            Encoding.UTF8);

        var received = new List<LogEntry>();
        var gate     = new SemaphoreSlim(0, 1);

        await using var tail = new LogTail(NullLogger<LogTail>.Instance,
            backfillBytes: 1024 * 1024);
        tail.EntryAppended += e =>
        {
            lock (received) received.Add(e);
            // Release once we have at least 2 (backfill + new line).
            if (received.Count >= 2) gate.Release();
        };

        await tail.StartAsync();

        // Append a second line AFTER the tail starts so the cursor
        // advance path runs (not just backfill).
        await Task.Delay(200);
        await File.AppendAllTextAsync(path,
            MakeLine("WAR", "Test.Source", "second line") + "\n",
            Encoding.UTF8);

        await gate.WaitAsync(TimeSpan.FromSeconds(5));

        lock (received)
        {
            Assert.True(received.Count >= 2, $"got {received.Count} lines");
            Assert.Contains(received, e =>
                e.Level == "INF" && e.Message == "first line");
            Assert.Contains(received, e =>
                e.Level == "WAR" && e.Message == "second line");
        }
    }

    [Fact]
    public async Task Backfill_BoundedToWindow()
    {
        var path = LogFilePath();
        // Write 5KB of lines but pass backfillBytes=512 to the tail —
        // only the trailing window should be replayed at startup.
        var sb = new StringBuilder();
        for (var i = 0; i < 100; i++)
            sb.AppendLine(MakeLine("INF", "Test.Source", $"line {i}"));
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);

        var received = new List<LogEntry>();
        await using var tail = new LogTail(NullLogger<LogTail>.Instance,
            backfillBytes: 512);
        tail.EntryAppended += e =>
        {
            lock (received) received.Add(e);
        };

        await tail.StartAsync();
        await Task.Delay(1500); // one polling cycle

        lock (received)
        {
            // 100 lines × ~80 bytes ≈ 8KB. With a 512-byte backfill
            // the user sees roughly the last 6 lines, not all 100.
            Assert.NotEmpty(received);
            Assert.True(received.Count < 100,
                $"backfill should be bounded, got {received.Count} of 100");
        }
    }

    [Fact]
    public async Task DoubleStart_Throws()
    {
        await using var tail = new LogTail(NullLogger<LogTail>.Instance);
        await tail.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => tail.StartAsync());
    }

    [Fact]
    public async Task StartAsync_NoFile_NoEntriesNoCrash()
    {
        // The logs dir exists but no file in it. Tail starts cleanly,
        // emits nothing, doesn't throw.
        var received = new List<LogEntry>();
        await using var tail = new LogTail(NullLogger<LogTail>.Instance);
        tail.EntryAppended += e => { lock (received) received.Add(e); };

        await tail.StartAsync();
        await Task.Delay(1200);
        lock (received) Assert.Empty(received);
    }
}
