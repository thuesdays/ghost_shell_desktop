// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using GhostShell.Data.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GhostShell.Tests.Services;

/// <summary>
/// SessionService round-trip tests against a real on-disk SQLite
/// file (in a temp dir). In-memory SQLite would be cheaper but the
/// service uses our DatabaseConnection wrapper which expects a
/// file path; spinning up the migration runner is the price of
/// covering the actual code path consumers hit.
///
/// Each test gets its own DB file via <see cref="ServiceFixture"/>
/// so tests don't bleed state into each other.
/// </summary>
public class SessionServiceTests : IDisposable
{
    private readonly ServiceFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task SaveAndGet_RoundTripsPayload()
    {
        var payload = new SessionPayload
        {
            Cookies =
            [
                new CookieEntry { Name = "NID", Value = "abc", Domain = ".google.com" },
                new CookieEntry { Name = "SID", Value = "xyz", Domain = ".google.com",
                    Secure = true, HttpOnly = true, ExpiresUnixSec = 1_800_000_000 },
            ],
            Storage =
            [
                new StorageEntry
                {
                    Origin       = "https://www.google.com",
                    LocalStorage = new Dictionary<string, string> { ["theme"] = "dark" },
                },
            ],
        };

        var id = await _fx.Sessions.SaveAsync(
            "profile_a", payload, runId: 42, trigger: "manual", reason: "unit test");

        var meta = await _fx.Sessions.GetAsync(id);
        Assert.NotNull(meta);
        Assert.Equal("profile_a", meta!.ProfileName);
        Assert.Equal(2,           meta.CookieCount);
        Assert.Equal("manual",    meta.Trigger);
        Assert.Equal(42,          meta.RunId);

        var fetched = await _fx.Sessions.GetPayloadAsync(id);
        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.Cookies.Count);
        Assert.Equal("abc",        fetched.Cookies[0].Value);
        Assert.True(fetched.Cookies[1].Secure);
        Assert.True(fetched.Cookies[1].HttpOnly);
        Assert.Equal(1_800_000_000L, fetched.Cookies[1].ExpiresUnixSec);
        Assert.Single(fetched.Storage);
        Assert.Equal("dark", fetched.Storage[0].LocalStorage["theme"]);
    }

    [Fact]
    public async Task ListByProfile_FiltersToMatch()
    {
        await _fx.Sessions.SaveAsync("profile_a", SessionPayload.Empty,
            null, "auto_clean_run");
        await _fx.Sessions.SaveAsync("profile_b", SessionPayload.Empty,
            null, "auto_clean_run");
        await _fx.Sessions.SaveAsync("profile_a", SessionPayload.Empty,
            null, "manual");

        var a = await _fx.Sessions.ListAsync("profile_a");
        var b = await _fx.Sessions.ListAsync("profile_b");
        var all = await _fx.Sessions.ListAsync();

        Assert.Equal(2, a.Count);
        Assert.Single(b);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task GetLatest_PicksMostRecentForProfile()
    {
        var first  = await _fx.Sessions.SaveAsync("p", SessionPayload.Empty, null, "manual");
        await Task.Delay(15); // ensure created_at differs (ISO ms granularity)
        var second = await _fx.Sessions.SaveAsync("p", SessionPayload.Empty, null, "manual");

        var latest = await _fx.Sessions.GetLatestAsync("p");
        Assert.NotNull(latest);
        Assert.Equal(second, latest!.Id);
        Assert.NotEqual(first, latest.Id);
    }

    [Fact]
    public async Task GetLatest_NullForUnknownProfile()
    {
        var latest = await _fx.Sessions.GetLatestAsync("never-existed");
        Assert.Null(latest);
    }

    [Fact]
    public async Task Delete_RemovesRowAndPayload()
    {
        var id = await _fx.Sessions.SaveAsync("p",
            new SessionPayload { Cookies = [ new CookieEntry { Name = "x", Value = "1", Domain = ".x.com" } ] },
            null, "manual");

        await _fx.Sessions.DeleteAsync(id);

        Assert.Null(await _fx.Sessions.GetAsync(id));
        Assert.Null(await _fx.Sessions.GetPayloadAsync(id));
    }

    [Fact]
    public async Task DomainCount_DedupesLeadingDot()
    {
        // Cookies with ".google.com" and "google.com" should count
        // as ONE domain — leading-dot is the legacy "all subdomains"
        // marker, not a different host.
        var payload = new SessionPayload
        {
            Cookies =
            [
                new CookieEntry { Name = "a", Value = "1", Domain = ".google.com" },
                new CookieEntry { Name = "b", Value = "2", Domain = "google.com"  },
                new CookieEntry { Name = "c", Value = "3", Domain = ".youtube.com" },
            ],
        };

        var id   = await _fx.Sessions.SaveAsync("p", payload, null, "manual");
        var meta = await _fx.Sessions.GetAsync(id);

        Assert.Equal(2, meta!.DomainCount);
        Assert.Equal(3, meta.CookieCount);
    }

    [Fact]
    public async Task ConcurrentSaves_AllSurvive()
    {
        // Six saves in flight at once — exercises the QueueAsync
        // semaphore: without serialization, SQLite would throw
        // "database is locked" or "DataReader open" on the shared
        // connection.
        var tasks = Enumerable.Range(0, 6).Select(i =>
            _fx.Sessions.SaveAsync($"p{i % 2}", SessionPayload.Empty, null, "manual"));
        var ids = await Task.WhenAll(tasks);

        Assert.Equal(6,                ids.Length);
        Assert.Equal(6,                ids.Distinct().Count());
        Assert.Equal(6, (await _fx.Sessions.ListAsync()).Count);
    }
}

/// <summary>
/// Spins up a fresh DB file + DatabaseConnection + applied
/// migrations for one test. Each fixture instance is single-use;
/// dispose deletes the file. Used by SessionService and
/// CookiePackService tests.
/// </summary>
internal sealed class ServiceFixture : IDisposable
{
    public string DbPath { get; }
    public DatabaseConnection Db { get; }
    public ISessionService    Sessions { get; }
    public ICookiePackService Packs    { get; }

    public ServiceFixture()
    {
        DbPath = Path.Combine(Path.GetTempPath(), $"ghostshell-test-{Guid.NewGuid():N}.db");
        Db = new DatabaseConnection(DbPath, NullLogger<DatabaseConnection>.Instance);
        new MigrationRunner(Db, NullLogger<MigrationRunner>.Instance).Run();

        Sessions = new SessionService(Db, NullLogger<SessionService>.Instance);
        Packs    = new CookiePackService(Db, NullLogger<CookiePackService>.Instance);
    }

    public void Dispose()
    {
        Db.Dispose();
        // SQLite's pooled connections may keep the file alive a tick
        // after close — try a few times before giving up.
        for (var i = 0; i < 5; i++)
        {
            try { File.Delete(DbPath); break; }
            catch (IOException)              { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}
