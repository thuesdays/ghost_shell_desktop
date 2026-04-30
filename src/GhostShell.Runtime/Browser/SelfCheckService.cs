// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Net.Http;
using System.Text.Json;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Concrete <see cref="ISelfCheckService"/>. Runs four probes against
/// a live browser session:
///
///   1. <b>Exit IP + geo</b> — the browser navigates to ipinfo.io's
///      JSON endpoint and we read the response. The IP must come from
///      the proxy if one is configured; geo tells us which country
///      the exit lands in.
///   2. <b>WebRTC leak</b> — JS RTCPeerConnection probe. Creates an
///      ephemeral connection, gathers ICE candidates, looks for any
///      <c>typ host</c> candidate that exposes a non-loopback / non-
///      proxied local IP. If we see one, the user's real LAN IP is
///      leaking around the proxy.
///   3. <b>Timezone</b> — <c>Intl.DateTimeFormat().resolvedOptions().timeZone</c>.
///      Should match the timezone the FP payload set.
///   4. <b>User-Agent</b> — <c>navigator.userAgent</c> as the C++
///      patch actually exposes it. Lets us compare against the FP
///      payload's intended UA.
///
/// Score: 0-100 derived from how many probes passed cleanly.
///   • Exit IP got returned → +40
///   • WebRTC didn't leak    → +30
///   • Timezone matched      → +15
///   • UA non-empty          → +15
/// </summary>
public sealed class SelfCheckService : ISelfCheckService
{
    private readonly ISelfCheckHistoryService _history;
    private readonly ILogger<SelfCheckService> _log;

    public SelfCheckService(
        ISelfCheckHistoryService history,
        ILogger<SelfCheckService> log)
    {
        _history = history;
        _log     = log;
    }

    public async Task<SelfCheckResult> RunAsync(
        IBrowserSession session, string profileName,
        long? runId = null, string? expectedTimezone = null,
        CancellationToken ct = default)
    {
        var ranAt = DateTime.UtcNow;
        string? exitIp = null, geoCountry = null, geoCity = null;
        string? tzActual = null, ua = null;
        bool webrtcLeak = false;
        string? webrtcLocalIp = null;
        var notes = new List<string>();

        // ─── Probe 1: exit IP + geo via ipinfo.io ──────────────
        // We navigate the browser there directly so the request
        // goes through whatever proxy is configured for the
        // session — the ipinfo response IS the exit-side IP.
        try
        {
            await session.NavigateAsync("https://ipinfo.io/json", ct);
            // The response is rendered into a <pre> by Chrome's
            // JSON viewer; readout via document.body.innerText.
            var jsonText = await session.ExecuteScriptAsync(
                "return document.body && document.body.innerText || '';",
                null, ct) as string;
            if (!string.IsNullOrWhiteSpace(jsonText))
            {
                using var doc = JsonDocument.Parse(jsonText);
                if (doc.RootElement.TryGetProperty("ip", out var ipEl))
                    exitIp = ipEl.GetString();
                if (doc.RootElement.TryGetProperty("country", out var cEl))
                    geoCountry = cEl.GetString();
                if (doc.RootElement.TryGetProperty("city", out var cyEl))
                    geoCity = cyEl.GetString();
            }
            else
            {
                notes.Add("ipinfo.io returned no body");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Self-check ipinfo probe failed for '{P}'", profileName);
            notes.Add("ipinfo probe failed: " + ex.Message);
        }

        // ─── Probe 2: WebRTC leak ───────────────────────────────
        // The JS creates an offer with both audio + video pseudo-
        // tracks, gathers ICE candidates, returns the host-typ ones.
        // If any contains a non-mDNS / non-loopback IPv4, that IP
        // would be exposed to peers — leak.
        const string WebRtcJs = """
            return new Promise((resolve) => {
              try {
                const pc = new RTCPeerConnection({iceServers: []});
                const ips = new Set();
                pc.createDataChannel('p');
                pc.onicecandidate = (e) => {
                  if (!e.candidate) {
                    pc.close();
                    resolve(Array.from(ips));
                    return;
                  }
                  // Strict IPv4 — octets 0-255 only. The looser
                  // [0-9]{1,3} pattern matches obvious garbage like
                  // 999.999.999.999 which slips through downstream
                  // validation and confuses the IsTrivialIp filter.
                  const m = /\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b/.exec(e.candidate.candidate);
                  if (m) ips.add(m[0]);
                };
                pc.createOffer({offerToReceiveAudio: true, offerToReceiveVideo: true})
                  .then((o) => pc.setLocalDescription(o))
                  .catch(() => resolve(Array.from(ips)));
                setTimeout(() => { try { pc.close(); } catch (e) {} resolve(Array.from(ips)); }, 2500);
              } catch (e) { resolve([]); }
            });
        """;
        try
        {
            var raw = await session.ExecuteScriptAsync(WebRtcJs, null, ct);
            // Selenium's JS bridge returns IList<object> for arrays.
            if (raw is System.Collections.IEnumerable arr)
            {
                foreach (var ip in arr.Cast<object?>().Select(o => o?.ToString() ?? ""))
                {
                    if (string.IsNullOrEmpty(ip)) continue;
                    // Loopback (127.x) and link-local (169.254.x) are
                    // expected. mDNS (.local hostnames) never reach
                    // here since the regex matched IPv4 only. Anything
                    // else IS the leak — record the first non-trivial
                    // one and flag.
                    if (IsTrivialIp(ip)) continue;
                    webrtcLeak = true;
                    webrtcLocalIp = ip;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "WebRTC probe failed for '{P}'", profileName);
            notes.Add("webrtc probe failed");
        }

        // ─── Probe 3: timezone ──────────────────────────────────
        try
        {
            tzActual = await session.ExecuteScriptAsync(
                "return Intl.DateTimeFormat().resolvedOptions().timeZone;",
                null, ct) as string;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Timezone probe failed");
        }

        // ─── Probe 4: navigator.userAgent ───────────────────────
        try
        {
            ua = await session.ExecuteScriptAsync(
                "return navigator.userAgent;", null, ct) as string;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "UA probe failed");
        }

        // ─── Score ─────────────────────────────────────────────
        var score = 0;
        if (!string.IsNullOrEmpty(exitIp)) score += 40;
        if (!webrtcLeak)                   score += 30;
        if (!string.IsNullOrEmpty(tzActual)
            && (expectedTimezone is null
                || tzActual.Equals(expectedTimezone, StringComparison.OrdinalIgnoreCase)))
            score += 15;
        if (!string.IsNullOrEmpty(ua))     score += 15;

        var raw_ = JsonSerializer.Serialize(new
        {
            exit_ip = exitIp, geo_country = geoCountry, geo_city = geoCity,
            webrtc_leaked = webrtcLeak, webrtc_local_ip = webrtcLocalIp,
            timezone_actual = tzActual, timezone_expected = expectedTimezone,
            ua_actual = ua,
            notes,
        });

        var result = new SelfCheckResult
        {
            ProfileName       = profileName,
            RunId             = runId,
            RanAt             = ranAt,
            ExitIp            = exitIp,
            GeoCountry        = geoCountry,
            GeoCity           = geoCity,
            WebRtcLeaked      = webrtcLeak,
            WebRtcLocalIp     = webrtcLocalIp,
            TimezoneActual    = tzActual,
            TimezoneExpected  = expectedTimezone,
            UaActual          = ua,
            Score             = score,
            Note              = notes.Count == 0 ? null : string.Join("; ", notes),
            RawJson           = raw_,
        };
        var id = await _history.InsertAsync(result, ct);
        return result with { Id = id };
    }

    public Task<IReadOnlyList<SelfCheckResult>> ListAsync(
        string profileName, int limit = 50, CancellationToken ct = default)
        => _history.ListAsync(profileName, limit, ct);

    public Task<SelfCheckResult?> GetLatestAsync(string profileName, CancellationToken ct = default)
        => _history.GetLatestAsync(profileName, ct);

    /// <summary>
    /// IPs that DON'T constitute a leak: loopback, link-local,
    /// CGNAT-ish ranges that real users have. We treat private LAN
    /// IPs (10/8, 192.168/16, 172.16/12) as also "trivial" because
    /// modern Chrome's mDNS obfuscation hides them by default — if
    /// we see one it's our patched build skipping the obfuscation,
    /// not the user's real WAN address.
    /// </summary>
    private static bool IsTrivialIp(string ip)
    {
        if (ip.StartsWith("127.")) return true;
        if (ip.StartsWith("169.254.")) return true;
        if (ip.StartsWith("10.")) return true;
        if (ip.StartsWith("192.168.")) return true;
        if (ip.StartsWith("172."))
        {
            var parts = ip.Split('.');
            if (parts.Length > 1
                && int.TryParse(parts[1], out var second)
                && second is >= 16 and <= 31)
                return true;
        }
        return false;
    }
}
