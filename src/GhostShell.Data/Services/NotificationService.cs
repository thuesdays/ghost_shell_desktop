// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>Phase 29 — bell-drawer notification implementation.</summary>
internal sealed class NotificationService : INotificationService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(DatabaseConnection db, ILogger<NotificationService> log)
    {
        _db = db;
        _log = log;
    }

    public event EventHandler? Changed;

    public async Task<Notification> AddAsync(
        string severity, string title, string? body = null,
        string? action = null, string? actionArg = null,
        string source = "manual",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("title is required", nameof(title));
        if (!NotificationSeverity.IsValid(severity))
            severity = NotificationSeverity.Info;
        const string sql = """
            INSERT INTO notifications
              (severity, title, body, action, action_arg, source, created_at)
            VALUES (@severity, @title, @body, @action, @actionArg, @source, @createdAt)
            RETURNING id;
        """;
        var nowIso = DateTime.UtcNow.ToString("O");
        var id = await _db.QueueAsync(c => c.ExecuteScalarAsync<long>(sql, new
        {
            severity, title, body, action, actionArg, source, createdAt = nowIso,
        }), ct);
        var row = new Notification
        {
            Id = id, Severity = severity, Title = title, Body = body,
            Action = action, ActionArg = actionArg, Source = source,
            CreatedAt = DateTime.UtcNow,
        };
        _log.LogInformation(
            "Notification #{Id} added [{Sev}] '{Title}' (source={Src})",
            id, severity, title, source);
        FireChanged();
        return row;
    }

    private const string SelectColumns = """
        id, severity, title, body, action,
        action_arg   AS ActionArg,
        source,
        created_at   AS CreatedAt,
        dismissed_at AS DismissedAt
    """;

    public async Task<IReadOnlyList<Notification>> ListActiveAsync(
        int limit = 100, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var sql = $"""
            SELECT {SelectColumns}
            FROM notifications
            WHERE dismissed_at IS NULL
            ORDER BY
              CASE severity
                WHEN 'critical' THEN 0
                WHEN 'warning'  THEN 1
                WHEN 'info'     THEN 2
                WHEN 'success'  THEN 3
                ELSE 4
              END,
              created_at DESC
            LIMIT @limit;
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<Notification>(sql, new { limit }), ct);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Notification>> ListAllAsync(
        int limit = 200, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var sql = $"""
            SELECT {SelectColumns}
            FROM notifications
            ORDER BY created_at DESC
            LIMIT @limit;
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<Notification>(sql, new { limit }), ct);
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> CountActiveBySeverityAsync(
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT severity AS Severity, COUNT(*) AS Count
            FROM notifications
            WHERE dismissed_at IS NULL
            GROUP BY severity;
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<(string Severity, int Count)>(sql), ct);
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sev in NotificationSeverity.All) dict[sev] = 0;
        foreach (var r in rows) dict[r.Severity] = r.Count;
        return dict;
    }

    public async Task DismissAsync(long id, CancellationToken ct = default)
    {
        var rows = await _db.QueueAsync(c => c.ExecuteAsync(
            "UPDATE notifications SET dismissed_at = @now WHERE id = @id AND dismissed_at IS NULL;",
            new { id, now = DateTime.UtcNow.ToString("O") }), ct);
        if (rows > 0) FireChanged();
    }

    public async Task DismissAllAsync(CancellationToken ct = default)
    {
        var rows = await _db.QueueAsync(c => c.ExecuteAsync(
            "UPDATE notifications SET dismissed_at = @now WHERE dismissed_at IS NULL;",
            new { now = DateTime.UtcNow.ToString("O") }), ct);
        if (rows > 0)
        {
            _log.LogInformation("Notifications: bulk-dismissed {Count} row(s)", rows);
            FireChanged();
        }
    }

    public async Task PurgeOlderThanAsync(int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650);
        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("O");
        var rows = await _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM notifications WHERE created_at < @cutoff;",
            new { cutoff }), ct);
        if (rows > 0)
        {
            _log.LogInformation("Notifications: purged {Count} row(s) older than {Days}d", rows, days);
            FireChanged();
        }
    }

    private void FireChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _log.LogWarning(ex, "Notification subscriber threw"); }
    }
}
