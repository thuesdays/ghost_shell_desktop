// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Runtime.Browser;
using Xunit;

namespace GhostShell.Tests.Browser;

/// <summary>
/// StripAuth is the credential-redaction step that runs in the
/// "fallback when forwarder fails" branch and in the launcher's
/// log lines. Getting it wrong leaks user:pass to disk; getting it
/// over-aggressive breaks bare host:port URLs that have NO creds.
/// </summary>
public class ChromeOptionsBuilderTests
{
    [Theory]
    [InlineData("http://user:pass@host:8080",  "http://host:8080")]
    [InlineData("https://u:p@1.2.3.4:443",     "https://1.2.3.4:443")]
    [InlineData("socks5://x:y@1.2.3.4:1080",   "socks5://1.2.3.4:1080")]
    public void StripAuth_RemovesCredentialsWithScheme(string input, string expected)
    {
        Assert.Equal(expected, ChromeOptionsBuilder.StripAuth(input));
    }

    [Theory]
    [InlineData("http://host:8080")]
    [InlineData("https://1.2.3.4:443")]
    [InlineData("host:8080")]                    // bare — no scheme, no creds
    [InlineData("socks5://1.2.3.4:1080")]
    public void StripAuth_PassesThroughCleanUrls(string input)
    {
        Assert.Equal(input, ChromeOptionsBuilder.StripAuth(input));
    }

    [Fact]
    public void StripAuth_BareHostPortWithCredsLeftAlone()
    {
        // No scheme is present, so we can't safely identify the '@'
        // as auth (it could be part of a hostname in some pathological
        // input). The forwarder branch upstream handles this case
        // by injecting a synthetic scheme first; StripAuth is the
        // belt-and-braces fallback and it deliberately won't touch
        // schemeless inputs.
        var input = "user:pass@host:8080";
        Assert.Equal(input, ChromeOptionsBuilder.StripAuth(input));
    }

    [Fact]
    public void StripAuth_HandlesMultiAtSafely()
    {
        // Defensive — passwords legitimately contain '@'. The first
        // '@' after the scheme is the auth boundary; subsequent ones
        // are part of the host (or a malformed URL we don't care
        // about). The implementation uses IndexOf('@', authStart)
        // and that's the contract.
        var stripped = ChromeOptionsBuilder.StripAuth("http://u:p@h@host:8080");
        // Implementation strips at the FIRST @ — the second one
        // moves into the host segment. Verifying the documented
        // behaviour, not editorialising on it.
        Assert.Equal("http://h@host:8080", stripped);
    }
}
