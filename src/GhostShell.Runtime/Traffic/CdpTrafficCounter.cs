// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium.Chrome;

namespace GhostShell.Runtime.Traffic;

/// <summary>
/// Phase 31 — per-host byte ESTIMATOR for direct (no-proxy) browsing.
/// Used as a fallback (and supplement) to
/// <see cref="GhostShell.Runtime.ProxyAuth.HttpConnectForwarder"/>
/// when the profile launches WITHOUT a proxy — in that case the
/// forwarder never gets created and the Traffic dashboard records 0 B
/// no matter how much the user browses.
///
/// IMPORTANT — accuracy caveat (audit PROXY-08):
/// The original design intended a true CDP Network-domain subscription
/// (<c>Network.responseReceived</c> → requestId→host,
/// <c>Network.loadingFinished</c> → <c>encodedDataLength</c> = real
/// TCP-level wire size). That path requires the typed DevTools domain
/// API, whose protocol-version package must line up exactly with the
/// vendored chromedriver — which it is NOT guaranteed to here — so we
/// cannot rely on it without risking a hard failure on driver upgrades.
/// Instead this class injects a JS <c>PerformanceObserver</c> over the
/// <c>resource</c> timing buffer. That is CDP-independent and ships in
/// every modern Chromium, BUT the PerformanceResourceTiming spec ZEROES
/// every size field (<c>transferSize</c>/<c>encodedBodySize</c>/
/// <c>decodedBodySize</c>) for cross-origin responses that lack a
/// <c>Timing-Allow-Origin</c> header. Most third-party CDN/ad bytes are
/// exactly that case, so reported BYTES are a lower-bound ESTIMATE and
/// systematically under-count; only request COUNTS are reliable. We
/// surface this by tracking the opaque (zero-byte) request fraction and
/// logging the limitation once, so the dashboard total is never mistaken
/// for the wire-accurate figure the proxied path produces.
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

    // audit PROXY-08: track how many resources arrived size-opaque
    // (cross-origin, no Timing-Allow-Origin → bytes hidden by the spec)
    // vs. total, so the under-count is observable rather than silent.
    private long _opaqueRequests;
    private long _totalRequests;
    // Emit the accuracy caveat to the log at most once per instance.
    private int _limitationWarned;

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
            // audit PROXY-08: a true CDP Network-domain subscription
            // (DevToolsSession.Domains.Network → loadingFinished
            // .encodedDataLength = real wire size) is the only way to get
            // byte-accurate direct-connection accounting, but the typed
            // DevTools domain API is bound to a specific protocol-version
            // package that is NOT guaranteed to match the vendored
            // chromedriver here — wiring it in this file would risk a hard
            // runtime/compile break on driver upgrades. So we keep the
            // CDP-independent JS PerformanceObserver path and instead make
            // its limitation explicit (see DrainCounters + class doc).
            //
            // For each resource entry we record the best available size
            // signal AND a flag marking whether it was size-opaque (all
            // size fields 0 — i.e. cross-origin without Timing-Allow-Origin,
            // whose true bytes the spec hides from us). The flag lets the
            // drain side measure how much of the traffic is being
            // under-counted instead of silently reporting 0 as if real.
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
                          // transferSize already includes header + TLS
                          // overhead; fall back to body sizes when it's
                          // unavailable but the body sizes are exposed.
                          var size = e.transferSize || e.encodedBodySize || e.decodedBodySize || 0;
                          window.__gsTrafficBuf.push({
                            host:   u.hostname,
                            bytes:  size,
                            // true when the spec zeroed every size field
                            // (cross-origin, no Timing-Allow-Origin): the
                            // request is real but its byte count is hidden.
                            opaque: size === 0,
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

                        // audit PROXY-08: a request the spec exposed as
                        // size-opaque contributes a real request but 0
                        // bytes — record it so the under-count fraction is
                        // measurable instead of vanishing into the total.
                        var opaque = entry.TryGetProperty("opaque", out var o)
                            && o.ValueKind == JsonValueKind.True;
                        Interlocked.Increment(ref _totalRequests);
                        if (opaque || bytes == 0)
                            Interlocked.Increment(ref _opaqueRequests);
                    }

                    WarnOnSizeOpacityOnce();
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

    /// <summary>
    /// audit PROXY-08: once enough resources have been seen and a
    /// meaningful share of them are size-opaque (cross-origin without
    /// Timing-Allow-Origin → bytes hidden), log the accuracy caveat a
    /// single time so the operator knows the direct-connection byte
    /// total is a lower-bound estimate, not the wire-accurate figure the
    /// proxied path reports. Fires at most once per instance.
    /// </summary>
    private void WarnOnSizeOpacityOnce()
    {
        var total = Interlocked.Read(ref _totalRequests);
        if (total < 50) return; // wait for a representative sample
        var opaque = Interlocked.Read(ref _opaqueRequests);
        // Only warn if the under-counting is material (>25% of requests).
        if (opaque * 4 < total) return;
        if (Interlocked.Exchange(ref _limitationWarned, 1) != 0) return;
        _log.LogInformation(
            "CdpTrafficCounter: {Opaque}/{Total} direct-connection resources are size-opaque " +
            "(cross-origin without Timing-Allow-Origin); reported BYTES under-count actual wire " +
            "traffic — request counts remain accurate. Use a proxy for wire-accurate accounting.",
            opaque, total);
    }

    public void Dispose()
    {
        _counters.Clear();
        _pending.Clear();
        _started = false;
    }
}
