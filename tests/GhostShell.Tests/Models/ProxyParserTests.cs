// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using Xunit;

namespace GhostShell.Tests.Models;

/// <summary>
/// Bulk-import parser is the single most-touched code path during
/// onboarding — every user pastes a 50-line proxy list into the
/// import dialog within their first session. A regression here
/// surfaces as "import did nothing" or "78/100 silently skipped",
/// neither of which shows up in logs without these tests.
/// </summary>
public class ProxyParserTests
{
    // ─── Single-line variants ────────────────────────────────────

    // Format strings here MUST match the literal labels emitted by
    // ProxyParser.ParseLine — they're surfaced to the UI as the
    // "parsed as: …" hint in the bulk-import preview, so a rename
    // would silently break existing translation strings. Keep them
    // pinned by these tests.
    [Theory]
    [InlineData("1.2.3.4:8080",                "host_port",            "1.2.3.4", 8080)]
    [InlineData("user:pass@1.2.3.4:8080",      "creds_at_host_port",   "1.2.3.4", 8080)]
    [InlineData("1.2.3.4:8080@user:pass",      "host_port_at_creds",   "1.2.3.4", 8080)]
    [InlineData("1.2.3.4:8080:user:pass",      "host_port_user_pass",  "1.2.3.4", 8080)]
    [InlineData("user:pass:1.2.3.4:8080",      "user_pass_host_port",  "1.2.3.4", 8080)]
    [InlineData("http://1.2.3.4:8080",         "canonical",            "1.2.3.4", 8080)]
    [InlineData("socks5://1.2.3.4:1080",       "canonical",            "1.2.3.4", 1080)]
    public void ParseLine_AcceptsAllFormats(
        string input, string expectedFormat, string expectedHost, int expectedPort)
    {
        var p = ProxyParser.ParseLine(input);

        Assert.NotNull(p);
        Assert.True(p!.Ok, p.Error);
        Assert.Equal(expectedFormat, p.Format);
        Assert.Equal(expectedHost,   p.Host);
        Assert.Equal(expectedPort,   p.Port);
    }

    [Fact]
    public void ParseLine_PreservesCredentials()
    {
        // The 4-part formats are the trickiest because there's no
        // delimiter to disambiguate "host:port:user:pass" from
        // "user:pass:host:port" — the parser uses the host-shape
        // heuristic. Make sure we keep the creds aligned with the
        // host once it disambiguates.
        var hostFirst = ProxyParser.ParseLine("1.2.3.4:8080:alice:secret");
        var userFirst = ProxyParser.ParseLine("alice:secret:1.2.3.4:8080");

        Assert.NotNull(hostFirst); Assert.NotNull(userFirst);
        Assert.Equal("alice",  hostFirst!.Username);
        Assert.Equal("secret", hostFirst.Password);
        Assert.Equal("alice",  userFirst!.Username);
        Assert.Equal("secret", userFirst.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# this is a comment")]
    [InlineData("   # commented")]
    public void ParseLine_BlanksAndCommentsReturnNull(string line)
    {
        Assert.Null(ProxyParser.ParseLine(line));
    }

    [Fact]
    public void ParseLine_StripsTrailingComment()
    {
        // The real-world paste pattern. The '#' is NOT inside creds;
        // strip it before parsing or the format detector misfires.
        var p = ProxyParser.ParseLine("1.2.3.4:8080  # us-east-1");

        Assert.NotNull(p);
        Assert.True(p!.Ok);
        Assert.Equal("1.2.3.4", p.Host);
        Assert.Equal(8080,      p.Port);
    }

    [Theory]
    [InlineData("not a proxy")]
    [InlineData("1.2.3.4")]
    [InlineData("http://")]
    public void ParseLine_RejectsGarbage(string line)
    {
        var p = ProxyParser.ParseLine(line);
        Assert.NotNull(p);
        Assert.False(p!.Ok);
        Assert.NotNull(p.Error);
    }

    [Fact]
    public void ParseLine_LeadingAtSignIsAcceptedWithEmptyCreds()
    {
        // "@1.2.3.4:8080" technically passes the @-detect branch:
        // left="" right="1.2.3.4:8080", pRight succeeds, SplitCreds
        // on the empty string returns ("",""). The result is a valid
        // host:port with no creds. It's degenerate input but it's
        // not an error condition — pin the behaviour so a future
        // rewrite doesn't accidentally start rejecting it.
        var p = ProxyParser.ParseLine("@1.2.3.4:8080");
        Assert.NotNull(p);
        Assert.True(p!.Ok);
        Assert.Equal("1.2.3.4", p.Host);
        Assert.Equal(8080,      p.Port);
        Assert.Null(p.Username);
        Assert.Null(p.Password);
    }

    // ─── Bulk parsing ────────────────────────────────────────────

    [Fact]
    public void ParseBulk_CountsValidsErrorsTotal()
    {
        var paste = string.Join("\n", new[]
        {
            "1.2.3.4:8080",
            "",
            "# comment",
            "broken-line",
            "user:pass@5.6.7.8:1080",
            "http://1.2.3.4:9999",
        });

        var result = ProxyParser.ParseBulk(paste);

        Assert.Equal(3, result.Valid.Count);
        Assert.Single(result.Errors);
        // 4 non-blank, non-comment lines → blank/# excluded
        Assert.Equal(4, result.TotalNonBlankLines);
    }

    [Fact]
    public void ParseBulk_HandlesCRLF()
    {
        // Windows clipboards often produce \r\n line endings. The
        // parser splits on \n and trims \r — verify both endings
        // produce identical results.
        var lf   = "1.1.1.1:1\n2.2.2.2:2";
        var crlf = "1.1.1.1:1\r\n2.2.2.2:2\r\n";

        var rLf   = ProxyParser.ParseBulk(lf);
        var rCrlf = ProxyParser.ParseBulk(crlf);

        Assert.Equal(rLf.Valid.Count,   rCrlf.Valid.Count);
        Assert.Equal(rLf.Errors.Count,  rCrlf.Errors.Count);
        Assert.Equal(2, rCrlf.Valid.Count);
    }

    [Fact]
    public void ParseBulk_NullInputYieldsEmpty()
    {
        var result = ProxyParser.ParseBulk(null);
        Assert.Empty(result.Valid);
        Assert.Empty(result.Errors);
        Assert.Equal(0, result.TotalNonBlankLines);
    }
}
