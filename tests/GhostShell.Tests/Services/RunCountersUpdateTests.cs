// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Services;
using Xunit;

namespace GhostShell.Tests.Services;

/// <summary>
/// Phase 71dd regression — REQ/ADS/CAPTCHA columns on the runs grid
/// were always 0 because nobody ever called UpdateCountersAsync. This
/// test fixture pins the contract:
///   • IRunService now exposes UpdateCountersAsync(runId, q, ads, c)
///   • The signature is exactly (long, int, int, int, CancellationToken)
///   • Calls to it actually persist the values when wired to a real
///     SQLite-backed run service (covered separately via integration
///     tests; here we just verify the interface shape so the runner
///     side can rely on it).
///
/// Pre-fix the method didn't exist at all — the runs.total_queries /
/// total_ads / captchas columns were created in V1 but never UPDATEd.
/// We add a contract-level test so a future refactor can't silently
/// remove the method again.
/// </summary>
public class RunCountersUpdateTests
{
    [Fact]
    public void IRunService_HasUpdateCountersAsyncMethod()
    {
        // Static introspection — the method must exist with the
        // expected shape. Paranoid because this method's absence is
        // exactly the bug we're regression-testing.
        var method = typeof(IRunService).GetMethod(nameof(IRunService.UpdateCountersAsync));
        Assert.NotNull(method);

        // Signature: Task UpdateCountersAsync(long, int, int, int, CancellationToken)
        var parameters = method!.GetParameters();
        Assert.Equal(5, parameters.Length);
        Assert.Equal(typeof(long),              parameters[0].ParameterType);  // runId
        Assert.Equal(typeof(int),               parameters[1].ParameterType);  // totalQueries
        Assert.Equal(typeof(int),               parameters[2].ParameterType);  // totalAds
        Assert.Equal(typeof(int),               parameters[3].ParameterType);  // captchas
        Assert.Equal(typeof(CancellationToken), parameters[4].ParameterType);  // ct
        Assert.Equal(typeof(Task),              method.ReturnType);
    }

    [Fact]
    public async Task RecordingService_StoresAllThreeCounters()
    {
        // Cheap behavioural test against an in-memory stub. The real
        // SQLite-backed service is exercised by integration tests; here
        // we just confirm the call contract round-trips q/ads/c.
        var svc = new RecordingService();
        await svc.UpdateCountersAsync(runId: 42,
            totalQueries: 7, totalAds: 3, captchas: 1);
        Assert.Equal(42, svc.LastRunId);
        Assert.Equal(7,  svc.LastQueries);
        Assert.Equal(3,  svc.LastAds);
        Assert.Equal(1,  svc.LastCaptchas);
    }

    /// <summary>
    /// Minimal IRunService stub. We only override UpdateCountersAsync
    /// because it's the one we're regression-testing; everything else
    /// is a NotImplementedException so any test that accidentally hits
    /// a different method fails loudly.
    /// </summary>
    private sealed class RecordingService : IRunService
    {
        public long? LastRunId    { get; private set; }
        public int?  LastQueries  { get; private set; }
        public int?  LastAds      { get; private set; }
        public int?  LastCaptchas { get; private set; }

        public Task UpdateCountersAsync(
            long runId, int totalQueries, int totalAds, int captchas,
            CancellationToken ct = default)
        {
            LastRunId    = runId;
            LastQueries  = totalQueries;
            LastAds      = totalAds;
            LastCaptchas = captchas;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GhostShell.Core.Models.Run>> ListAsync(
            int limit = 50, string? profileName = null,
            RunStatusFilter status = RunStatusFilter.All,
            int? sinceHours = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<GhostShell.Core.Models.Run?> GetAsync(long runId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<RunStats> GetStatsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<long> StartAsync(string profileName, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task FinishAsync(long runId, int exitCode,
            string? lastError = null, string? stopReason = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task HeartbeatAsync(long runId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkFailedAsync(long runId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> ClearAsync(DateTime? olderThan, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> DeleteAsync(long runId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
