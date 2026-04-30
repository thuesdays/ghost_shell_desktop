// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

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

        _upstreamHost = uri.Host;
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
            "Auth-proxy forwarder ready: 127.0.0.1:{Local} → {Up}:{UpPort} (auth={HasAuth})",
            port, _upstreamHost, _upstreamPort, _authHeader.Length > 0);

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
        try
        {
            client.NoDelay = true;
            using var clientStream = client.GetStream();

            // Read request headers up to the first blank line. Cap at
            // 64 KiB — anything bigger than that on a CONNECT/proxy
            // request is malformed.
            var headers = await ReadHeadersAsync(clientStream, 64 * 1024, ct);
            if (headers.Length == 0)
            {
                _log.LogDebug("Empty request — client disconnected before sending headers");
                return;
            }

            var targetHost = ExtractTargetHost(headers);

            // Connect to the real proxy.
            upstream = new TcpClient { NoDelay = true };
            using var upstreamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            upstreamCts.CancelAfter(TimeSpan.FromSeconds(30));
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, upstreamCts.Token);
            using var upstreamStream = upstream.GetStream();

            var modified = InjectAuth(headers);
            await upstreamStream.WriteAsync(modified, ct);
            await upstreamStream.FlushAsync(ct);

            _log.LogTrace(
                "Forwarding {Bytes}B header block (target={Target})",
                modified.Length, string.IsNullOrEmpty(targetHost) ? "?" : targetHost);

            // Bidirectional pump. Either side hitting EOF / error
            // tears the whole thing down.
            //
            // After WhenAny returns we must force the OTHER copy to
            // unblock — otherwise it sits inside ReadAsync until
            // its own peer closes (which can take indefinitely if
            // the upstream proxy is slow to drain). Closing both
            // streams via the finally block does the trick: the
            // pending ReadAsync throws ObjectDisposed/IOException
            // and the loser task exits. We swallow whichever it
            // threw — by then we already know the connection is
            // dead.
            var c2u = clientStream.CopyToAsync(upstreamStream, ct);
            var u2c = upstreamStream.CopyToAsync(clientStream, ct);
            await Task.WhenAny(c2u, u2c);

            // Politely close both sides; the surviving copy will
            // observe the close and exit. Wrap in try-catch — the
            // streams might already be in a torn-down state.
            try { client.Close();   } catch { /* swallow */ }
            try { upstream.Close(); } catch { /* swallow */ }
            try { await Task.WhenAll(c2u, u2c).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* expected — the loser threw on the closed stream */ }
        }
        catch (OperationCanceledException) { /* shutdown / timeout */ }
        catch (IOException) { /* peer closed mid-stream — normal */ }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "Forwarder socket error (peer reset)");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Forwarder connection failed");
        }
        finally
        {
            try { client.Close(); }   catch { /* swallow */ }
            try { upstream?.Close(); } catch { /* swallow */ }
        }
    }

    // ─────────────────────────────────────────────────────────
    // Header parsing & rewriting
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Read the byte stream up to and including the first blank line
    /// (\r\n\r\n) — that's the end of the HTTP request-line + headers
    /// block. Returns whatever was buffered if the cap is hit first.
    /// </summary>
    private static async Task<byte[]> ReadHeadersAsync(
        NetworkStream stream, int maxBytes, CancellationToken ct)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream(capacity: 4096);
        var terminator = "\r\n\r\n"u8.ToArray();

        while (ms.Length < maxBytes)
        {
            var read = await stream.ReadAsync(buf, ct);
            if (read == 0) break;
            ms.Write(buf, 0, read);

            // Scan for terminator; cheap because requests are tiny.
            if (ContainsSequence(ms.GetBuffer(), (int)ms.Length, terminator))
                break;
        }
        return ms.ToArray();
    }

    private static bool ContainsSequence(byte[] haystack, int length, byte[] needle)
    {
        if (length < needle.Length) return false;
        var last = length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
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
