// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Glue between the runner and <see cref="ISessionService"/>.
/// Runs at two specific moments in a session's life:
///
///   • Right after launch, before the user sees a window: try to
///     restore the most recent snapshot for this profile so the
///     browser starts already "warm" (logged-in, cookied, etc.).
///     Auto-restore is skipped on a fresh profile (no snapshots yet)
///     — nothing to do.
///
///   • Right before the runner disposes the browser session (clean
///     stop, exit_code = 0): capture the current cookies + storage
///     and persist them as a new <c>auto_clean_run</c> snapshot.
///     Skipped on crash / external-close paths because the driver
///     can't answer at that point.
///
/// Both hooks are best-effort — failures log a warning and let the
/// run proceed. We never block a real Stop on a failed snapshot.
/// </summary>
public sealed class SessionLifecycle
{
    private readonly ISessionService _sessions;
    private readonly ILogger<SessionLifecycle> _log;

    public SessionLifecycle(ISessionService sessions, ILogger<SessionLifecycle> log)
    {
        _sessions = sessions;
        _log      = log;
    }

    /// <summary>
    /// Called after <see cref="IBrowserLauncher.LaunchAsync"/> returns
    /// successfully. Looks up the latest snapshot for the profile;
    /// if one exists, pushes it into the live session.
    /// </summary>
    public async Task RestoreLatestAsync(
        IBrowserSession session, CancellationToken ct = default)
    {
        SessionSnapshot? latest;
        try
        {
            latest = await _sessions.GetLatestAsync(session.ProfileName, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Could not look up latest snapshot for '{Profile}'; skipping restore",
                session.ProfileName);
            return;
        }

        if (latest is null)
        {
            _log.LogDebug(
                "No snapshot to restore for '{Profile}' (fresh profile)",
                session.ProfileName);
            return;
        }

        SessionPayload? payload;
        try
        {
            payload = await _sessions.GetPayloadAsync(latest.Id, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Could not load snapshot #{Id} payload for '{Profile}'; skipping restore",
                latest.Id, session.ProfileName);
            return;
        }
        if (payload is null || payload.IsEmpty)
        {
            _log.LogDebug(
                "Snapshot #{Id} for '{Profile}' is empty; nothing to restore",
                latest.Id, session.ProfileName);
            return;
        }

        try
        {
            await session.SetCookiesAsync(payload.Cookies, ct);
            // SetStorageAsync internally navigates per-origin; we
            // skip when storage list is empty because a no-op
            // round-trip still spends a NavigateAsync each.
            if (payload.Storage.Count > 0)
                await session.SetStorageAsync(payload.Storage, ct);

            _log.LogInformation(
                "Auto-restored snapshot #{Id} for '{Profile}' " +
                "({Cookies} cookies, {Storage} storage origins)",
                latest.Id, session.ProfileName,
                payload.Cookies.Count, payload.Storage.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Auto-restore of snapshot #{Id} failed for '{Profile}'; " +
                "session continues with fresh cookies",
                latest.Id, session.ProfileName);
        }
    }

    /// <summary>
    /// Called right before <see cref="IBrowserSession.DisposeAsync"/>
    /// on a clean-exit path. Captures cookies + per-origin storage and
    /// inserts a new snapshot row. Failures are swallowed — the
    /// teardown should never fail because of a snapshot hiccup.
    /// </summary>
    public async Task CaptureCleanRunAsync(
        IBrowserSession session, long runId, CancellationToken ct = default)
    {
        try
        {
            var cookies = await session.GetCookiesAsync(ct);
            if (cookies.Count == 0)
            {
                _log.LogDebug(
                    "Skipping snapshot for '{Profile}' (no cookies — nothing to save)",
                    session.ProfileName);
                return;
            }

            // Origins to query for localStorage = unique cookie
            // domains projected to https://. We rely on cookies as
            // the authoritative "places this profile has been"
            // signal — same heuristic as legacy session/manager.py.
            var origins = cookies
                .Select(c => "https://" + c.Domain.TrimStart('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var storage = await session.GetStorageAsync(origins, ct);

            await _sessions.SaveAsync(
                profileName: session.ProfileName,
                payload:     new SessionPayload { Cookies = cookies, Storage = storage },
                runId:       runId,
                trigger:     "auto_clean_run",
                reason:      "Auto-saved on clean shutdown",
                ct:          ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Auto-snapshot failed for '{Profile}' (run #{Run}); " +
                "session teardown continues",
                session.ProfileName, runId);
        }
    }
}
