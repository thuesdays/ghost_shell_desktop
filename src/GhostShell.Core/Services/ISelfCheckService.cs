// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Network-layer probe runner. Hits ipinfo for exit IP + geo, runs a
/// WebRTC ICE-candidate probe via JS to detect local-IP leaks,
/// captures Intl.DateTimeFormat().resolvedOptions().timeZone via JS,
/// reads navigator.userAgent.
///
/// Probe execution is kicked off by the runtime hook after a profile
/// launch (or on demand from the FP page). Results persist via
/// <see cref="ISelfCheckHistoryService"/>.
/// </summary>
public interface ISelfCheckService
{
    /// <summary>
    /// Run the probe against an active browser session. Caller passes
    /// the session — we don't launch a browser ourselves.
    /// </summary>
    Task<SelfCheckResult> RunAsync(IBrowserSession session, string profileName,
        long? runId = null, string? expectedTimezone = null,
        CancellationToken ct = default);

    /// <summary>Last N probe results for the profile.</summary>
    Task<IReadOnlyList<SelfCheckResult>> ListAsync(
        string profileName, int limit = 50, CancellationToken ct = default);

    /// <summary>Most recent probe result for the profile, or null.</summary>
    Task<SelfCheckResult?> GetLatestAsync(string profileName, CancellationToken ct = default);
}

/// <summary>
/// Persistence-only split — same pattern as IFingerprintAuditService.
/// Implementation lives in GhostShell.Data; orchestration in Runtime.
/// </summary>
public interface ISelfCheckHistoryService
{
    Task<long> InsertAsync(SelfCheckResult result, CancellationToken ct = default);

    Task<IReadOnlyList<SelfCheckResult>> ListAsync(
        string profileName, int limit = 50, CancellationToken ct = default);

    Task<SelfCheckResult?> GetLatestAsync(string profileName, CancellationToken ct = default);
}
