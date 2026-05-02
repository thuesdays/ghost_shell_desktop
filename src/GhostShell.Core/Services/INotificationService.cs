// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 29 — bell-drawer notification service. Owns the
/// <c>notifications</c> table and surfaces an in-process event so the
/// UI (bell badge + drawer) can refresh without polling.
///
/// <para>The web project re-aggregates notifications from other tables
/// on every fetch; the desktop port stores them once when the
/// triggering event happens (run failed, IP burned, app update, …)
/// then flips the dismissed_at column when the user clicks. This keeps
/// the bell drawer responsive on big DBs and lets the user see recent
/// events even after the underlying data was cleared.</para>
/// </summary>
public interface INotificationService
{
    /// <summary>Emitted whenever a notification is added, dismissed,
    /// or bulk-cleared. UI subscribes to refresh the bell badge.</summary>
    event EventHandler? Changed;

    /// <summary>Insert a new notification. Returns the persisted row
    /// with its assigned id. Severity is validated; unknown values
    /// fall back to "info".</summary>
    Task<Notification> AddAsync(
        string severity, string title, string? body = null,
        string? action = null, string? actionArg = null,
        string source = "manual",
        CancellationToken ct = default);

    /// <summary>Most-recent-first list of UNDISMISSED notifications,
    /// capped at <paramref name="limit"/> rows.</summary>
    Task<IReadOnlyList<Notification>> ListActiveAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>List ALL notifications (active + dismissed), most
    /// recent first, capped at <paramref name="limit"/>. Used by the
    /// drawer's "show dismissed" toggle.</summary>
    Task<IReadOnlyList<Notification>> ListAllAsync(int limit = 200, CancellationToken ct = default);

    /// <summary>Counts grouped by severity for the bell badge tint.</summary>
    Task<IReadOnlyDictionary<string, int>> CountActiveBySeverityAsync(CancellationToken ct = default);

    /// <summary>Mark a single notification as dismissed (sets
    /// dismissed_at = now). No-op if already dismissed.</summary>
    Task DismissAsync(long id, CancellationToken ct = default);

    /// <summary>Mark every active notification as dismissed.</summary>
    Task DismissAllAsync(CancellationToken ct = default);

    /// <summary>Hard-delete rows older than N days (active or
    /// dismissed). Default 30. Called from app startup once a day.</summary>
    Task PurgeOlderThanAsync(int days = 30, CancellationToken ct = default);
}
