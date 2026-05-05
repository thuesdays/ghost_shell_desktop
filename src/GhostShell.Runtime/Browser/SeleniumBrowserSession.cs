// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Concrete <see cref="IBrowserSession"/> backed by Selenium 4's
/// <see cref="ChromeDriver"/>. Owns the WebDriver, the
/// <see cref="ChromeDriverService"/> (chromedriver.exe child
/// process), and tracks any chrome.exe PIDs spawned during launch
/// so DisposeAsync can clean them up reliably.
///
/// Exposes only the high-level surface defined by IBrowserSession;
/// the raw IWebDriver stays internal so future swaps (Playwright,
/// raw CDP) don't ripple through the consuming layers.
/// </summary>
internal sealed class SeleniumBrowserSession : IBrowserSession
{
    private readonly IWebDriver _driver;
    private readonly ChromeDriverService _service;
    private readonly ILogger _log;
    private readonly List<int> _ownedPids;
    private readonly IProxyAuthForwarder? _forwarder;
    private readonly GhostShell.Runtime.Traffic.TrafficCollector? _trafficCollector;
    private bool _disposed;

    public string ProfileName { get; }
    public long RunId { get; }
    public DateTime StartedAt { get; }

    public bool IsAlive => !_disposed && _service.IsRunning;

    public SeleniumBrowserSession(
        string profileName, long runId,
        IWebDriver driver, ChromeDriverService service,
        IEnumerable<int> ownedPids,
        IProxyAuthForwarder? forwarder,
        ILogger log,
        ITrafficService? traffic = null)
    {
        ProfileName = profileName;
        RunId       = runId;
        StartedAt   = DateTime.UtcNow;
        _driver     = driver;
        _service    = service;
        _ownedPids  = ownedPids.ToList();
        _forwarder  = forwarder;
        _log        = log;

        // Phase 28/31 — start the traffic collector. The CDP counter
        // is created for EVERY session (proxied or not) so direct
        // connections still get bandwidth accounting via Chrome's
        // PerformanceObserver. The forwarder is layered on top for
        // proxied profiles where its TCP-level counts are more
        // accurate. The collector merges both via MAX. When neither
        // a forwarder nor a CDP-able driver is present, no collector
        // is created and the dashboard just shows 0 for that session.
        if (traffic is not null && driver is OpenQA.Selenium.Chrome.ChromeDriver chrome)
        {
            GhostShell.Runtime.Traffic.CdpTrafficCounter? cdp = null;
            try
            {
                cdp = new GhostShell.Runtime.Traffic.CdpTrafficCounter(chrome, log);
                cdp.Start();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "CDP traffic counter setup failed for '{Name}' — direct-connection traffic won't be counted", profileName);
                cdp = null;
            }
            _trafficCollector = new GhostShell.Runtime.Traffic.TrafficCollector(
                forwarder, traffic, profileName, runId, log, cdp);
            _trafficCollector.Start();
        }
    }

    public Task NavigateAsync(string url, CancellationToken ct = default)
    {
        // WebDriver is synchronous; offload so the caller's await
        // doesn't pin the UI thread. Each Navigate is bounded by
        // Chromium's pageLoadTimeout (set in ChromeDriver ctor).
        return Task.Run(() => _driver.Navigate().GoToUrl(url), ct);
    }

    public Task<string?> GetTitleAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try { return _driver.Title; }
            catch { return null; }
        }, ct);
    }

    // ─────────────────────────────────────────────────────────────
    // Cookie I/O — routes through CDP for the bulk operations.
    // The CDP path is meaningfully better than driver.Manage().
    // Cookies because:
    //   • Network.getAllCookies returns cookies for EVERY domain the
    //     browser knows, not just the current page (driver.GetCookies
    //     is page-scoped).
    //   • Network.setCookies is bulk + domain-aware; driver.AddCookie
    //     requires you to be navigated to the cookie's domain first.
    // ─────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<CookieEntry>> GetCookiesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<CookieEntry>>(() =>
        {
            try
            {
                var resp = ExecCdp("Network.getAllCookies", new Dictionary<string, object>());
                if (resp is null || !resp.TryGetValue("cookies", out var raw))
                    return Array.Empty<CookieEntry>();
                var list = raw as IEnumerable<object>;
                if (list is null) return Array.Empty<CookieEntry>();

                var result = new List<CookieEntry>();
                foreach (var c in list.OfType<Dictionary<string, object>>())
                    result.Add(MapCdpCookieToEntry(c));
                return result;
            }
            catch (OpenQA.Selenium.NoSuchWindowException)
            {
                // Phase 70 — expected when the window was closed by the
                // user or torn down by the watchdog. No need to dump a
                // stack trace; just info-log and return empty so the
                // run-tail snapshot path treats this as "no cookies
                // available" rather than an error.
                _log.LogInformation(
                    "GetCookies skipped for '{Profile}': window already closed",
                    ProfileName);
                return Array.Empty<CookieEntry>();
            }
            catch (Exception ex) when (
                ex.Message?.Contains("invalid session id", StringComparison.OrdinalIgnoreCase) == true
             || ex.Message?.Contains("session deleted", StringComparison.OrdinalIgnoreCase) == true
             || ex.Message?.Contains("not connected to DevTools", StringComparison.OrdinalIgnoreCase) == true)
            {
                _log.LogInformation(
                    "GetCookies skipped for '{Profile}': session already gone ({Msg})",
                    ProfileName, ex.Message);
                return Array.Empty<CookieEntry>();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GetCookies via CDP failed for '{Profile}'", ProfileName);
                return Array.Empty<CookieEntry>();
            }
        }, ct);

    public Task SetCookiesAsync(IEnumerable<CookieEntry> cookies, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            // Network.setCookies takes an array under the "cookies" key.
            // We chunk to keep individual CDP messages reasonable in
            // size — a 30-day Google profile can carry 200+ cookies
            // and the message wire format is verbose.
            const int chunk = 100;
            var batch = new List<Dictionary<string, object>>(chunk);
            foreach (var c in cookies)
            {
                batch.Add(MapEntryToCdpCookie(c));
                if (batch.Count >= chunk)
                {
                    ExecCdp("Network.setCookies", new Dictionary<string, object> { ["cookies"] = batch });
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                ExecCdp("Network.setCookies", new Dictionary<string, object> { ["cookies"] = batch });
        }, ct);

    public Task ClearCookiesAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try { ExecCdp("Network.clearBrowserCookies", new Dictionary<string, object>()); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ClearCookies via CDP failed for '{Profile}'", ProfileName);
            }
        }, ct);

    // ─────────────────────────────────────────────────────────────
    // Storage I/O — per-origin navigation + JS execution. There's no
    // CDP equivalent for arbitrary-origin localStorage R/W; you have
    // to be on the origin's page for `localStorage.setItem` to scope
    // correctly. Same approach as legacy session/manager.py.
    // ─────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<StorageEntry>> GetStorageAsync(
        IEnumerable<string> origins, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<StorageEntry>>(() =>
        {
            // Phase 50 — CDP DOMStorage instead of per-origin navigation.
            //
            // OLD behaviour: navigate to https://origin, then eval
            // localStorage. For a 50-domain profile that's 50 page
            // loads = ~45 seconds + visible "chaotic page opening"
            // for the user. Every snapshot save did this.
            //
            // NEW behaviour: enable CDP DOMStorage domain ONCE, then
            // call DOMStorage.getDOMStorageItems for each (origin,
            // isLocalStorage) pair. Zero page loads, runs entirely
            // through DevTools protocol — invisible to the user.
            //
            // Compatibility note: getDOMStorageItems works for ANY
            // security origin Chrome has data for, even origins
            // we've never visited in THIS session — Chrome reads
            // straight from the on-disk leveldb store.
            var result = new List<StorageEntry>();
            if (_driver is not ChromeDriver chromeDriver) return result;
            try { chromeDriver.ExecuteCdpCommand("DOMStorage.enable", new Dictionary<string, object>()); }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "DOMStorage.enable failed — falling through with empty result");
                return result;
            }
            foreach (var origin in origins.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (!IsHttpOrigin(origin)) continue;
                    var local   = ReadStorageViaCdp(chromeDriver, origin, isLocalStorage: true);
                    var session = ReadStorageViaCdp(chromeDriver, origin, isLocalStorage: false);
                    if (local.Count == 0 && session.Count == 0) continue;
                    result.Add(new StorageEntry
                    {
                        Origin         = origin,
                        LocalStorage   = local,
                        SessionStorage = session,
                    });
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Storage read skipped for origin {Origin}", origin);
                }
            }
            return result;
        }, ct);

    /// <summary>
    /// Read DOMStorage entries for (origin, isLocalStorage) via CDP
    /// — no navigation needed. The CDP response shape is:
    ///   { "entries": [["key1","val1"], ["key2","val2"], ...] }
    /// where entries[N] is either a List&lt;object&gt; or an
    /// object[] depending on the Selenium driver version. Both are
    /// IEnumerable&lt;object&gt; with [0]=key, [1]=value as strings.
    /// </summary>
    private static Dictionary<string, string> ReadStorageViaCdp(
        ChromeDriver driver, string origin, bool isLocalStorage)
    {
        var dict = new Dictionary<string, string>();
        try
        {
            var storageId = new Dictionary<string, object>
            {
                ["securityOrigin"] = origin,
                ["isLocalStorage"] = isLocalStorage,
            };
            var args = new Dictionary<string, object> { ["storageId"] = storageId };
            var resp = driver.ExecuteCdpCommand("DOMStorage.getDOMStorageItems", args)
                       as IDictionary<string, object>;
            if (resp is null) return dict;
            if (!resp.TryGetValue("entries", out var entriesObj)) return dict;
            if (entriesObj is not System.Collections.IEnumerable entries) return dict;
            foreach (var entry in entries)
            {
                if (entry is not System.Collections.IList kv) continue;
                if (kv.Count < 2) continue;
                var key = kv[0]?.ToString() ?? "";
                var val = kv[1]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(key)) dict[key] = val;
            }
        }
        catch
        {
            // Per-origin failures are non-fatal. ICU errors / origin
            // not in store / DOMStorage temporarily unavailable —
            // empty dict is the safe default.
        }
        return dict;
    }

    public Task SetStorageAsync(IEnumerable<StorageEntry> entries, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            // Phase 50 — CDP DOMStorage.setDOMStorageItem instead of
            // per-origin navigation + JS. Zero page loads to write
            // hundreds of localStorage entries across dozens of
            // origins. Same trade-off as GetStorageAsync above.
            if (_driver is not ChromeDriver chromeDriver) return;
            try { chromeDriver.ExecuteCdpCommand("DOMStorage.enable", new Dictionary<string, object>()); }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "DOMStorage.enable failed — storage restore skipped");
                return;
            }
            foreach (var e in entries)
            {
                if (ct.IsCancellationRequested) return;
                if (!IsHttpOrigin(e.Origin)) continue;
                if (e.LocalStorage.Count == 0 && e.SessionStorage.Count == 0) continue;

                try
                {
                    foreach (var kv in e.LocalStorage)
                        WriteStorageViaCdp(chromeDriver, e.Origin, true, kv.Key, kv.Value);
                    foreach (var kv in e.SessionStorage)
                        WriteStorageViaCdp(chromeDriver, e.Origin, false, kv.Key, kv.Value);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Storage write skipped for origin {Origin}", e.Origin);
                }
            }
        }, ct);

    /// <summary>
    /// Write a single (key, value) into the DOMStorage entry table
    /// for (origin, isLocalStorage) via CDP. Per-key call because
    /// CDP's setDOMStorageItem takes a single pair; high-volume
    /// storage restore (hundreds of items) still adds zero page
    /// loads — each call is a small DevTools round-trip (&lt;5ms).
    /// </summary>
    private static void WriteStorageViaCdp(
        ChromeDriver driver, string origin, bool isLocalStorage, string key, string value)
    {
        try
        {
            var storageId = new Dictionary<string, object>
            {
                ["securityOrigin"] = origin,
                ["isLocalStorage"] = isLocalStorage,
            };
            var args = new Dictionary<string, object>
            {
                ["storageId"] = storageId,
                ["key"]       = key,
                ["value"]     = value,
            };
            driver.ExecuteCdpCommand("DOMStorage.setDOMStorageItem", args);
        }
        catch
        {
            // Per-key write failures are non-fatal; the rest of the
            // restore continues. Common cause: origin's storage was
            // disabled by a Chrome policy.
        }
    }

    public Task<object?> ExecuteScriptAsync(
        string script, object[]? args = null, CancellationToken ct = default) =>
        Task.Run<object?>(() =>
        {
            // Selenium is sync; offload as we do for navigate. We
            // deliberately do NOT swallow exceptions here — the engine
            // wraps individual calls in try/catch with knowledge of
            // which failures are recoverable (consent-banner missing
            // is fine, scroll script error is fine), so it's wrong to
            // hide the distinction at this level.
            if (_driver is not IJavaScriptExecutor js) return null;
            return args is { Length: > 0 } a
                ? js.ExecuteScript(script, a)
                : js.ExecuteScript(script);
        }, ct);

    public Task<IReadOnlyList<string>> GetWindowHandlesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            // Selenium is sync — offload to thread pool. WindowHandles
            // is cheap (~5ms) but we keep the same off-thread pattern
            // as ExecuteScriptAsync to avoid blocking the recorder
            // poll loop's UI thread continuation.
            if (_driver is null) return Array.Empty<string>();
            return _driver.WindowHandles.ToList();
        }, ct);

    public Task<string> GetCurrentWindowHandleAsync(CancellationToken ct = default) =>
        Task.Run(() => _driver?.CurrentWindowHandle ?? "", ct);

    public Task SwitchToWindowAsync(string handle, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (_driver is null || string.IsNullOrEmpty(handle)) return;
            try { _driver.SwitchTo().Window(handle); }
            catch (OpenQA.Selenium.NoSuchWindowException)
            {
                // Caller is expected to re-enumerate after a NoSuchWindow.
                // Swallow rather than throw — the recorder's per-tick
                // window enumeration tolerates a window vanishing
                // between snapshot and switch (extension popups close
                // mid-poll all the time).
            }
        }, ct);

    public Task<string> CaptureScreenshotAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            // CDP returns PNG bytes as a base64 string. The legacy
            // Python uses driver.save_screenshot which round-trips
            // through Selenium's deprecated v3 API; the CDP path is
            // the modern equivalent and gives us back the raw bytes
            // synchronously without an extra disk write inside
            // chromedriver.
            var resp = ExecCdp("Page.captureScreenshot",
                new Dictionary<string, object>
                {
                    ["format"]      = "png",
                    ["fromSurface"] = true,
                });
            if (resp is null || !resp.TryGetValue("data", out var raw))
                throw new InvalidOperationException("CDP Page.captureScreenshot returned no 'data'");
            var b64 = raw?.ToString() ?? "";
            var bytes = Convert.FromBase64String(b64);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllBytes(path, bytes);
            return path;
        }, ct);

    // ─── CDP / JS helpers ────────────────────────────────────────

    private Dictionary<string, object>? ExecCdp(string command, Dictionary<string, object> args)
    {
        if (_driver is not ChromiumDriver cdp) return null;
        return cdp.ExecuteCdpCommand(command, args) as Dictionary<string, object>;
    }

    private void NavigateWithBudget(string url, TimeSpan budget)
    {
        // Bound the per-origin navigation: a slow site shouldn't be
        // able to hold up snapshot save / restore for the whole batch.
        // Selenium's PageLoadTimeout is the closest knob; restore
        // afterwards so it doesn't bleed into the rest of the run.
        var prev = _driver.Manage().Timeouts().PageLoad;
        try
        {
            _driver.Manage().Timeouts().PageLoad = budget;
            try { _driver.Navigate().GoToUrl(url); }
            catch (WebDriverException) { /* timeout / navigation aborted — proceed */ }
        }
        finally
        {
            try { _driver.Manage().Timeouts().PageLoad = prev; } catch { /* swallow */ }
        }
    }

    private IReadOnlyDictionary<string, string> ReadStorageDict(string storageName)
    {
        if (_driver is not IJavaScriptExecutor js) return new Dictionary<string, string>();
        // Build a flat object {key: value, ...} server-side so we
        // round-trip in one call. Returns null if the storage isn't
        // accessible (private mode, blocked, etc.).
        var script = $$"""
            try {
                var s = window.{{storageName}};
                if (!s) return {};
                var out = {};
                for (var i = 0; i < s.length; i++) {
                    var k = s.key(i);
                    out[k] = s.getItem(k);
                }
                return out;
            } catch (e) { return {}; }
        """;
        var result = js.ExecuteScript(script);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (result is Dictionary<string, object> d)
        {
            foreach (var kv in d)
                dict[kv.Key] = kv.Value?.ToString() ?? "";
        }
        return dict;
    }

    private void WriteStorageDict(string storageName, IReadOnlyDictionary<string, string> entries)
    {
        if (entries.Count == 0) return;
        if (_driver is not IJavaScriptExecutor js) return;

        // Pass the dict as a parameter so we don't have to escape
        // arbitrary user-content into a JS literal. Selenium's
        // ExecuteScript marshals .NET objects → JS for object[] args.
        var script = $$"""
            var s = window.{{storageName}};
            if (!s) return;
            var data = arguments[0];
            for (var k in data) {
                try { s.setItem(k, data[k]); } catch (e) { /* quota / blocked */ }
            }
        """;
        js.ExecuteScript(script, entries.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
    }

    private static bool IsHttpOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u)
        && (u.Scheme == "http" || u.Scheme == "https");

    private static CookieEntry MapCdpCookieToEntry(Dictionary<string, object> c)
    {
        long? expires = null;
        if (c.TryGetValue("expires", out var ex) && ex is not null)
        {
            // CDP returns a double (seconds since epoch). -1 / 0 = session.
            var d = Convert.ToDouble(ex);
            if (d > 0) expires = (long)d;
        }
        return new CookieEntry
        {
            Name           = c.TryGetValue("name",     out var n) ? n?.ToString() ?? "" : "",
            Value          = c.TryGetValue("value",    out var v) ? v?.ToString() ?? "" : "",
            Domain         = c.TryGetValue("domain",   out var dm) ? dm?.ToString() ?? "" : "",
            Path           = c.TryGetValue("path",     out var p) ? p?.ToString() ?? "/" : "/",
            Secure         = c.TryGetValue("secure",   out var s) && Convert.ToBoolean(s),
            HttpOnly       = c.TryGetValue("httpOnly", out var h) && Convert.ToBoolean(h),
            SameSite       = c.TryGetValue("sameSite", out var ss) ? ss?.ToString() : null,
            ExpiresUnixSec = expires,
        };
    }

    private static Dictionary<string, object> MapEntryToCdpCookie(CookieEntry c)
    {
        var d = new Dictionary<string, object>
        {
            ["name"]     = c.Name,
            ["value"]    = c.Value,
            ["domain"]   = c.Domain,
            ["path"]     = c.Path,
            ["secure"]   = c.Secure,
            ["httpOnly"] = c.HttpOnly,
        };
        if (c.ExpiresUnixSec is { } exp) d["expires"] = exp;
        if (!string.IsNullOrEmpty(c.SameSite)) d["sameSite"] = c.SameSite;
        return d;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Phase 28 — stop the traffic collector FIRST so any final
        // bytes pushed by chromedriver's shutdown handshake still get
        // billed before the forwarder closes its sockets. The collector
        // does its own final flush during DisposeAsync.
        if (_trafficCollector is not null)
        {
            try { await _trafficCollector.DisposeAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "TrafficCollector dispose threw"); }
        }

        // Quit driver first — Chromium typically exits cleanly when
        // WebDriver disconnects via CDP.
        try
        {
            _driver.Quit();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Driver.Quit() threw — continuing teardown");
        }

        // Stop chromedriver service.
        try
        {
            _service.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ChromeDriverService.Dispose() threw");
        }

        // Reap any chrome.exe PIDs we spawned but that didn't exit.
        // (Patched Chromium occasionally leaks workers when killed
        // mid-launch; mirroring legacy process_reaper.py here.)
        foreach (var pid in _ownedPids)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    _log.LogInformation(
                        "Reaping orphan chrome.exe pid={Pid} for profile '{Profile}'",
                        pid, ProfileName);
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (ArgumentException) { /* already gone */ }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Could not reap pid={Pid}", pid);
            }
        }

        // Tear down the auth-proxy forwarder LAST — connections from
        // chromedriver might still be flushing as the driver quits,
        // and pulling the forwarder out from under them would surface
        // as scary RST messages in the chromedriver log even though
        // they're harmless.
        if (_forwarder is not null)
        {
            try
            {
                await _forwarder.DisposeAsync();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Forwarder disposal threw — connection already gone");
            }
        }

        _log.LogInformation("Session for '{Profile}' (run #{Run}) closed", ProfileName, RunId);
    }
}
