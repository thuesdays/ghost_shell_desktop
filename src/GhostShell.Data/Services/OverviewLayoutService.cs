// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>
/// Phase 34 — Overview widget layout persistence. Stores per-user
/// enabled/position overrides for widgets registered in
/// <see cref="OverviewWidgetCatalog"/>. Falls through to catalog defaults
/// for any widget not yet configured.
/// </summary>
internal sealed class OverviewLayoutService : IOverviewLayoutService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<OverviewLayoutService> _log;

    public OverviewLayoutService(DatabaseConnection db, ILogger<OverviewLayoutService> log)
    {
        _db  = db;
        _log = log;
    }

    public async Task<IReadOnlyList<OverviewWidgetState>> ListAsync(CancellationToken ct = default)
    {
        var savedStates = await _db.QueueAsync(
            c => c.QueryAsync<OverviewWidgetState>(
                "SELECT widget_id AS WidgetId, enabled AS Enabled, position AS Position, updated_at AS UpdatedAt FROM overview_widgets ORDER BY position ASC;"),
            ct);

        var savedMap = savedStates.ToDictionary(s => s.WidgetId);
        var result = new List<OverviewWidgetState>();

        foreach (var catalog in OverviewWidgetCatalog.All)
        {
            if (savedMap.TryGetValue(catalog.Id, out var saved))
            {
                result.Add(saved);
            }
            else
            {
                // Fall through to catalog defaults
                result.Add(new OverviewWidgetState
                {
                    WidgetId = catalog.Id,
                    Enabled = catalog.DefaultOn,
                    Position = catalog.DefaultOrder,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
        }

        // Sort by Position ascending
        return result.OrderBy(s => s.Position).ToList();
    }

    public Task SaveAsync(IReadOnlyList<OverviewWidgetState> states, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO overview_widgets (widget_id, enabled, position, updated_at)
            VALUES (@widgetId, @enabled, @position, @updatedAt)
            ON CONFLICT(widget_id)
            DO UPDATE SET
              enabled    = excluded.enabled,
              position   = excluded.position,
              updated_at = excluded.updated_at;
            """;

        return _db.QueueAsync(async c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var state in states ?? Array.Empty<OverviewWidgetState>())
            {
                await c.ExecuteAsync(sql, new
                {
                    widgetId = state.WidgetId,
                    enabled = state.Enabled ? 1 : 0,
                    position = state.Position,
                    updatedAt = DateTime.UtcNow.ToString("O"),
                }, tx);
            }
            tx.Commit();
            return 0;
        }, ct);
    }

    public Task ResetAsync(CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync("DELETE FROM overview_widgets;"), ct);
}
