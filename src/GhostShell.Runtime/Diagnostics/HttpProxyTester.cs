// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Diagnostics;

/// <summary>
/// Phase 61 — real proxy probe. Replaces the deterministic-fake
/// <see cref="StubProxyTester"/> which always reported "ok" regardless
/// of whether the proxy actually worked. The stub gave us 74/74 OK
/// even though every browser launch died with
/// <c>ERR_PROXY_CONNECTION_FAILED</c>; that gap (test passes, real
/// usage fails) is what this class closes.
///
/// Strategy:
///   1. Parse the user's URL to get host:port + creds + URL-declared
///      scheme (the user may type http:// for an actual SOCKS5 endpoint).
///   2. TCP-level reachability: open a Socket to host:port with a 4s
///      timeout. If even this fails, the proxy is unreachable — short
///      circuit out without trying every protocol.
///   3. Try schemes in priority order (URL hint first, then the others)
///      against <c>http://ip-api.com/json</c> via a per-scheme HttpClient.
///      First scheme that returns a parseable JSON response wins.
///   4. Surface per-attempt diagnostics so the user can see the chain
///      ("tried http: refused; tried socks5: ok in 312ms"). On success,
///      <see cref="ProxyTestResult.DetectedScheme"/> tells the UI which
///      protocol actually worked — if it differs from the URL, the UI
///      can offer to auto-correct.
///
/// .NET 6+ supports SOCKS schemes natively in <see cref="WebProxy"/>:
/// passing socks5://host:port to WebProxy + assigning to
/// <see cref="HttpClientHandler.Proxy"/> just works for HTTP traffic.
/// </summary>
public sealed class HttpProxyTester : IProxyTester
{
    private readonly ILogger<HttpProxyTester> _log;

    /// <summary>Endpoint hit through each candidate proxy. Free, no auth,
    /// fast, returns IP + country/city/ISP — the exact shape we want
    /// for the proxy table. Plain HTTP (not HTTPS) so a SOCKS4 proxy
    /// — which can't handle CONNECT for TLS — still passes the probe.</summary>
    private const string ProbeEndpoint =
        "http://ip-api.com/json/?fields=status,country,countryCode,city,isp,as,query,proxy,hosting,mobile";

    /// <summary>Per-scheme probe timeout. Free proxies are slow; 8s is a
    /// reasonable upper bound — anything past that and the proxy is
    /// unusable for real browsing anyway.</summary>
    private static readonly TimeSpan PerSchemeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>TCP reachability timeout — if we can't even open the
    /// socket in 4s, the proxy is dead and we save 3 × per-scheme
    /// timeout by bailing early.</summary>
    private static readonly TimeSpan TcpProbeTimeout = TimeSpan.FromSeconds(4);

    public HttpProxyTester(ILogger<HttpProxyTester> log) => _log = log;

    public async Task<ProxyTestResult> TestAsync(Proxy proxy, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1) Parse URL — we need host/port for the TCP check + creds for
        //    auth-bearing proxies + scheme hint for the priority order.
        if (!TryParseProxyUrl(proxy.Url, out var host, out var port, out var declaredScheme,
                              out var user, out var pass, out var parseError))
        {
            _log.LogWarning("Proxy '{Slug}' URL malformed: {Err}", proxy.Slug, parseError);
            return new ProxyTestResult
            {
                Ok = false,
                Error = "URL malformed: " + parseError,
            };
        }

        // 2) TCP reachability — fail-fast on dead/blocked proxies before
        //    we waste 3×8s on per-scheme timeouts.
        var tcpAttempt = await ProbeTcpAsync(host, port, ct);
        if (!tcpAttempt.Ok)
        {
            _log.LogInformation(
                "Proxy '{Slug}' TCP unreachable: {Err}", proxy.Slug, tcpAttempt.Error);
            return new ProxyTestResult
            {
                Ok = false,
                Error = "TCP: " + (tcpAttempt.Error ?? "unreachable"),
                Attempts = new[] { new ProxyProbeAttempt("tcp", false, tcpAttempt.LatencyMs, tcpAttempt.Error) },
            };
        }

        // 3) Try each scheme in priority order. The URL-declared scheme
        //    goes first (most likely correct), then the others as
        //    fallbacks. If declared scheme is unknown/empty, default to
        //    http first since most free-proxy lists are HTTP CONNECT.
        var schemes = OrderSchemes(declaredScheme);
        // Internal list keeps SchemeAttempt (carries optional Body for
        // success cases); we convert to public ProxyProbeAttempt at exit.
        var attempts = new List<SchemeAttempt>(capacity: schemes.Count + 1)
        {
            new("tcp", true, tcpAttempt.LatencyMs, null),
        };

        foreach (var scheme in schemes)
        {
            ct.ThrowIfCancellationRequested();
            var attempt = await ProbeViaSchemeAsync(scheme, host, port, user, pass, ct);
            attempts.Add(attempt);
            if (attempt.Ok && attempt.Body is { } body)
            {
                // Phase 61c — second probe: actual Google reachability.
                // ip-api.com is permissive, but free proxies routinely
                // pass the ip-api probe and then ERR_TIMED_OUT against
                // google.com — which is what the script actually drives.
                // Verify google.com responds via this scheme; if not,
                // mark the result as a "weak" pass so the UI can warn
                // the user before they assign the proxy to a profile.
                var googleAttempt = await ProbeGoogleAsync(scheme, host, port, user, pass, ct);
                attempts.Add(googleAttempt);
                if (!googleAttempt.Ok)
                {
                    _log.LogWarning(
                        "Proxy '{Slug}' passes ip-api via {Scheme} but FAILS Google reachability: {Err}",
                        proxy.Slug, scheme, googleAttempt.Error);
                    return new ProxyTestResult
                    {
                        // Mark as failed — the proxy can't carry our actual
                        // workload. Better to surface this loudly than to let
                        // the user assign it to a profile and watch the
                        // browser launch fail with ERR_TIMED_OUT.
                        Ok            = false,
                        Error         = $"Google unreachable via {scheme}: {Trim(googleAttempt.Error, 80)}",
                        Ip            = body.Ip,
                        Country       = body.Country,
                        CountryCode   = body.CountryCode,
                        City          = body.City,
                        Asn           = body.Asn,
                        Isp           = body.Isp,
                        IpType        = body.IpType,
                        LatencyMs     = attempt.LatencyMs,
                        DetectedScheme = scheme,
                        Attempts      = ToPublic(attempts),
                        At            = DateTime.UtcNow,
                    };
                }
                _log.LogInformation(
                    "Proxy '{Slug}' OK via {Scheme} → {Ip} ({Country}, {City}, {Isp}) ip-api={Ms1}ms google={Ms2}ms",
                    proxy.Slug, scheme, body.Ip, body.Country, body.City, body.Isp,
                    attempt.LatencyMs, googleAttempt.LatencyMs);
                return new ProxyTestResult
                {
                    Ok            = true,
                    Ip            = body.Ip,
                    Country       = body.Country,
                    CountryCode   = body.CountryCode,
                    City          = body.City,
                    Asn           = body.Asn,
                    Isp           = body.Isp,
                    IpType        = body.IpType,
                    // Use the slower of the two latencies — that's what
                    // the user will actually feel in the browser. Fast
                    // ip-api but slow Google = effectively slow proxy.
                    LatencyMs     = Math.Max(attempt.LatencyMs ?? 0, googleAttempt.LatencyMs ?? 0),
                    DetectedScheme = scheme,
                    Attempts      = ToPublic(attempts),
                    At            = DateTime.UtcNow,
                };
            }
        }

        // All schemes failed. Surface the best (least-rude) error so the
        // user sees actionable text instead of "ERR_PROXY_CONNECTION_FAILED".
        var summary = string.Join("; ",
            attempts.Where(a => a.Scheme != "tcp" && !a.Ok)
                    .Select(a => $"{a.Scheme}: {Trim(a.Error, 60)}"));
        _log.LogWarning(
            "Proxy '{Slug}' FAILED all schemes ({Total}ms): {Summary}",
            proxy.Slug, sw.ElapsedMilliseconds, summary);
        return new ProxyTestResult
        {
            Ok       = false,
            Error    = string.IsNullOrEmpty(summary) ? "no scheme worked" : summary,
            Attempts = ToPublic(attempts),
        };
    }

    private static IReadOnlyList<ProxyProbeAttempt> ToPublic(List<SchemeAttempt> a)
        => a.Select(x => new ProxyProbeAttempt(x.Scheme, x.Ok, x.LatencyMs, x.Error))
            .ToList();

    // ── Internals ─────────────────────────────────────────────────

    /// <summary>Internal probe attempt with optional parsed body for
    /// success cases. Doesn't escape the class.</summary>
    private sealed record SchemeAttempt(
        string Scheme,
        bool   Ok,
        int?   LatencyMs,
        string? Error,
        IpApiBody? Body = null);

    private sealed record IpApiBody(
        IpType IpType, string? CountryCode, string? Country,
        string? City, string? Isp, string? Asn, string? Ip);

    private async Task<SchemeAttempt> ProbeTcpAsync(
        string host, int port, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var sock = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TcpProbeTimeout);
            await sock.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return new SchemeAttempt("tcp", true, (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SchemeAttempt("tcp", false, null, "cancelled");
        }
        catch (OperationCanceledException)
        {
            return new SchemeAttempt("tcp", false, (int)sw.ElapsedMilliseconds,
                $"connect timeout after {TcpProbeTimeout.TotalSeconds:0}s");
        }
        catch (SocketException sex)
        {
            return new SchemeAttempt("tcp", false, (int)sw.ElapsedMilliseconds,
                $"{sex.SocketErrorCode}: {sex.Message}");
        }
        catch (Exception ex)
        {
            return new SchemeAttempt("tcp", false, (int)sw.ElapsedMilliseconds,
                ex.Message);
        }
    }

    private async Task<SchemeAttempt> ProbeViaSchemeAsync(
        string scheme, string host, int port, string? user, string? pass,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Build proxy URI for this scheme. .NET 6+ accepts
            // socks4/socks4a/socks5 directly on WebProxy.
            var proxyUri = new Uri($"{scheme}://{host}:{port}");
            ICredentials? creds = null;
            if (!string.IsNullOrEmpty(user))
                creds = new NetworkCredential(user, pass ?? "");

            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxyUri) { Credentials = creds },
                UseProxy = true,
                // Free proxies often re-write headers / inject banner
                // pages. Allow auto-redirect so a 302 doesn't kill us.
                AllowAutoRedirect = true,
            };
            using var client = new HttpClient(handler) { Timeout = PerSchemeTimeout };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PerSchemeTimeout);

            using var resp = await client.GetAsync(ProbeEndpoint, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return new SchemeAttempt(scheme, false, (int)sw.ElapsedMilliseconds,
                    $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            var body = ParseIpApi(json);
            sw.Stop();
            if (body is null)
            {
                return new SchemeAttempt(scheme, false, (int)sw.ElapsedMilliseconds,
                    "non-JSON response (proxy injected banner?)");
            }
            return new SchemeAttempt(scheme, true, (int)sw.ElapsedMilliseconds, null, body);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            return new SchemeAttempt(scheme, false, null, "cancelled");
        }
        catch (TaskCanceledException)
        {
            return new SchemeAttempt(scheme, false, (int)sw.ElapsedMilliseconds,
                $"timeout after {PerSchemeTimeout.TotalSeconds:0}s");
        }
        catch (HttpRequestException hrex) when (hrex.InnerException is SocketException sex)
        {
            return new SchemeAttempt(scheme, false, (int)sw.ElapsedMilliseconds,
                $"{sex.SocketErrorCode}: {sex.Message}");
        }
        catch (HttpRequestException hrex)
        {
            return new SchemeAttempt(scheme, false, (int)sw.ElapsedMilliseconds,
                hrex.Message);
        }
        catch (Exception ex)
        {
            return new SchemeAttempt(scheme, false, (int)sw.ElapsedMilliseconds,
                ex.Message);
        }
    }

    /// <summary>
    /// Phase 61c — second-stage probe that validates Google reachability
    /// over the same scheme that just passed ip-api. Free proxies
    /// frequently route ip-api OK and then ERR_TIMED_OUT against Google
    /// (rate-limited, blocked by Google's bot-net detection, slow on
    /// global routes). A HEAD to https://www.google.com/generate_204 is
    /// cheap (~1KB), returns 204 No Content, and is what Chromium itself
    /// uses for connectivity checks — so passing this means the browser's
    /// own probes will succeed.
    /// </summary>
    private async Task<SchemeAttempt> ProbeGoogleAsync(
        string scheme, string host, int port, string? user, string? pass,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var proxyUri = new Uri($"{scheme}://{host}:{port}");
            ICredentials? creds = null;
            if (!string.IsNullOrEmpty(user))
                creds = new NetworkCredential(user, pass ?? "");
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxyUri) { Credentials = creds },
                UseProxy = true,
                AllowAutoRedirect = false, // generate_204 should be a single hop
            };
            using var client = new HttpClient(handler) { Timeout = PerSchemeTimeout };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PerSchemeTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://www.google.com/generate_204");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            // 204 = pass; 200/3xx = also pass (some proxies inject content
            // but the connection works). Anything 4xx/5xx = the proxy
            // itself is rejecting us (captive portal, anti-bot block).
            var code = (int)resp.StatusCode;
            if (code >= 400)
            {
                return new SchemeAttempt("google", false, (int)sw.ElapsedMilliseconds,
                    $"HTTP {code} {resp.ReasonPhrase}");
            }
            return new SchemeAttempt("google", true, (int)sw.ElapsedMilliseconds, null);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            return new SchemeAttempt("google", false, null, "cancelled");
        }
        catch (TaskCanceledException)
        {
            return new SchemeAttempt("google", false, (int)sw.ElapsedMilliseconds,
                $"timeout after {PerSchemeTimeout.TotalSeconds:0}s");
        }
        catch (HttpRequestException hrex) when (hrex.InnerException is SocketException sex)
        {
            return new SchemeAttempt("google", false, (int)sw.ElapsedMilliseconds,
                $"{sex.SocketErrorCode}: {sex.Message}");
        }
        catch (HttpRequestException hrex)
        {
            return new SchemeAttempt("google", false, (int)sw.ElapsedMilliseconds,
                hrex.Message);
        }
        catch (Exception ex)
        {
            return new SchemeAttempt("google", false, (int)sw.ElapsedMilliseconds,
                ex.Message);
        }
    }

    /// <summary>Parse ip-api.com's JSON response into our domain shape.
    /// Returns null on JSON parse failure or status != "success".</summary>
    private static IpApiBody? ParseIpApi(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                return null;

            string? Get(string key) =>
                root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
            bool? GetBool(string key) =>
                root.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean() : null;

            var ipType = (GetBool("mobile"), GetBool("hosting"), GetBool("proxy")) switch
            {
                (true, _, _)            => IpType.Mobile,
                (_, true, _)            => IpType.Datacenter,
                // ip-api flags VPN / known-bad-ASN as proxy=true; treat
                // as Datacenter for our colouring (it's a "burnt" IP).
                (_, _, true)            => IpType.Datacenter,
                _                       => IpType.Residential,
            };

            return new IpApiBody(
                IpType: ipType,
                CountryCode: Get("countryCode"),
                Country:     Get("country"),
                City:        Get("city"),
                Isp:         Get("isp"),
                Asn:         Get("as"),
                Ip:          Get("query"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pick the scheme priority order. The user's URL hint goes
    /// first; the rest fall through. Unknown / blank scheme defaults to
    /// http first (most free-proxy lists), then SOCKS5, then SOCKS4.</summary>
    private static IReadOnlyList<string> OrderSchemes(string declared)
    {
        var d = (declared ?? "").Trim().ToLowerInvariant();
        return d switch
        {
            "socks5" or "socks5h" => new[] { "socks5", "socks4", "http" },
            "socks4" or "socks4a" => new[] { "socks4", "socks5", "http" },
            "https"               => new[] { "http",   "socks5", "socks4" }, // HTTPS proxies use HTTP CONNECT
            _                     => new[] { "http",   "socks5", "socks4" },
        };
    }

    /// <summary>Robust parse — accepts schemeful URLs, schemeless host:port
    /// (defaults to http), and user:pass@host:port. Does NOT throw on
    /// well-known free-proxy quirks (extra whitespace, trailing slash).</summary>
    private static bool TryParseProxyUrl(
        string url, out string host, out int port, out string scheme,
        out string? user, out string? pass, out string? error)
    {
        host = ""; port = 0; scheme = ""; user = null; pass = null; error = null;
        if (string.IsNullOrWhiteSpace(url)) { error = "empty"; return false; }
        url = url.Trim();

        // Accept "1.2.3.4:8080" as schemeless http
        if (!url.Contains("://")) url = "http://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            error = "could not parse URI"; return false;
        }
        host = u.Host;
        port = u.Port > 0 ? u.Port : 80;
        scheme = u.Scheme.ToLowerInvariant();
        if (!string.IsNullOrEmpty(u.UserInfo))
        {
            var parts = u.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            if (parts.Length == 2) pass = Uri.UnescapeDataString(parts[1]);
        }
        if (string.IsNullOrEmpty(host)) { error = "missing host"; return false; }
        return true;
    }

    private static string Trim(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
