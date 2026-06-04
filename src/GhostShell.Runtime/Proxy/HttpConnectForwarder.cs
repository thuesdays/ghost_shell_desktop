// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

// Note: the namespace is `ProxyAuth`, not `Proxy`, on purpose —
// `GhostShell.Core.Models.Proxy` is the domain type, and a sibling
// `GhostShell.Runtime.Proxy` namespace would shadow it inside any
// runtime file that says `using GhostShell.Runtime.*;`. The folder
// stays named `Proxy/` for ergonomics; the namespace is what the
// compiler resolves.
namespace GhostShell.Runtime.ProxyAuth;

/// <summary>
/// Local HTTP-proxy that authenticates upstream on Chromium's
/// behalf. Mirrors the design of the legacy Python ProxyForwarder
/// (ghost_shell/proxy/forwarder.py) — the patched browser sees a
/// plain unauthenticated proxy on 127.0.0.1; we open a TCP
/// connection to the real upstream proxy and inject
/// <c>Proxy-Authorization: Basic …</c> into the first
/// request line we read off the wire (CONNECT for HTTPS, plain
/// HTTP method for the rest), then transparently shuttle bytes
/// in both directions until either side closes.
///
/// Why we can't just put creds in --proxy-server:
///   Chromium 80+ stripped support for <c>http://user:pass@host:port</c>
///   in the command-line flag. The browser exits during startup
///   before chromedriver's DevTools port appears, surfacing as
///   "DevToolsActivePort file doesn't exist" with no further
///   detail. The legacy stack hit this in 2023 and the same
///   forwarder pattern is the canonical workaround.
///
/// Why one forwarder per session:
///   Bytes for an HTTPS tunnel are encrypted, so a single shared
///   forwarder can't multiplex per-profile traffic billing.
///   Per-session also means disposing a profile cleanly tears
///   down its open sockets without disturbing siblings.
/// </summary>
public sealed class HttpConnectForwarder : IProxyAuthForwarder
{
    private readonly ILogger<HttpConnectForwarder> _log;

    private TcpListener? _listener;
    private CancellationTokenSource? _stopCts;
    private Task? _acceptLoop;

    private string _upstreamHost = "";
    private int    _upstreamPort;
    private byte[] _authHeader   = Array.Empty<byte>();
    private string? _localUrl;

    public bool IsRunning => _listener is not null && _stopCts is { IsCancellationRequested: false };
    public string? LocalUrl => _localUrl;

    // Phase 28 — per-host byte / request counters. ConcurrentDictionary
    // gives us thread-safe lookups across the per-connection handlers;
    // each connection mutates ITS OWN HostCounter via Interlocked.Add
    // so the only contention is on the dictionary itself (cheap, since
    // most accesses are reads after the first request to a new host).
    private sealed class HostCounter
    {
        public long Bytes;
        public long Requests;
    }
    private readonly ConcurrentDictionary<string, HostCounter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private HostCounter GetCounter(string host) =>
        _counters.GetOrAdd(host, _ => new HostCounter());

    // ─── Aggregate health counters (net-trace diagnostics) ───────────
    // Total connections accepted, currently-open connections, and the
    // count of connections that FAILED to reach the upstream proxy.
    // A rising _upstreamFailures with a flat _connActive is the exact
    // signature of a dead/unreachable upstream proxy (the ERR_EMPTY_-
    // RESPONSE case). Surfaced in logs so the failure is never silent.
    private long _connTotal;
    private long _connActive;
    private long _upstreamFailures;

    /// <summary>Live snapshot of forwarder health for diagnostics.</summary>
    public (long Total, long Active, long UpstreamFailures) Health =>
        (Interlocked.Read(ref _connTotal),
         Interlocked.Read(ref _connActive),
         Interlocked.Read(ref _upstreamFailures));

    public HttpConnectForwarder(ILogger<HttpConnectForwarder> log)
    {
        _log = log;
    }

    public Task<string> StartAsync(string upstreamProxyUrl, CancellationToken ct = default)
    {
        if (_listener is not null)
            throw new InvalidOperationException("Forwarder is already started.");

        // Normalise — accept "host:port", "user:pass@host:port",
        // "http://host:port", or scheme-prefixed with creds.
        var normalised = upstreamProxyUrl;
        if (!normalised.Contains("://", StringComparison.Ordinal))
            normalised = "http://" + normalised;

        if (!Uri.TryCreate(normalised, UriKind.Absolute, out var uri))
            throw new ArgumentException(
                $"Cannot parse upstream proxy URL '{upstreamProxyUrl}'.",
                nameof(upstreamProxyUrl));

        // audit PROXY-03: Uri.Host returns IPv6 literals in bracketed
        // form (e.g. "[::1]"), but Socket.ConnectAsync/IPAddress.Parse
        // expect a bare host or IP. Strip the brackets so IPv6 upstream
        // proxies connect instead of throwing on the bracketed string.
        _upstreamHost = uri.Host.Trim('[', ']');
        _upstreamPort = uri.Port > 0 ? uri.Port : 8080;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            // UserInfo is "user:pass" — already URL-decoded by Uri.
            // Encoding to UTF-8 here matches what every HTTP client
            // does for Basic auth; the legacy Python forwarder uses
            // ASCII but UTF-8 is the spec (RFC 7617) and is a strict
            // superset.
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(uri.UserInfo));
            _authHeader = Encoding.ASCII.GetBytes($"Proxy-Authorization: Basic {token}");
        }

        // Bind to 127.0.0.1:0 — kernel picks a free port. Loopback
        // only is critical: a local proxy reachable from the LAN
        // would be a serious operational footgun (anyone on the
        // network using your egress IP via your auth creds).
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 128);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _localUrl = $"http://127.0.0.1:{port}";

        _stopCts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopCts.Token));

        _log.LogInformation(
            "Auth-proxy forwarder ready: 127.0.0.1:{Local} → {Up}:{UpPort} (auth={HasAuth}, net-trace={Trace})",
            port, _upstreamHost, _upstreamPort, _authHeader.Length > 0,
            GhostShell.Core.Common.Diagnostics.NetworkTrace);

        return Task.FromResult(_localUrl);
    }

    // ─────────────────────────────────────────────────────────
    // Accept loop
    // ─────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        // Capture the listener under the local stack — DisposeAsync
        // can null the field after Stop() fires, and the cancellation
        // token usually unblocks AcceptTcpClientAsync first, but on
        // a tight stop/start cycle we don't want the next loop
        // iteration to deref the field after it's been nulled.
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException)    { return; }
            catch (SocketException ex) when (ct.IsCancellationRequested)
            {
                _log.LogDebug(ex, "Accept aborted during shutdown");
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Accept loop swallowed an exception, continuing");
                continue;
            }

            // Fire-and-forget per connection. Each handler manages
            // its own lifetime; failures are logged and don't bring
            // the listener down.
            _ = Task.Run(() => HandleConnectionAsync(client, ct));
        }
    }

    // ─────────────────────────────────────────────────────────
    // Per-connection handler
    // ─────────────────────────────────────────────────────────

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        TcpClient? upstream = null;
        var targetHost = "";
        var connId = Interlocked.Increment(ref _connTotal);
        Interlocked.Increment(ref _connActive);
        try
        {
            client.NoDelay = true;
            using var clientStream = client.GetStream();

            // audit PROXY-01: buffer the client socket so bytes we read
            // past a header terminator (the start of a body, or the next
            // pipelined request) are preserved rather than lost. The
            // legacy single-read path silently dropped any such over-read.
            var clientReader = new BufferedSocketReader(clientStream);

            // Read request headers up to the first blank line. Cap at
            // 64 KiB — anything bigger than that on a CONNECT/proxy
            // request is malformed.
            var headers = await clientReader.ReadHeadersAsync(64 * 1024, ct);
            if (headers.Length == 0)
            {
                _log.LogDebug("[net #{Id}] empty request — client disconnected before headers", connId);
                return;
            }

            targetHost = ExtractTargetHost(headers);
            var isConnect  = IsConnectRequest(headers);

            _log.LogTrace(
                "[net #{Id}] {Method} target={Target} ({HdrBytes}B headers) → dialing upstream {Up}:{UpPort}",
                connId, isConnect ? "CONNECT" : "HTTP", string.IsNullOrEmpty(targetHost) ? "?" : targetHost,
                headers.Length, _upstreamHost, _upstreamPort);

            // Connect to the real proxy. This is THE failure point when an
            // upstream proxy is dead/rate-limited — the symptom the browser
            // shows as net::ERR_EMPTY_RESPONSE. Give it its own try/catch so
            // the cause is logged LOUDLY (Warning) with the target host and
            // the concrete socket error, instead of being swallowed below as
            // a generic "peer reset" at Debug.
            upstream = new TcpClient { NoDelay = true };
            var dialSw = Stopwatch.StartNew();
            try
            {
                using var upstreamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                upstreamCts.CancelAfter(TimeSpan.FromSeconds(30));
                await upstream.ConnectAsync(_upstreamHost, _upstreamPort, upstreamCts.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                var fails = Interlocked.Increment(ref _upstreamFailures);
                var reason = ex is OperationCanceledException
                    ? "timeout after 30s"
                    : $"{((SocketException)ex).SocketErrorCode} ({ex.Message})";
                _log.LogWarning(
                    "[net #{Id}] UPSTREAM PROXY UNREACHABLE {Up}:{UpPort} for target '{Target}': {Reason}. " +
                    "Browser will see ERR_EMPTY_RESPONSE. (upstream failures this session: {Fails})",
                    connId, _upstreamHost, _upstreamPort,
                    string.IsNullOrEmpty(targetHost) ? "?" : targetHost, reason, fails);
                return;
            }
            using var upstreamStream = upstream.GetStream();

            _log.LogDebug(
                "[net #{Id}] upstream connected in {Ms}ms (target={Target})",
                connId, dialSw.ElapsedMilliseconds, string.IsNullOrEmpty(targetHost) ? "?" : targetHost);

            HostCounter? counter =
                string.IsNullOrEmpty(targetHost) ? null : GetCounter(targetHost);

            if (isConnect)
            {
                // ── CONNECT (HTTPS tunnel) ───────────────────────────
                // Exactly one request needs auth; after the upstream's
                // 200 response everything is opaque TLS. Inject once,
                // then raw-pump in both directions (the original design).
                var modified = InjectAuth(headers);
                await upstreamStream.WriteAsync(modified, ct);
                await upstreamStream.FlushAsync(ct);

                _log.LogTrace(
                    "Forwarding CONNECT {Bytes}B header block (target={Target})",
                    modified.Length, string.IsNullOrEmpty(targetHost) ? "?" : targetHost);

                if (counter is not null)
                {
                    Interlocked.Increment(ref counter.Requests);
                    Interlocked.Add(ref counter.Bytes, modified.Length);
                }
            }
            else
            {
                // ── Plain HTTP (keep-alive / pipelined) ──────────────
                // audit PROXY-01: a single HTTP keep-alive connection
                // carries many requests (GET http://a/, GET http://b/…).
                // Every one needs Proxy-Authorization, not just the
                // first — otherwise the upstream answers 407 for all but
                // the first resource. We forward the already-read first
                // request here (auth-injected + body) and then loop in
                // PumpHttpRequestsAsync for the rest until the client
                // half-closes or the connection upgrades.
                await ForwardHttpRequestAsync(
                    clientReader, upstreamStream, headers, counter, ct);
            }

            // Bidirectional pump. Either side hitting EOF / error
            // tears the whole thing down. The counting copy mirrors
            // Stream.CopyToAsync but Interlocked-adds each chunk's
            // length to the host counter. counter==null = direct
            // proxy without an upstream host header (rare); the copy
            // still works, we just don't bill it.
            //
            // audit PROXY-01: for plain HTTP the client→upstream
            // direction goes through PumpHttpRequestsAsync (re-injects
            // auth + counts each request); the upstream→client direction
            // is always a transparent byte copy.
            var c2u = isConnect
                ? CountingCopyAsync(clientReader.AsStream(), upstreamStream, counter, ct)
                : PumpHttpRequestsAsync(clientReader, upstreamStream, counter, ct);
            var u2c = CountingCopyAsync(upstreamStream, clientStream, counter, ct);
            await Task.WhenAny(c2u, u2c);

            // Politely close both sides; the surviving copy will
            // observe the close and exit. Wrap in try-catch — the
            // streams might already be in a torn-down state.
            try { client.Close();   } catch { /* swallow */ }
            try { upstream.Close(); } catch { /* swallow */ }
            try { await Task.WhenAll(c2u, u2c).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* expected — the loser threw on the closed stream */ }

            if (counter is not null)
                _log.LogTrace(
                    "[net #{Id}] closed (target={Target}; host total {Bytes}B / {Reqs} req)",
                    connId, string.IsNullOrEmpty(targetHost) ? "?" : targetHost,
                    Interlocked.Read(ref counter.Bytes), Interlocked.Read(ref counter.Requests));
        }
        catch (OperationCanceledException) { /* shutdown / timeout */ }
        catch (IOException ex)
        {
            // Peer closed mid-stream. Usually normal (browser navigated
            // away), but during an upstream brown-out it's the proxy
            // hanging up — visible only under net-trace.
            _log.LogTrace(ex, "[net #{Id}] stream closed mid-transfer (target={Target})",
                connId, string.IsNullOrEmpty(targetHost) ? "?" : targetHost);
        }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "[net #{Id}] socket error (target={Target})",
                connId, string.IsNullOrEmpty(targetHost) ? "?" : targetHost);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[net #{Id}] connection failed (target={Target})",
                connId, string.IsNullOrEmpty(targetHost) ? "?" : targetHost);
        }
        finally
        {
            Interlocked.Decrement(ref _connActive);
            try { client.Close(); }   catch { /* swallow */ }
            try { upstream?.Close(); } catch { /* swallow */ }
        }
    }

    /// <summary>Phase 28 — Stream.CopyToAsync analogue that bills each
    /// chunk's length to <paramref name="counter"/> via Interlocked.
    /// Bills BOTH directions to the same host counter (request +
    /// response) — that's what the user sees as "total traffic for
    /// host X" in the dashboard.</summary>
    private static async Task CountingCopyAsync(
        Stream src, Stream dst, HostCounter? counter, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        while (true)
        {
            int n;
            try { n = await src.ReadAsync(buf, ct); }
            catch (IOException)            { return; }
            catch (ObjectDisposedException) { return; }
            if (n <= 0) return;
            try { await dst.WriteAsync(buf.AsMemory(0, n), ct); }
            catch (IOException)            { return; }
            catch (ObjectDisposedException) { return; }
            if (counter is not null) Interlocked.Add(ref counter.Bytes, n);
        }
    }

    /// <summary>Phase 28 — drain the per-host counter table and return
    /// a snapshot keyed by hostname (lowercase). The collector thread is
    /// the only reader; per-connection writers keep Interlocked-adding
    /// into the same HostCounter objects.</summary>
    /// <remarks>
    /// audit PROXY-07: we do NOT swap or Clear() the dictionary (the old
    /// doc comment claimed a Clear()-swap that never happened). We
    /// atomically Interlocked.Exchange each counter's Bytes/Requests to
    /// zero, copying the previous values into the snapshot — so values
    /// are billed exactly once and writes landing mid-drain are counted
    /// on the NEXT drain.
    ///
    /// To stop the dictionary growing unboundedly for long-lived
    /// sessions that touch tens of thousands of hosts (CDNs, trackers),
    /// we TryRemove entries that were already at zero before this drain
    /// (i.e. idle since the last drain). We accept the documented tiny
    /// race: a writer can re-add a host concurrently with TryRemove —
    /// worst case we drop a handful of bytes that get re-counted under a
    /// fresh HostCounter on the next request, which is fine for hourly
    /// bucket aggregation.
    /// </remarks>
    public IReadOnlyDictionary<string, (long Bytes, long Requests)> DrainCounters()
    {
        var snapshot = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _counters)
        {
            var bytes = Interlocked.Exchange(ref kv.Value.Bytes, 0);
            var reqs  = Interlocked.Exchange(ref kv.Value.Requests, 0);
            if (bytes == 0 && reqs == 0)
            {
                // audit PROXY-07: idle host (no traffic since the prior
                // drain) — evict it so the table doesn't grow forever.
                // TryRemove is keyed by value reference, so a concurrent
                // GetOrAdd that replaced the HostCounter won't be dropped.
                ((ICollection<KeyValuePair<string, HostCounter>>)_counters)
                    .Remove(new KeyValuePair<string, HostCounter>(kv.Key, kv.Value));
                continue;
            }
            snapshot[kv.Key] = (bytes, reqs);
        }
        return snapshot;
    }

    // ─────────────────────────────────────────────────────────
    // Header parsing & rewriting
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// audit PROXY-01: true when the first request-line is a CONNECT
    /// (i.e. an HTTPS tunnel). CONNECT gets auth injected once and then
    /// raw-pumped; plain HTTP needs per-request auth injection.
    /// </summary>
    private static bool IsConnectRequest(byte[] headers)
    {
        // "CONNECT " is 8 bytes; compare ASCII, case-sensitive (the
        // method token is always upper-case per RFC 7231).
        ReadOnlySpan<byte> connect = "CONNECT "u8;
        if (headers.Length < connect.Length) return false;
        for (var i = 0; i < connect.Length; i++)
            if (headers[i] != connect[i]) return false;
        return true;
    }

    /// <summary>
    /// audit PROXY-01: keep-alive HTTP request loop for the
    /// client→upstream direction. Frames each successive HTTP request
    /// on the connection, injects <c>Proxy-Authorization</c> into every
    /// one (not just the first), bills it (audit PROXY-06: one Requests
    /// increment per real request, not per TCP connection), and forwards
    /// it upstream. Exits when the client half-closes, the request can't
    /// be parsed, or the connection upgrades to an opaque protocol
    /// (WebSocket / CONNECT), after which we fall back to a raw copy.
    /// </summary>
    private async Task PumpHttpRequestsAsync(
        BufferedSocketReader reader, Stream upstream, HostCounter? counter, CancellationToken ct)
    {
        while (true)
        {
            var headers = await reader.ReadHeadersAsync(64 * 1024, ct);
            if (headers.Length == 0) return; // client half-closed — done.

            // A second CONNECT or an Upgrade turns the stream opaque;
            // stop framing and let the caller's raw copy take over the
            // remaining bytes (we've already consumed exactly this
            // header block, the rest stays buffered in the reader).
            if (IsConnectRequest(headers) || HasHeader(headers, "upgrade:"u8.ToArray()))
            {
                var passthrough = InjectAuth(headers);
                await upstream.WriteAsync(passthrough, ct);
                await upstream.FlushAsync(ct);
                if (counter is not null)
                {
                    Interlocked.Increment(ref counter.Requests);
                    Interlocked.Add(ref counter.Bytes, passthrough.Length);
                }
                await CountingCopyAsync(reader.AsStream(), upstream, counter, ct);
                return;
            }

            await ForwardHttpRequestAsync(reader, upstream, headers, counter, ct);
        }
    }

    /// <summary>
    /// audit PROXY-01 / PROXY-06: inject auth into one already-read HTTP
    /// header block, forward it upstream, then forward exactly that
    /// request's body (Content-Length or chunked Transfer-Encoding) so we
    /// stay byte-framed for the next pipelined request. Counts one
    /// request and bills every byte written upstream.
    /// </summary>
    private async Task ForwardHttpRequestAsync(
        BufferedSocketReader reader, Stream upstream, byte[] headers, HostCounter? counter, CancellationToken ct)
    {
        var modified = InjectAuth(headers);
        await upstream.WriteAsync(modified, ct);

        if (counter is not null)
        {
            Interlocked.Increment(ref counter.Requests);
            Interlocked.Add(ref counter.Bytes, modified.Length);
        }

        // Forward the request body, if any, so the byte stream stays
        // aligned to request boundaries for the next iteration.
        if (IsChunkedTransfer(headers))
        {
            await ForwardChunkedBodyAsync(reader, upstream, counter, ct);
        }
        else
        {
            var contentLength = GetContentLength(headers);
            if (contentLength > 0)
                await ForwardFixedBodyAsync(reader, upstream, contentLength, counter, ct);
        }

        await upstream.FlushAsync(ct);
    }

    /// <summary>Forward exactly <paramref name="length"/> body bytes
    /// from the buffered client reader to upstream.</summary>
    private static async Task ForwardFixedBodyAsync(
        BufferedSocketReader reader, Stream upstream, long length, HostCounter? counter, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var want = (int)Math.Min(remaining, buf.Length);
            var n = await reader.ReadAsync(buf.AsMemory(0, want), ct);
            if (n <= 0) return; // client closed mid-body — bail.
            await upstream.WriteAsync(buf.AsMemory(0, n), ct);
            if (counter is not null) Interlocked.Add(ref counter.Bytes, n);
            remaining -= n;
        }
    }

    /// <summary>Forward a chunked-transfer body verbatim (chunk-size
    /// lines + data + trailing CRLF) up to and including the
    /// zero-length terminating chunk, so the next pipelined request is
    /// correctly framed.</summary>
    private static async Task ForwardChunkedBodyAsync(
        BufferedSocketReader reader, Stream upstream, HostCounter? counter, CancellationToken ct)
    {
        while (true)
        {
            // chunk-size line, e.g. "1a3\r\n" (may carry chunk-ext).
            var sizeLine = await reader.ReadLineAsync(ct);
            if (sizeLine.Length == 0) return; // closed mid-body.

            await upstream.WriteAsync(sizeLine, ct);
            if (counter is not null) Interlocked.Add(ref counter.Bytes, sizeLine.Length);

            var chunkSize = ParseChunkSize(sizeLine);
            if (chunkSize == 0)
            {
                // Last chunk — forward trailers up to the final blank
                // line, then the body is complete.
                while (true)
                {
                    var trailer = await reader.ReadLineAsync(ct);
                    if (trailer.Length == 0) return;
                    await upstream.WriteAsync(trailer, ct);
                    if (counter is not null) Interlocked.Add(ref counter.Bytes, trailer.Length);
                    // A bare CRLF line ("\r\n") terminates the trailer section.
                    if (trailer.Length == 2 && trailer[0] == (byte)'\r' && trailer[1] == (byte)'\n')
                        return;
                }
            }

            // Forward chunkSize data bytes plus the trailing CRLF.
            await ForwardFixedBodyAsync(reader, upstream, chunkSize + 2, counter, ct);
        }
    }

    private static int ParseChunkSize(byte[] line)
    {
        // Hex up to the first ';' (chunk extension) or CR.
        var value = 0;
        foreach (var b in line)
        {
            int d;
            if (b >= (byte)'0' && b <= (byte)'9') d = b - (byte)'0';
            else if (b >= (byte)'a' && b <= (byte)'f') d = b - (byte)'a' + 10;
            else if (b >= (byte)'A' && b <= (byte)'F') d = b - (byte)'A' + 10;
            else break; // ';', '\r', whitespace — end of size token.
            value = (value << 4) | d;
        }
        return value;
    }

    /// <summary>Parse the Content-Length header value (decimal) from a
    /// request header block; 0 when absent or unparseable.</summary>
    private static long GetContentLength(byte[] headers)
    {
        var value = ReadHeaderValue(headers, "content-length:"u8.ToArray());
        if (value is null) return 0;
        return long.TryParse(value.Trim(), out var len) && len > 0 ? len : 0;
    }

    private static bool IsChunkedTransfer(byte[] headers)
    {
        var value = ReadHeaderValue(headers, "transfer-encoding:"u8.ToArray());
        return value is not null &&
               value.Contains("chunked", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Return the (Latin1-decoded) value of the first header
    /// whose name matches <paramref name="needleLower"/> (lower-case,
    /// includes the trailing colon), or null if absent.</summary>
    private static string? ReadHeaderValue(byte[] data, byte[] needleLower)
    {
        var firstNl = IndexOfSequence(data, "\r\n"u8.ToArray());
        if (firstNl < 0) return null;

        var i = firstNl + 2;
        while (i < data.Length)
        {
            var lineEnd = IndexOfSequence(data.AsSpan(i), "\r\n"u8.ToArray());
            var lineLen = lineEnd < 0 ? data.Length - i : lineEnd;
            if (lineLen == 0) break; // blank line — end of headers.

            if (lineLen > needleLower.Length)
            {
                var match = true;
                for (var j = 0; j < needleLower.Length; j++)
                {
                    var b = data[i + j];
                    if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 32);
                    if (b != needleLower[j]) { match = false; break; }
                }
                if (match)
                    return Encoding.Latin1.GetString(
                        data, i + needleLower.Length, lineLen - needleLower.Length);
            }

            if (lineEnd < 0) break;
            i += lineEnd + 2;
        }
        return null;
    }

    /// <summary>
    /// Insert the cached <c>Proxy-Authorization</c> header right
    /// after the request-line. Skips insertion when the client
    /// already supplied one (shouldn't happen — Chromium doesn't
    /// know our creds — but be idempotent).
    /// </summary>
    private byte[] InjectAuth(byte[] data)
    {
        if (_authHeader.Length == 0) return data;

        // Already present? (case-insensitive ASCII match).
        if (HasHeader(data, "proxy-authorization:"u8.ToArray()))
            return data;

        // Find end of request-line.
        var nl = IndexOfSequence(data, "\r\n"u8.ToArray());
        if (nl < 0) return data;

        // request-line + CRLF + auth + CRLF + rest.
        var crlf = "\r\n"u8.ToArray();
        var total = nl + crlf.Length + _authHeader.Length + crlf.Length + (data.Length - nl - crlf.Length);
        var output = new byte[total];
        var pos = 0;
        Buffer.BlockCopy(data, 0, output, pos, nl);                  pos += nl;
        Buffer.BlockCopy(crlf, 0, output, pos, crlf.Length);         pos += crlf.Length;
        Buffer.BlockCopy(_authHeader, 0, output, pos, _authHeader.Length); pos += _authHeader.Length;
        Buffer.BlockCopy(crlf, 0, output, pos, crlf.Length);         pos += crlf.Length;
        var restStart = nl + crlf.Length;
        Buffer.BlockCopy(data, restStart, output, pos, data.Length - restStart);
        return output;
    }

    private static bool HasHeader(byte[] data, byte[] needleLower)
    {
        // Linear scan of header lines (skip request-line). Tiny —
        // proxy requests have a handful of headers max.
        var firstNl = IndexOfSequence(data, "\r\n"u8.ToArray());
        if (firstNl < 0) return false;

        var i = firstNl + 2;
        while (i < data.Length)
        {
            // Compare against needle, case-insensitive ASCII.
            if (i + needleLower.Length > data.Length) return false;
            var match = true;
            for (var j = 0; j < needleLower.Length; j++)
            {
                var b = data[i + j];
                if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 32);
                if (b != needleLower[j]) { match = false; break; }
            }
            if (match) return true;

            // Advance to next CRLF.
            var nextNl = IndexOfSequence(data.AsSpan(i), "\r\n"u8.ToArray());
            if (nextNl < 0) return false;
            i += nextNl + 2;
        }
        return false;
    }

    private static int IndexOfSequence(ReadOnlySpan<byte> haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        var last = haystack.Length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle) =>
        IndexOfSequence(haystack.AsSpan(), needle);

    /// <summary>
    /// Pull the eventual destination host from the first request
    /// line — purely for log breadcrumbs. Empty string when we
    /// can't parse it (handler still forwards regardless).
    /// </summary>
    private static string ExtractTargetHost(byte[] data)
    {
        try
        {
            var nl = IndexOfSequence(data, "\r\n"u8.ToArray());
            if (nl < 0) return "";

            var firstLine = Encoding.Latin1.GetString(data, 0, nl);

            // CONNECT host:port HTTP/1.1
            if (firstLine.StartsWith("CONNECT ", StringComparison.Ordinal))
            {
                var parts = firstLine.Split(' ');
                if (parts.Length >= 2)
                    return parts[1].Split(':')[0].ToLowerInvariant();
            }

            // <METHOD> http://host/... HTTP/1.1
            var bits = firstLine.Split(' ');
            if (bits.Length >= 2 &&
                (bits[1].StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
                 bits[1].StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                Uri.TryCreate(bits[1], UriKind.Absolute, out var u))
            {
                return u.Host.ToLowerInvariant();
            }
        }
        catch { /* ignore — diagnostics only */ }
        return "";
    }

    // ─────────────────────────────────────────────────────────
    // Buffered socket reader (audit PROXY-01)
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// audit PROXY-01: a thin read-side buffer over a NetworkStream.
    /// The legacy header read went straight to the socket, so any bytes
    /// it read past the <c>\r\n\r\n</c> terminator (the body, or a
    /// pipelined follow-up request) were dropped. To re-frame and re-auth
    /// each request on a keep-alive HTTP connection we must keep that
    /// over-read around. This reader buffers it and serves it back before
    /// touching the socket again. It also exposes a <see cref="Stream"/>
    /// view (<see cref="AsStream"/>) so the raw-tunnel fallback drains the
    /// buffer first, then the live socket.
    /// </summary>
    private sealed class BufferedSocketReader
    {
        private readonly Stream _inner;
        private byte[] _buffer = Array.Empty<byte>();
        private int _start;
        private int _end;

        public BufferedSocketReader(Stream inner) => _inner = inner;

        private int Buffered => _end - _start;

        /// <summary>Fill the buffer from the socket when empty. Returns
        /// the number of bytes now buffered (0 == socket EOF).</summary>
        private async ValueTask<int> FillAsync(CancellationToken ct)
        {
            if (Buffered > 0) return Buffered;
            if (_buffer.Length == 0) _buffer = new byte[16 * 1024];
            _start = 0;
            _end = await _inner.ReadAsync(_buffer, ct);
            if (_end < 0) _end = 0;
            return _end;
        }

        /// <summary>Read body/opaque bytes — buffered leftovers first,
        /// then a single socket read. Mirrors Stream.ReadAsync semantics
        /// (0 == EOF).</summary>
        public async ValueTask<int> ReadAsync(Memory<byte> dst, CancellationToken ct)
        {
            if (Buffered == 0)
            {
                // Bypass the buffer for large reads once it's drained.
                if (await FillAsync(ct) == 0) return 0;
            }
            var n = Math.Min(Buffered, dst.Length);
            _buffer.AsMemory(_start, n).CopyTo(dst);
            _start += n;
            return n;
        }

        /// <summary>
        /// Read up to and including the first blank line (\r\n\r\n) — the
        /// end of an HTTP request-line + headers block. Returns whatever
        /// is buffered if <paramref name="maxBytes"/> is hit first, or an
        /// empty array on a clean EOF before any bytes arrive.
        /// </summary>
        public async Task<byte[]> ReadHeadersAsync(int maxBytes, CancellationToken ct)
        {
            using var ms = new MemoryStream(capacity: 4096);
            // Terminator is CRLF CRLF = {13,10,13,10}. We track how many
            // bytes have matched so far. (Kept as plain bytes rather than
            // a ReadOnlySpan local because a ref struct cannot stay alive
            // across the awaits below.)
            const byte CR = 13, LF = 10;
            var matched = 0;

            while (ms.Length < maxBytes)
            {
                if (await FillAsync(ct) == 0) break; // EOF.

                // Consume buffered bytes one at a time, tracking the
                // terminator so we stop the instant it completes and
                // leave the remainder in the buffer for the next read.
                while (_start < _end)
                {
                    var b = _buffer[_start++];
                    ms.WriteByte(b);
                    // matched: 0,2 expect CR; 1,3 expect LF.
                    var expected = (matched & 1) == 0 ? CR : LF;
                    matched = b == expected ? matched + 1 : (b == CR ? 1 : 0);
                    if (matched == 4)
                        return ms.ToArray();
                    if (ms.Length >= maxBytes)
                        return ms.ToArray();
                }
            }
            return ms.ToArray();
        }

        /// <summary>Read a single CRLF-terminated line, inclusive of the
        /// CRLF. Returns an empty array on EOF.</summary>
        public async Task<byte[]> ReadLineAsync(CancellationToken ct)
        {
            using var ms = new MemoryStream(capacity: 64);
            var sawCr = false;
            while (true)
            {
                if (await FillAsync(ct) == 0) break;
                while (_start < _end)
                {
                    var b = _buffer[_start++];
                    ms.WriteByte(b);
                    if (sawCr && b == (byte)'\n') return ms.ToArray();
                    sawCr = b == (byte)'\r';
                }
            }
            return ms.ToArray();
        }

        /// <summary>Expose the reader as a forward-only Stream so the
        /// raw-tunnel pump (CONNECT / upgraded connections) drains any
        /// buffered bytes before reading the live socket.</summary>
        public Stream AsStream() => new ReaderStream(this);

        private sealed class ReaderStream : Stream
        {
            private readonly BufferedSocketReader _r;
            public ReaderStream(BufferedSocketReader r) => _r = r;

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken ct = default) =>
                await _r.ReadAsync(buffer, ct);

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead  => true;
            public override bool CanSeek  => false;
            public override bool CanWrite => false;
            public override long Length   => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    // ─────────────────────────────────────────────────────────
    // Disposal
    // ─────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_listener is null) return;

        try { _stopCts?.Cancel(); } catch { /* swallow */ }
        try { _listener.Stop(); }   catch { /* swallow */ }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* swallow — accept loop unwinds via cancellation */ }
        }

        _stopCts?.Dispose();
        _listener  = null;
        _stopCts   = null;
        _acceptLoop = null;

        _log.LogDebug("Auth-proxy forwarder ({Local}) stopped", _localUrl ?? "?");
    }
}

/// <summary>
/// Default factory — pulls a fresh forwarder + logger from DI on
/// each <see cref="Create"/>. Kept tiny so DI registration stays
/// boring (singleton factory + transient forwarder via the factory).
/// </summary>
public sealed class HttpConnectForwarderFactory : IProxyAuthForwarderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public HttpConnectForwarderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IProxyAuthForwarder Create() =>
        new HttpConnectForwarder(_loggerFactory.CreateLogger<HttpConnectForwarder>());
}
