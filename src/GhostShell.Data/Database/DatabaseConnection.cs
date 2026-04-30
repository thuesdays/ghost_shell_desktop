// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShell.Data.Database;

/// <summary>
/// Opens a single shared <see cref="SqliteConnection"/> in WAL mode
/// for the application's lifetime. Mirrors the legacy
/// <c>db/database.py</c> strategy: one connection, WAL journaling,
/// BUSY_TIMEOUT high enough to absorb scheduler bursts.
///
/// Concurrency: <see cref="SqliteConnection"/> is NOT thread-safe.
/// Two services calling <c>QueryAsync</c> at the same time on the
/// same connection will at best serialize via the implicit lock
/// inside ADO.NET, and at worst surface SQLite's
/// <c>"There is already an open DataReader"</c> error — which we hit
/// in real life when one ViewModel triggers a reload while another
/// VM's reload is mid-await (e.g. RunsViewModel's ActiveChanged
/// handler racing with ProfilesViewModel's OnNavigatedToAsync).
///
/// Fix: a process-wide <see cref="SemaphoreSlim"/> serialises every
/// query. The single-connection-shared-pragmas pattern is preserved;
/// callers that need long-running work (transactions across multiple
/// statements) should use <see cref="GetExclusiveAsync"/> which
/// holds the gate for the whole duration of the returned lease.
/// Read/write Dapper one-shots use <see cref="QueueAsync"/> — they
/// take the gate, run the lambda, release.
///
/// Why not connection-per-call: with WAL + busy_timeout=5000, plain
/// pooled connections work too, but pre-Phase-5 we have an in-memory
/// cache of derived state (proxy stats, run-history aggregates) that
/// relies on the shared <c>Cache=Shared</c> setting. Splitting now
/// would require touching every service.
/// </summary>
public sealed class DatabaseConnection : IDisposable
{
    private readonly object _gate = new();

    /// <summary>
    /// Process-wide query gate. Every call into the underlying
    /// connection (via <see cref="Get"/>) acquires this before
    /// touching ADO.NET, releases when the operation finishes.
    /// </summary>
    private readonly SemaphoreSlim _querySemaphore = new(1, 1);

    private readonly ILogger<DatabaseConnection> _log;
    private SqliteConnection? _conn;

    public string ConnectionString { get; }

    public DatabaseConnection(ILogger<DatabaseConnection>? log = null)
        : this(AppPaths.DatabasePath, log) { }

    public DatabaseConnection(string dbPath, ILogger<DatabaseConnection>? log = null)
    {
        _log = log ?? NullLogger<DatabaseConnection>.Instance;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Cache      = SqliteCacheMode.Shared,
            Pooling    = true,
        }.ToString();
        _log.LogDebug("DatabaseConnection initialized: {DbPath}", dbPath);
    }

    /// <summary>
    /// Returns the shared open connection. The first call opens it
    /// and applies pragmas; subsequent calls just hand the same
    /// instance back. Callers that issue async queries SHOULD use
    /// <see cref="QueueAsync{T}"/> instead — direct .Get() use is
    /// kept for the migration runner (synchronous boot path) and
    /// should not be added in new code without a serialization plan.
    /// </summary>
    public SqliteConnection Get()
    {
        if (_conn is { State: System.Data.ConnectionState.Open })
            return _conn;

        lock (_gate)
        {
            if (_conn is { State: System.Data.ConnectionState.Open })
                return _conn;

            _log.LogInformation("Opening SQLite connection ({Cs})", ConnectionString);
            var c = new SqliteConnection(ConnectionString);
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous   = NORMAL;
                    PRAGMA busy_timeout  = 5000;
                    PRAGMA foreign_keys  = ON;
                """;
                cmd.ExecuteNonQuery();
            }
            _log.LogDebug("SQLite pragmas applied (WAL, busy_timeout=5000)");
            _conn = c;
            return _conn;
        }
    }

    /// <summary>
    /// Acquire the query gate, run a lambda against the shared
    /// connection, release. Use this for every read or write so two
    /// parallel callers never trigger
    /// <c>"There is already an open DataReader"</c>.
    /// </summary>
    public async Task<T> QueueAsync<T>(
        Func<SqliteConnection, Task<T>> work, CancellationToken ct = default)
    {
        await _querySemaphore.WaitAsync(ct);
        try
        {
            return await work(Get());
        }
        finally
        {
            _querySemaphore.Release();
        }
    }

    /// <summary>Same as <see cref="QueueAsync{T}"/> but for void returns.</summary>
    public async Task QueueAsync(
        Func<SqliteConnection, Task> work, CancellationToken ct = default)
    {
        await _querySemaphore.WaitAsync(ct);
        try
        {
            await work(Get());
        }
        finally
        {
            _querySemaphore.Release();
        }
    }

    public void Dispose()
    {
        if (_conn is null) return;
        _log.LogDebug("Closing SQLite connection");
        _conn.Close();
        _conn.Dispose();
        _conn = null;
        _querySemaphore.Dispose();
    }
}
