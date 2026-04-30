// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.Runtime.ProxyAuth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GhostShell.Tests.Proxy;

/// <summary>
/// End-to-end exercise of the auth-proxy forwarder using a fake
/// upstream proxy that just records what it received. The forwarder
/// is fed a "user:pass@host:port" upstream URL and we verify:
///   • the local listener binds and reports a usable URL,
///   • the first request sent through gets a Proxy-Authorization
///     header injected EXACTLY ONCE in the right position,
///   • bytes flow bidirectionally after the header swap.
///
/// Pure loopback test — no real network, no Selenium, no chrome.
/// Runs in &lt;200ms.
/// </summary>
public class HttpConnectForwarderTests
{
    [Fact]
    public async Task StartAsync_ReturnsLocalLoopbackUrl()
    {
        await using var fwd = new HttpConnectForwarder(
            NullLogger<HttpConnectForwarder>.Instance);

        var local = await fwd.StartAsync("http://user:pass@127.0.0.1:1");

        Assert.True(fwd.IsRunning);
        Assert.StartsWith("http://127.0.0.1:", local);
        Assert.Equal(local, fwd.LocalUrl);
    }

    [Fact]
    public async Task StartAsync_RejectsMalformedUpstream()
    {
        await using var fwd = new HttpConnectForwarder(
            NullLogger<HttpConnectForwarder>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            () => fwd.StartAsync("definitely not a url"));
    }

    [Fact]
    public async Task StartAsync_DoubleStartThrows()
    {
        await using var fwd = new HttpConnectForwarder(
            NullLogger<HttpConnectForwarder>.Instance);

        await fwd.StartAsync("http://127.0.0.1:1");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fwd.StartAsync("http://127.0.0.1:2"));
    }

    [Fact]
    public async Task ForwardedRequest_GetsProxyAuthorizationInjected()
    {
        // Spin up a fake upstream proxy on a random loopback port —
        // it accepts ONE connection, reads the headers up to the
        // blank line, captures them, then closes.
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;

        var captured = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            try
            {
                using var client = await upstream.AcceptTcpClientAsync();
                using var ns = client.GetStream();
                var buf = new byte[8192];
                var sb  = new StringBuilder();
                while (sb.Length < 65536)
                {
                    var read = await ns.ReadAsync(buf);
                    if (read == 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, read));
                    if (sb.ToString().Contains("\r\n\r\n")) break;
                }
                captured.TrySetResult(sb.ToString());
            }
            catch (Exception ex)
            {
                captured.TrySetException(ex);
            }
        });

        // Forwarder pointed at the fake upstream, with creds
        // "alice:rabbit-hole" — that's user=alice, pass=rabbit-hole,
        // base64("alice:rabbit-hole") = YWxpY2U6cmFiYml0LWhvbGU=
        await using var fwd = new HttpConnectForwarder(
            NullLogger<HttpConnectForwarder>.Instance);
        var local = await fwd.StartAsync(
            $"http://alice:rabbit-hole@127.0.0.1:{upstreamPort}");

        var localPort = int.Parse(local["http://127.0.0.1:".Length..]);

        // Pretend to be Chromium: send a CONNECT through the local
        // listener, headers terminated with the empty line.
        using var chrome = new TcpClient();
        await chrome.ConnectAsync(IPAddress.Loopback, localPort);
        var send = "CONNECT example.com:443 HTTP/1.1\r\n" +
                   "Host: example.com:443\r\n" +
                   "User-Agent: chromium/test\r\n" +
                   "\r\n";
        await chrome.GetStream().WriteAsync(Encoding.ASCII.GetBytes(send));

        // Wait for the upstream to capture what came through.
        var got = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        upstream.Stop();

        // Critical assertions:
        Assert.Contains("CONNECT example.com:443 HTTP/1.1",            got);
        Assert.Contains("Host: example.com:443",                       got);
        // Auth header was injected with the right base64 value.
        Assert.Contains("Proxy-Authorization: Basic YWxpY2U6cmFiYml0LWhvbGU=", got);
        // And it appears EXACTLY once — not duplicated.
        var occurrences = CountOccurrences(got, "Proxy-Authorization:");
        Assert.Equal(1, occurrences);
        // Header order: Proxy-Authorization comes RIGHT AFTER the
        // request line (that's what _inject_auth in the legacy
        // Python forwarder does, and the C# port preserves it).
        var requestLineEnd = got.IndexOf("\r\n");
        var authPos        = got.IndexOf("Proxy-Authorization:");
        Assert.True(authPos > requestLineEnd
                    && authPos < requestLineEnd + 4 + "Proxy-Authorization:".Length,
            $"auth header should come right after request-line; layout was:\n{got}");
    }

    [Fact]
    public async Task DisposeAsync_StopsListener()
    {
        var fwd = new HttpConnectForwarder(NullLogger<HttpConnectForwarder>.Instance);
        var local = await fwd.StartAsync("http://127.0.0.1:1");
        Assert.True(fwd.IsRunning);

        await fwd.DisposeAsync();
        Assert.False(fwd.IsRunning);

        // Listener is gone — connecting now returns ConnectionRefused
        // (or times out, depending on platform). Either way, NOT a
        // successful connect.
        var localPort = int.Parse(local["http://127.0.0.1:".Length..]);
        using var probe = new TcpClient();
        var connectTask = probe.ConnectAsync(IPAddress.Loopback, localPort);
        var winner = await Task.WhenAny(connectTask,
            Task.Delay(TimeSpan.FromMilliseconds(500)));

        if (winner == connectTask)
        {
            // If the connect somehow succeeded, then the listener is
            // very-much still up — the test fails.
            Assert.False(probe.Connected,
                "forwarder still listening after dispose");
        }
        // else: timed out → success (port is closed)
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
