// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium.Chrome;

namespace GhostShell.Runtime.Traffic;

/// <summary>
/// Phase 31 — CDP-based per-host byte counter. Used as a fallback (and
/// supplement) to <see cref="GhostShell.Runtime.ProxyAuth.HttpConnectForwarder"/>
/// when the profile launches WITHOUT a proxy — in that case the
/// forwarder never gets created and the Traffic dashboard records 0 B
/// no matter how much the user browses.
///
/// Strategy: subscribe to Chrome DevTools Network domain events. We
/// only need two:
///   • <c>Network.responseReceived</c> — gives us
///     <c>requestId → URL → host</c>.
///   • <c>Network.loadingFinished</c> — gives us
///     <c>requestId → encodedDataLength</c> (TCP-level wire size,
///     including TLS overhead and gzipped body).
/// On each loadingFinished we look up the host the requestId resolved
/// to in responseReceived, increment the host counter, and drop the
/// requestId from the map.
///
/// Implements <see cref="GhostShell.Core.Services.IProxyAuthForwarder"/>'s
/// counter shape so <see cref="TrafficCollector"/> can drain us with
/// the exact same code path it already uses for the forwarder.
/// </summary>
public sealed class CdpTrafficCounter : IDisposable
{
    private readonly ChromeDriver _driver;
    private readonly ILogger _log;
    private bool _started;

    /// <summary>requestId → host (lowercased). Populated on
    /// responseReceived, drained on loadingFinished.</summary>
    private readonly ConcurrentDictionary<string, string> _pending =
        new(StringComparer.Ordinal);

    private sealed class HostCounter
    {
        public long Bytes;
        public long Requests;
    }
    private readonly ConcurrentDictionary<string, HostCounter> _counters =
        new(StringComparer.OrdinalIgnoreCase);

    private HostCounter Get(string host) =>
        _counters.GetOrAdd(host, _ => new HostCounter());

    public CdpTrafficCounter(ChromeDriver driver, ILogger log)
    {
        _driver = driver;
        _log = log;
    }

    /// <summary>Subscribe to Network domain events. Idempotent.</summary>
    public void Start()
    {
        if (_started) return;
        try
        {
            _driver.ExecuteCdpCommand("Network.enable", new Dictionary<string, object>());
            // Selenium 4's Selenium.WebDriver wraps DevTools through
            // ChromeDriver.GetDevToolsSession(). We use the simpler
            // raw-event hook via WebDriver's logs is unreliable; the
            // best path that works against a vendored chromedriver is
            // INJECTING a small JS shim that mirrors the data we need
            // via a custom property + we sample on flush.
            //
            // A pure-CDP subscription via DevToolsSession.Domains.Network
            // requires the matching protocol-version package which the
            // vendored chromedriver may not align with. Instead we go
            // via JS PerformanceObserver — it's CDP-independent and
            // every modern Chromium ships it. We accept the cross-
            // origin transferSize=0 limitation (mirrors the legacy
            // web's fallback path) — at least request COUNTS are
            // accurate.
            const string ObserverJs = """
                (function() {
                  if (window.__gsTrafficObserverInstalled) return;
                  window.__gsTrafficObserverInstalled = true;
                  window.__gsTrafficBuf = [];
                  try {
                    const obs = new PerformanceObserver((list) => {
                      for (const e of list.getEntries()) {
                        try {
                          const u = new URL(e.name, location.href);
                          window.__gsTrafficBuf.push({
                            host:  u.hostname,
                            bytes: e.transferSize || e.encodedBodySize || 0,
                          });
                        } catch (err) { /* relative / data: URLs */ }
                      }
                      // cap so a long-running tab doesn't blow up.
                      if (window.__gsTrafficBuf.length > 5000)
                        window.__gsTrafficBuf.splice(0, window.__gsTrafficBuf.length - 2000);
                    });
                    obs.observe({type: 'resource', buffered: true});
                  } catch (err) { /* observer not available */ }
                })();
            """;
            _driver.ExecuteCdpCommand("Page.addScriptToEvaluateOnNewDocument",
                new Dictionary<string, object> { ["source"] = ObserverJs });
            // ALSO inject into the current page (about:blank at this point)
            // so a user already-running session starts counting.
            try { _driver.ExecuteScript(ObserverJs); }
            catch { /* current doc may not be ready yet — addScript covers next nav */ }
            _started = true;
            _log.LogDebug("CdpTrafficCounter started — PerformanceObserver injected");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "CdpTrafficCounter setup failed — direct-connection traffic won't be counted");
        }
    }

    /// <summary>Pull whatever the in-page observer has buffered since
    /// the last drain, fold it into our counters, and return the
    /// snapshot in the (Bytes, Requests) shape the collector expects.</summary>
    public IReadOnlyDictionary<string, (long Bytes, long Requests)> DrainCounters()
    {
        if (!_started) return new Dictionary<string, (long, long)>();

        try
        {
            // Drain buffer: returns AND CLEARS in one shot.
            const string DrainJs = """
                if (!window.__gsTrafficBuf) return [];
                var b = window.__gsTrafficBuf;
                window.__gsTrafficBuf = [];
                return JSON.stringify(b);
            """;
            var raw = _driver.ExecuteScript(DrainJs) as string;
            if (!string.IsNullOrEmpty(raw))
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in doc.RootElement.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object) continue;
                        var host = entry.TryGetProperty("host", out var h) ? h.GetString() : null;
                        var bytes = entry.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var bv) ? bv : 0;
                        if (string.IsNullOrWhiteSpace(host)) continue;
                        var counter = Get(host);
                        Interlocked.Add(ref counter.Bytes, bytes);
                        Interlocked.Increment(ref counter.Requests);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "CdpTrafficCounter drain failed (page may be navigating)");
        }

        // Snapshot + reset host counters.
        var snapshot = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _counters)
        {
            var bytes = Interlocked.Exchange(ref kv.Value.Bytes, 0);
            var reqs  = Interlocked.Exchange(ref kv.Value.Requests, 0);
            if (bytes == 0 && reqs == 0) continue;
            snapshot[kv.Key] = (bytes, reqs);
        }
        return snapshot;
    }

    public void Dispose()
    {
        _counters.Clear();
        _pending.Clear();
        _started = false;
    }
}
