// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Runtime.Browser;
using GhostShell.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GhostShell.Tests.Browser;

/// <summary>
/// End-to-end behaviour of the SessionLifecycle hook plumbed against
/// a real SessionService. Verifies that:
///
///   • RestoreLatestAsync no-ops on a profile with no snapshots
///   • RestoreLatestAsync pushes the latest snapshot's cookies +
///     storage into the live session
///   • CaptureCleanRunAsync skips when the session reports zero
///     cookies (nothing worth saving)
///   • CaptureCleanRunAsync persists a fresh row tagged
///     <c>auto_clean_run</c> when there are cookies
///
/// Failure paths (driver dies mid-call) are tested by injecting an
/// IBrowserSession that throws — the lifecycle should swallow,
/// log, and let teardown proceed.
/// </summary>
public class SessionLifecycleTests : IDisposable
{
    private readonly ServiceFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    private SessionLifecycle MakeLifecycle() =>
        new(_fx.Sessions, NullLogger<SessionLifecycle>.Instance);

    [Fact]
    public async Task RestoreLatest_NoOpWhenNoSnapshots()
    {
        var lifecycle = MakeLifecycle();
        var session   = new ScriptedSession("fresh-profile");

        await lifecycle.RestoreLatestAsync(session);

        Assert.Empty(session.AppliedCookies);
        Assert.Empty(session.AppliedStorage);
    }

    [Fact]
    public async Task RestoreLatest_PushesLatestPayload()
    {
        await _fx.Sessions.SaveAsync(
            "p1",
            new SessionPayload
            {
                Cookies =
                [
                    new CookieEntry { Name = "x", Value = "1", Domain = ".y.com" },
                ],
                Storage =
                [
                    new StorageEntry
                    {
                        Origin       = "https://y.com",
                        LocalStorage = new Dictionary<string, string> { ["k"] = "v" },
                    },
                ],
            },
            null, "manual");

        var lifecycle = MakeLifecycle();
        var session   = new ScriptedSession("p1");

        await lifecycle.RestoreLatestAsync(session);

        Assert.Single(session.AppliedCookies);
        Assert.Equal("x", session.AppliedCookies[0].Name);
        Assert.Single(session.AppliedStorage);
    }

    [Fact]
    public async Task CaptureCleanRun_SkipsWhenNoCookies()
    {
        var lifecycle = MakeLifecycle();
        var session   = new ScriptedSession("p1") { Cookies = [] };

        await lifecycle.CaptureCleanRunAsync(session, runId: 1);

        var snaps = await _fx.Sessions.ListAsync("p1");
        Assert.Empty(snaps);
    }

    [Fact]
    public async Task CaptureCleanRun_PersistsSnapshotWithRunId()
    {
        var lifecycle = MakeLifecycle();
        var session   = new ScriptedSession("p1")
        {
            Cookies =
            [
                new CookieEntry { Name = "a", Value = "1", Domain = ".example.com" },
            ],
            Storage = new Dictionary<string, StorageEntry>
            {
                ["https://example.com"] = new StorageEntry
                {
                    Origin = "https://example.com",
                    LocalStorage = new Dictionary<string, string> { ["k"] = "v" },
                },
            },
        };

        await lifecycle.CaptureCleanRunAsync(session, runId: 7);

        var snaps = await _fx.Sessions.ListAsync("p1");
        Assert.Single(snaps);
        Assert.Equal("auto_clean_run", snaps[0].Trigger);
        Assert.Equal(7L,               snaps[0].RunId);
        Assert.Equal(1,                snaps[0].CookieCount);
    }

    [Fact]
    public async Task RestoreLatest_SwallowsDriverFailure()
    {
        await _fx.Sessions.SaveAsync(
            "p1",
            new SessionPayload
            {
                Cookies = [ new CookieEntry { Name = "x", Value = "1", Domain = ".x.com" } ],
            },
            null, "manual");

        var lifecycle = MakeLifecycle();
        // Session whose SetCookies throws — emulates dead driver.
        var session = new ScriptedSession("p1") { ThrowOnSet = true };

        // Must not throw — runtime hook is best-effort.
        await lifecycle.RestoreLatestAsync(session);
    }

    [Fact]
    public async Task CaptureCleanRun_SwallowsDriverFailure()
    {
        var lifecycle = MakeLifecycle();
        var session   = new ScriptedSession("p1") { ThrowOnGet = true };

        await lifecycle.CaptureCleanRunAsync(session, runId: 1);
        // No snapshot persisted (the GetCookiesAsync threw).
        Assert.Empty(await _fx.Sessions.ListAsync("p1"));
    }

    /// <summary>
    /// Faked-out IBrowserSession with hand-written replies for each
    /// method the lifecycle calls. Knobs let a test simulate driver
    /// failure on either Get* or Set*.
    /// </summary>
    private sealed class ScriptedSession : IBrowserSession
    {
        public ScriptedSession(string profileName) { ProfileName = profileName; }

        public string ProfileName { get; }
        public long RunId => 0;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public bool IsAlive => true;

        public IReadOnlyList<CookieEntry> Cookies { get; init; } = Array.Empty<CookieEntry>();
        public IDictionary<string, StorageEntry> Storage { get; init; }
            = new Dictionary<string, StorageEntry>();
        public bool ThrowOnGet { get; init; }
        public bool ThrowOnSet { get; init; }

        public List<CookieEntry>  AppliedCookies { get; } = new();
        public List<StorageEntry> AppliedStorage { get; } = new();

        public Task NavigateAsync(string url, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetTitleAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>("");

        public Task<IReadOnlyList<CookieEntry>> GetCookiesAsync(CancellationToken ct = default)
        {
            if (ThrowOnGet) throw new InvalidOperationException("driver dead");
            return Task.FromResult(Cookies);
        }
        public Task SetCookiesAsync(IEnumerable<CookieEntry> cookies, CancellationToken ct = default)
        {
            if (ThrowOnSet) throw new InvalidOperationException("driver dead");
            AppliedCookies.AddRange(cookies);
            return Task.CompletedTask;
        }
        public Task ClearCookiesAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<StorageEntry>> GetStorageAsync(
            IEnumerable<string> origins, CancellationToken ct = default)
        {
            if (ThrowOnGet) throw new InvalidOperationException("driver dead");
            var result = origins
                .Where(o => Storage.ContainsKey(o))
                .Select(o => Storage[o])
                .ToList();
            return Task.FromResult<IReadOnlyList<StorageEntry>>(result);
        }
        public Task SetStorageAsync(IEnumerable<StorageEntry> entries, CancellationToken ct = default)
        {
            if (ThrowOnSet) throw new InvalidOperationException("driver dead");
            AppliedStorage.AddRange(entries);
            return Task.CompletedTask;
        }

        // SessionLifecycle never executes scripts; method is required
        // by the expanded IBrowserSession contract added for the
        // Phase-6 warmup robot. Fake returns null.
        public Task<object?> ExecuteScriptAsync(
            string script, object[]? args = null, CancellationToken ct = default) =>
            Task.FromResult<object?>(null);

        public Task<string> CaptureScreenshotAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(path);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
