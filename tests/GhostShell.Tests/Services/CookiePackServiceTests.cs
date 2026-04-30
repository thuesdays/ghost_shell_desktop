// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Xunit;

namespace GhostShell.Tests.Services;

/// <summary>
/// CookiePackService — gzipped JSON BLOB round-trip, UPSERT on slug,
/// and the live-session "apply" path. The apply test uses a fake
/// IBrowserSession so we don't need Selenium.
/// </summary>
public class CookiePackServiceTests : IDisposable
{
    private readonly ServiceFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Upsert_RoundTripsViaGzippedBlob()
    {
        var meta = new CookiePack
        {
            Slug    = "google-uk-30d",
            Label   = "Google UK · 30-day",
            Domains = [ "google.com", "youtube.com" ],
            AgeDays     = 30,
            CaptchaRate = 0.04,
        };
        var payload = new SessionPayload
        {
            Cookies =
            [
                new CookieEntry { Name = "NID", Value = "x", Domain = ".google.com",
                    Secure = true, HttpOnly = true, SameSite = "None" },
            ],
            Storage =
            [
                new StorageEntry
                {
                    Origin       = "https://www.google.com",
                    LocalStorage = new Dictionary<string, string> { ["k"] = "v" },
                },
            ],
        };

        var id = await _fx.Packs.UpsertAsync(meta, payload);

        var got = await _fx.Packs.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal("google-uk-30d",       got!.Slug);
        Assert.Equal("Google UK · 30-day",  got.Label);
        Assert.Contains("google.com",       got.Domains);
        Assert.Equal(2,                     got.Domains.Count);
        Assert.Equal(30,                    got.AgeDays);
        Assert.Equal(0.04,                  got.CaptchaRate, 5);
        Assert.Equal(1,                     got.CookiesCount);
        Assert.Equal(1,                     got.StorageCount);

        var fetched = await _fx.Packs.GetPayloadAsync(id);
        Assert.NotNull(fetched);
        Assert.Single(fetched!.Cookies);
        Assert.True  (fetched.Cookies[0].Secure);
        Assert.Equal ("None", fetched.Cookies[0].SameSite);
        Assert.Single(fetched.Storage);
        Assert.Equal ("v", fetched.Storage[0].LocalStorage["k"]);
    }

    [Fact]
    public async Task Upsert_OverwritesOnSameSlug()
    {
        var slug = "dup";
        var first = await _fx.Packs.UpsertAsync(
            new CookiePack { Slug = slug, Label = "v1", Domains = [] },
            SessionPayload.Empty);
        var second = await _fx.Packs.UpsertAsync(
            new CookiePack { Slug = slug, Label = "v2", Domains = [] },
            SessionPayload.Empty);

        // Same row id — UPSERT-on-slug, not duplicated.
        Assert.Equal(first, second);
        var all = await _fx.Packs.ListAsync();
        Assert.Single(all);
        Assert.Equal("v2", all[0].Label);
    }

    [Fact]
    public async Task Delete_RemovesPackAndPayload()
    {
        var id = await _fx.Packs.UpsertAsync(
            new CookiePack { Slug = "tmp", Label = "tmp", Domains = [] },
            SessionPayload.Empty);

        await _fx.Packs.DeleteAsync(id);

        Assert.Null(await _fx.Packs.GetAsync(id));
        Assert.Null(await _fx.Packs.GetPayloadAsync(id));
    }

    [Fact]
    public async Task Apply_PushesCookiesAndStorageToSession()
    {
        var payload = new SessionPayload
        {
            Cookies =
            [
                new CookieEntry { Name = "a", Value = "1", Domain = ".x.com" },
                new CookieEntry { Name = "b", Value = "2", Domain = ".y.com" },
            ],
            Storage =
            [
                new StorageEntry { Origin = "https://x.com",
                    LocalStorage = new Dictionary<string, string> { ["k"] = "v" } },
            ],
        };
        var id = await _fx.Packs.UpsertAsync(
            new CookiePack { Slug = "p", Label = "p", Domains = [ "x.com", "y.com" ] },
            payload);

        var fake = new RecordingSession();
        var result = await _fx.Packs.ApplyAsync(id, fake);

        Assert.Equal(2, result.CookiesSet);
        Assert.Equal(1, result.StorageOriginsSet);
        Assert.Equal(2, fake.SetCookiesCalls);
        Assert.Equal(1, fake.SetStorageCalls);
        // Cookies preserved through the round-trip.
        Assert.Contains(fake.SetCookies, c => c.Name == "a" && c.Domain == ".x.com");
    }

    [Fact]
    public async Task Apply_ThrowsOnUnknownPack()
    {
        var fake = new RecordingSession();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _fx.Packs.ApplyAsync(99999, fake));
    }

    [Fact]
    public async Task ExportFromSession_CapturesCookiesAndDerivesOrigins()
    {
        var fake = new RecordingSession
        {
            ReadCookies =
            [
                new CookieEntry { Name = "n", Value = "1", Domain = ".example.com" },
                new CookieEntry { Name = "m", Value = "2", Domain = ".example.com" },
                new CookieEntry { Name = "o", Value = "3", Domain = ".other.com" },
            ],
            // Storage map keyed by origin; the service derives the
            // origin list from cookie domains then queries storage.
            ReadStorage = new Dictionary<string, StorageEntry>
            {
                ["https://example.com"] = new StorageEntry
                {
                    Origin       = "https://example.com",
                    LocalStorage = new Dictionary<string, string> { ["x"] = "y" },
                },
            },
        };

        var id = await _fx.Packs.ExportFromSessionAsync(
            "exp", "Export test", fake);

        var meta    = await _fx.Packs.GetAsync(id);
        var payload = await _fx.Packs.GetPayloadAsync(id);

        Assert.NotNull(meta);
        Assert.Equal("exp",         meta!.Slug);
        Assert.Equal(3,             meta.CookiesCount);
        Assert.Contains("example.com", meta.Domains);
        Assert.Contains("other.com",   meta.Domains);
        Assert.NotNull(payload);
        Assert.Equal(3, payload!.Cookies.Count);
        Assert.Single(payload.Storage);
    }

    /// <summary>
    /// In-memory IBrowserSession that records calls and serves
    /// scripted reads. We don't fake the rest of IBrowserSession's
    /// surface (NavigateAsync etc.) because the service paths under
    /// test never hit those.
    /// </summary>
    private sealed class RecordingSession : IBrowserSession
    {
        public string ProfileName => "test";
        public long RunId => 0;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public bool IsAlive => true;

        public IReadOnlyList<CookieEntry> ReadCookies { get; set; }
            = Array.Empty<CookieEntry>();
        public IDictionary<string, StorageEntry> ReadStorage { get; set; }
            = new Dictionary<string, StorageEntry>();

        public List<CookieEntry> SetCookies { get; } = new();
        public int SetCookiesCalls { get; private set; }
        public int SetStorageCalls { get; private set; }

        public Task NavigateAsync(string url, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetTitleAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>("");

        public Task<IReadOnlyList<CookieEntry>> GetCookiesAsync(CancellationToken ct = default) =>
            Task.FromResult(ReadCookies);

        public Task SetCookiesAsync(IEnumerable<CookieEntry> cookies, CancellationToken ct = default)
        {
            foreach (var c in cookies) { SetCookies.Add(c); SetCookiesCalls++; }
            return Task.CompletedTask;
        }

        public Task ClearCookiesAsync(CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StorageEntry>> GetStorageAsync(
            IEnumerable<string> origins, CancellationToken ct = default)
        {
            var result = origins
                .Where(o => ReadStorage.ContainsKey(o))
                .Select(o => ReadStorage[o])
                .ToList();
            return Task.FromResult<IReadOnlyList<StorageEntry>>(result);
        }

        public Task SetStorageAsync(IEnumerable<StorageEntry> entries, CancellationToken ct = default)
        {
            SetStorageCalls += entries.Count();
            return Task.CompletedTask;
        }

        // CookiePack code paths never run JS; method is required by
        // the expanded IBrowserSession contract added for the Phase-6
        // warmup robot. Fake returns null.
        public Task<object?> ExecuteScriptAsync(
            string script, object[]? args = null, CancellationToken ct = default) =>
            Task.FromResult<object?>(null);

        public Task<string> CaptureScreenshotAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(path);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
