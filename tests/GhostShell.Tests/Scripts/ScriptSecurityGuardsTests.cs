// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Runtime.Scripts;
using Xunit;

namespace GhostShell.Tests.Scripts;

/// <summary>
/// Phase 21 — security-guard contract tests. Each fix from the
/// 6-agent audit lives as a passing test here so regressions get
/// caught fast in CI.
/// </summary>
public class ScriptSecurityGuardsTests
{
    // ─── IsBlockedHost — SSRF ─────────────────────────────────────

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.255.255.254/")]
    [InlineData("http://0.0.0.0/")]                  // Phase 21 audit fix
    [InlineData("http://0.1.2.3/")]                  // 0.0.0.0/8
    [InlineData("http://10.0.0.1/")]                 // RFC1918
    [InlineData("http://10.255.255.255/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://192.168.255.255/")]
    [InlineData("http://169.254.169.254/")]          // EC2 IMDS
    [InlineData("http://[::1]/")]                    // IPv6 loopback
    [InlineData("http://[fc00::1]/")]                // IPv6 ULA
    [InlineData("http://[fd00::1]/")]                // IPv6 ULA upper half
    [InlineData("http://[fe80::1]/")]                // IPv6 link-local
    [InlineData("http://[fe80::affe:cafe]/")]
    public void IsBlockedHost_blocks_all_private_loopback_and_link_local(string url)
    {
        var u = new Uri(url);
        Assert.True(ScriptSecurityGuards.IsBlockedHost(u),
            $"expected {u.Host} to be blocked");
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://api.attacker.com/")]
    [InlineData("http://8.8.8.8/")]                  // Google DNS — public
    [InlineData("https://172.15.0.1/")]              // 172.15 is NOT private
    [InlineData("https://172.32.0.1/")]              // 172.32 is NOT private
    [InlineData("https://[2001:4860:4860::8888]/")]  // public IPv6
    public void IsBlockedHost_allows_public_hosts(string url)
    {
        var u = new Uri(url);
        Assert.False(ScriptSecurityGuards.IsBlockedHost(u),
            $"expected {u.Host} NOT to be blocked");
    }

    // ─── SanitiseExtensionPage — path traversal ───────────────────

    [Theory]
    [InlineData("../../manifest.json")]
    [InlineData("..\\..\\background.js")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32\\config\\sam")]
    [InlineData("subdir/popup.html")]
    [InlineData("popup.html?evil=1")]                // querystring → not a single filename
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".secret")]
    [InlineData("popup.exe")]                        // disallowed extension
    [InlineData("popup")]                            // no extension
    [InlineData("page.txt")]                         // not whitelisted
    public void SanitiseExtensionPage_rejects_unsafe_inputs(string raw)
    {
        var safe = ScriptSecurityGuards.SanitiseExtensionPage(raw);
        Assert.Equal("popup.html", safe);
    }

    [Theory]
    [InlineData("popup.html",   "popup.html")]
    [InlineData("options.html", "options.html")]
    [InlineData("home.htm",     "home.htm")]
    [InlineData("background.js","background.js")]
    [InlineData("manifest.json","manifest.json")]
    public void SanitiseExtensionPage_passes_safe_filenames(string raw, string expected)
    {
        Assert.Equal(expected, ScriptSecurityGuards.SanitiseExtensionPage(raw));
    }

    [Fact]
    public void SanitiseExtensionPage_returns_default_for_null_or_empty()
    {
        Assert.Equal("popup.html", ScriptSecurityGuards.SanitiseExtensionPage(""));
        Assert.Equal("popup.html", ScriptSecurityGuards.SanitiseExtensionPage("   "));
        Assert.Equal("popup.html", ScriptSecurityGuards.SanitiseExtensionPage(null!));
    }

    // ─── IsReservedVarName — save_var poisoning ───────────────────

    [Theory]
    [InlineData("ad_href")]
    [InlineData("ad_title")]
    [InlineData("ad_id")]
    [InlineData("ext_tab")]
    [InlineData("_ext_origin_tab")]
    [InlineData("")]
    [InlineData(null)]
    public void IsReservedVarName_blocks_runtime_owned_keys(string? name)
    {
        Assert.True(ScriptSecurityGuards.IsReservedVarName(name!));
    }

    [Theory]
    [InlineData("user_var")]
    [InlineData("counter")]
    [InlineData("ad_href_user")]   // partial match — should NOT collide
    [InlineData("Ad_Href")]        // case differs — Ordinal compare ⇒ allowed
    public void IsReservedVarName_allows_user_chosen_keys(string name)
    {
        Assert.False(ScriptSecurityGuards.IsReservedVarName(name));
    }

    // ─── IsValidExtensionId ───────────────────────────────────────

    [Theory]
    [InlineData("abcdefghijklmnopabcdefghijklmnop")] // 32 chars in [a-p]
    [InlineData("paaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void IsValidExtensionId_accepts_chrome_ids(string id)
    {
        Assert.True(ScriptSecurityGuards.IsValidExtensionId(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP")] // uppercase not allowed
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // 'z' is outside a-p
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // too long
    [InlineData("aaaa-aaaa-aaaa-aaaa-aaaa-aaaa-aa")] // dashes
    public void IsValidExtensionId_rejects_invalid(string id)
    {
        Assert.False(ScriptSecurityGuards.IsValidExtensionId(id));
    }
}
