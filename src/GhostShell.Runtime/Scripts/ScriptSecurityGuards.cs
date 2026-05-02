// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Net;

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Phase 21 — security helpers extracted as a public surface so the
/// unit-test suite can exercise SSRF, path traversal, and reserved-
/// name guards without going through the live runtime. ScriptRunner
/// thin-wraps these so production behaviour and tests share one
/// source of truth.
/// </summary>
public static class ScriptSecurityGuards
{
    /// <summary>
    /// SSRF guard for <c>http_request</c>. Returns true when the URL's
    /// host should NOT be reached over the network — loopback, RFC1918,
    /// link-local (v4 + v6), 0.0.0.0, IPv6 ULA. Does not resolve DNS;
    /// hostnames pointing at private IPs via DNS rebinding still pass.
    /// </summary>
    public static bool IsBlockedHost(Uri u)
    {
        var host = u.Host.ToLowerInvariant();
        if (host == "localhost") return true;
        if (u.IsLoopback) return true;
        if (IPAddress.TryParse(host, out var ip))
        {
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (b[0] == 0)   return true;                                // 0.0.0.0/8
                if (b[0] == 10)  return true;                                // 10.0.0.0/8
                if (b[0] == 127) return true;                                // 127.0.0.0/8
                if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;    // 172.16.0.0/12
                if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16
            }
            else
            {
                if ((b[0] & 0xfe) == 0xfc) return true;                      // fc00::/7 (ULA)
                if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;      // fe80::/10 (link-local)
            }
        }
        return false;
    }

    /// <summary>
    /// Clamp the <c>page</c> argument of <c>open_extension_*</c> to a
    /// single safe filename. Strips directory components, rejects "..",
    /// rejects path separators, whitelists known extension page
    /// extensions. Falls back to <c>"popup.html"</c> on any reject.
    /// </summary>
    public static string SanitiseExtensionPage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "popup.html";
        var leaf = System.IO.Path.GetFileName(raw);
        if (string.IsNullOrEmpty(leaf)) return "popup.html";
        if (leaf.IndexOfAny(new[] { '/', '\\', ':', ' ' }) >= 0
            || leaf.StartsWith("."))
        {
            return "popup.html";
        }
        var ext = System.IO.Path.GetExtension(leaf).ToLowerInvariant();
        if (ext is not (".html" or ".htm" or ".js" or ".json"))
        {
            return "popup.html";
        }
        return leaf;
    }

    /// <summary>True if the given variable name is one the runtime
    /// reserves and user scripts are forbidden from overwriting via
    /// <c>save_var</c>.</summary>
    public static bool IsReservedVarName(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        return name is "ad_href" or "ad_title" or "ad_id"
            or "ext_tab" or "_ext_origin_tab";
    }

    /// <summary>
    /// Validate a Chrome Web Store extension id — exactly 32 chars,
    /// each in [a-p].
    /// </summary>
    public static bool IsValidExtensionId(string id)
        => !string.IsNullOrEmpty(id)
        && System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-p]{32}$");
}
