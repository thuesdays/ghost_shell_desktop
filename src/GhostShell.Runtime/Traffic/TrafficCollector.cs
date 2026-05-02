// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Traffic;

/// <summary>
/// Phase 28 — per-session traffic collector. Owns a background loop
/// that drains the per-host counters from <see cref="IProxyAuthForwarder"/>
/// every 30 s and rolls them up into <c>traffic_stats</c> via
/// <see cref="ITrafficService.WriteSamplesAsync"/>.
///
/// Started by <c>SeleniumBrowserSession</c> on session begin (only
/// when a forwarder is present — direct connections have no proxy
/// to bill). Stopped + final-flushed on session dispose so the last
/// few seconds of traffic land in the table even if the user closes
/// the browser before the next tick.
///
/// Lifetime ladder:
///   Session ctor  → new TrafficCollector(forwarder, ...)  → Start()
///   Session ops   → background tick every 30 s            → Flush()
///   Session Dispose → Stop() + final Flush() + DisposeAsync
/// </summary>
public sealed class TrafficCollector : IAsyncDisposable
{
    /// <summary>How often we drain the forwarder. 30s matches the
    /// legacy web. Shorter would be wasteful (most ticks would write
    /// an empty batch); longer would lose data on early app-quit.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    private readonly IProxyAuthForwarder? _forwarder;
    private readonly CdpTrafficCounter? _cdp;
    private readonly ITrafficService _traffic;
    private readonly string _profileName;
    private readonly long _runId;
    private readonly ILogger _log;

    private CancellationTokenSource? _stopCts;
    private Task? _loop;

    public TrafficCollector(
        IProxyAuthForwarder? forwarder,
        ITrafficService traffic,
        string profileName,
        long runId,
        ILogger log,
        CdpTrafficCounter? cdp = null)
    {
        _forwarder   = forwarder;
        _cdp         = cdp;
        _traffic     = traffic;
        _profileName = profileName;
        _runId       = runId;
        _log         = log;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        _stopCts = new CancellationTokenSource();
        _loop    = Task.Run(() => RunLoopAsync(_stopCts.Token));
        _log.LogDebug(
            "TrafficCollector started for profile='{Name}' run={Run}",
            _profileName, _runId);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(FlushInterval, ct); }
                catch (OperationCanceledException) { break; }
                await FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TrafficCollector loop exited with exception");
        }
    }

    /// <summary>Drain the forwarder + write to traffic_stats.
    /// Public so the session can request a final flush during Dispose.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        // Phase 31 — drain BOTH the proxy forwarder (authoritative
        // for HTTPS bodies via TCP-level counting) and the CDP /
        // PerformanceObserver fallback (works for direct connections
        // where there's no forwarder). Merge by SUMming bytes per
        // host — they observe DIFFERENT traffic so adding is correct
        // (forwarder sees only proxied requests; CDP sees all). When
        // both observe the SAME request (proxy + observer both
        // running), the legacy web's "MAX of both" rule prevents
        // double-counting; we replicate by taking max() per host.
        var counters = new Dictionary<string, (long Bytes, long Requests)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_forwarder is not null)
            {
                foreach (var (host, val) in _forwarder.DrainCounters())
                    counters[host] = val;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Couldn't drain forwarder counters"); }
        try
        {
            if (_cdp is not null)
            {
                foreach (var (host, val) in _cdp.DrainCounters())
                {
                    // MAX merge — see comment above. Mirrors the
                    // legacy ghost_shell_browser/runtime.py rule.
                    if (counters.TryGetValue(host, out var existing))
                    {
                        counters[host] = (
                            Math.Max(existing.Bytes,    val.Bytes),
                            Math.Max(existing.Requests, val.Requests));
                    }
                    else counters[host] = val;
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Couldn't drain CDP counters"); }
        if (counters.Count == 0) return;

        // The legacy web stamps each delta with the LOCAL time so the
        // bucket label reads "21" for "9 PM" the same hour the user
        // saw it. Captured once per flush so a long-running drain
        // doesn't smear samples across two buckets.
        var now = DateTime.Now;
        var deltas = new List<TrafficDelta>(counters.Count);
        foreach (var (host, (bytes, reqs)) in counters)
        {
            if (string.IsNullOrWhiteSpace(host)) continue;
            deltas.Add(new TrafficDelta
            {
                ProfileName = _profileName,
                Domain      = host,
                Timestamp   = now,
                Bytes       = bytes,
                ReqCount    = reqs,
                RunId       = _runId,
            });
        }
        try
        {
            await _traffic.WriteSamplesAsync(deltas, ct);
            _log.LogDebug(
                "TrafficCollector flushed {Hosts} host(s) for profile='{Name}'",
                deltas.Count, _profileName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TrafficCollector write failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _stopCts?.Cancel(); } catch { /* ignore */ }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* ignore */ }
        }
        // One final flush so the closing seconds aren't lost.
        try { await FlushAsync(); }
        catch { /* ignore */ }
        _stopCts?.Dispose();
        _stopCts = null;
        _loop = null;
    }
}
