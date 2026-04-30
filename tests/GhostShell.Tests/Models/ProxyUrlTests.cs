// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using Xunit;

namespace GhostShell.Tests.Models;

/// <summary>
/// ProxyUrl is the gateway between user input (paste field, CSV
/// import, profile editor) and Chromium's <c>--proxy-server</c>
/// flag. Mis-parsing a credentialed URL leaks creds to logs;
/// mis-parsing a host:port crashes the launcher with "DevToolsActive
/// Port doesn't exist". Both fatal — keep this suite tight and the
/// production parser stays honest.
/// </summary>
public class ProxyUrlTests
{
    [Theory]
    [InlineData("1.2.3.4:8080",                "http",   "1.2.3.4",     8080, null,   null)]
    [InlineData("http://1.2.3.4:8080",         "http",   "1.2.3.4",     8080, null,   null)]
    [InlineData("https://example.com:443",     "https",  "example.com", 443,  null,   null)]
    [InlineData("user:pass@1.2.3.4:8080",      "http",   "1.2.3.4",     8080, "user", "pass")]
    [InlineData("http://u:p@host:8080",        "http",   "host",        8080, "u",    "p")]
    [InlineData("socks5://1.2.3.4:1080",       "socks5", "1.2.3.4",     1080, null,   null)]
    public void TryParse_AcceptsAllSupportedShapes(
        string input, string expectedScheme, string expectedHost,
        int expectedPort, string? expectedUser, string? expectedPass)
    {
        var ok = ProxyUrl.TryParse(input, out var url);

        Assert.True(ok, $"expected '{input}' to parse");
        Assert.Equal(expectedScheme, url.Scheme);
        Assert.Equal(expectedHost,   url.Host);
        Assert.Equal(expectedPort,   url.Port);
        Assert.Equal(expectedUser,   url.Username);
        Assert.Equal(expectedPass,   url.Password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void TryParse_RejectsGarbage(string? input)
    {
        var ok = ProxyUrl.TryParse(input, out var url);
        Assert.False(ok);
        Assert.False(url.IsValid);
    }

    [Theory]
    [InlineData("just-a-host", 80)]   // synthetic http:// scheme → default port 80
    [InlineData("host:",       80)]   // empty port falls back to scheme default
    public void TryParse_DefaultsPortFromScheme(string input, int expectedPort)
    {
        // Documenting: ProxyUrl trusts Uri's scheme-default port when
        // the input omits ":port". This is intentional — pasting
        // "proxy.example.com" without a port should still produce a
        // usable record (with port 80, the http default), even though
        // it's a strange thing to paste. The dialog editor warns the
        // user about it, but the parser itself accepts it.
        var ok = ProxyUrl.TryParse(input, out var url);
        Assert.True(ok);
        Assert.Equal(expectedPort, url.Port);
    }

    [Fact]
    public void TryParse_DecodesPercentEncodedCredentials()
    {
        // Real-world: copy-paste from a dashboard sometimes
        // URL-encodes the password. We must decode before passing
        // to the Basic-auth header builder.
        var ok = ProxyUrl.TryParse("http://user%40acme:p%40ssword@host:8080", out var url);

        Assert.True(ok);
        Assert.Equal("user@acme",   url.Username);
        Assert.Equal("p@ssword",    url.Password);
    }

    [Fact]
    public void TryParse_HandlesUsernameWithoutPassword()
    {
        // Some token-based proxies use username-only auth with the
        // password field empty. Accept and emit user-only.
        var ok = ProxyUrl.TryParse("http://just-a-token@host:8080", out var url);

        Assert.True(ok);
        Assert.Equal("just-a-token", url.Username);
        Assert.Null(url.Password);
    }

    [Theory]
    [InlineData("host:0")]            // port 0
    [InlineData("host:65536")]        // port too high
    [InlineData("host:99999999999")]  // overflow
    public void TryParse_RejectsOutOfRangePort(string input)
    {
        var ok = ProxyUrl.TryParse(input, out var url);
        Assert.False(ok);
        Assert.False(url.IsValid);
    }

    [Fact]
    public void HostPort_FormatsValidUrl()
    {
        ProxyUrl.TryParse("http://user:pass@1.2.3.4:8080", out var url);
        Assert.Equal("1.2.3.4:8080", url.HostPort);
    }

    [Fact]
    public void HostPort_RendersDashForInvalidUrl()
    {
        var url = new ProxyUrl();   // empty default
        Assert.Equal("—", url.HostPort);
    }
}
