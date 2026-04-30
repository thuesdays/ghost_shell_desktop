// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.RegularExpressions;

namespace GhostShell.Core.Models;

/// <summary>
/// Multi-format proxy bulk-paste parser. Mirrors
/// `ghost_shell/proxy/diagnostics.py:parse_proxy_line` from the
/// legacy Python project so import behaviour is identical.
///
/// Supported formats per line:
///   1. <c>scheme://user:pass@host:port</c>          (canonical URL)
///   2. <c>scheme://host:port</c>                    (canonical, no creds)
///   3. <c>host:port</c>                             (bare; default_scheme used)
///   4. <c>user:pass@host:port</c>                   (creds prefix)
///   5. <c>host:port@user:pass</c>                   (reversed)
///   6. <c>host:port:user:pass</c>                   (4-part)
///   7. <c>user:pass:host:port</c>                   (4-part reversed)
///   8. <c>[ipv6]:port[:user:pass]</c>               (bracketed IPv6)
///
/// Comments (anything after '#' on a line) and blank lines are
/// ignored. Each result line is tagged with the format that matched
/// so the UI can show "parsed as: host_port_user_pass".
/// </summary>
public static class ProxyParser
{
    private static readonly Regex CanonicalUrl =
        new(@"^(https?|socks[45])://(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Ipv6 =
        new(@"^\[([0-9a-fA-F:]+)\]:([0-9]{1,5})(:(.+))?$", RegexOptions.Compiled);

    /// <summary>
    /// Parse a multi-line bulk paste. Each non-blank, non-comment
    /// line becomes either a Valid or an Invalid entry. The summary
    /// fields make it easy for the UI to render
    /// "12 valid · 3 errors · 2 duplicates".
    /// </summary>
    public static BulkParseResult ParseBulk(string? text, string defaultScheme = "http")
    {
        var lines = (text ?? string.Empty).Split('\n');
        var valid  = new List<ParsedProxy>();
        var errors = new List<ParseError>();
        var totalNonBlank = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var stripped = raw.Trim();
            if (stripped.Length == 0 || stripped.StartsWith("#"))
                continue;

            totalNonBlank++;
            var p = ParseLine(raw, defaultScheme);
            if (p is null) continue;
            if (p.Ok) valid.Add(p);
            else errors.Add(new ParseError(i + 1, p.Raw, p.Error ?? "unknown"));
        }
        return new BulkParseResult(valid, errors, totalNonBlank);
    }

    /// <summary>Parse a single line. Null = blank/comment-only.</summary>
    public static ParsedProxy? ParseLine(string? line, string defaultScheme = "http")
    {
        if (line is null) return null;
        var raw = line;

        // Trim trailing comments BEFORE checking '@', otherwise a
        // comment like "# foo@bar" would be misread as creds.
        var hashPos = line.IndexOf('#');
        if (hashPos >= 0) line = line[..hashPos];
        line = line.Trim();
        if (line.Length == 0) return null;

        // 1. Canonical URL with scheme
        var m = CanonicalUrl.Match(line);
        if (m.Success)
        {
            var scheme = m.Groups[1].Value.ToLowerInvariant();
            var rest   = m.Groups[2].Value;
            var split  = SplitAuthority(rest);
            if (split is { } a)
                return Ok(raw, "canonical", scheme, a.Host, a.Port, a.User, a.Pass);
        }

        // 2-7. Schemeless. Disambiguate creds vs 4-part by '@'.
        if (line.Contains('@') && line.Count(c => c == '@') == 1)
        {
            var atIdx = line.IndexOf('@');
            var left  = line[..atIdx];
            var right = line[(atIdx + 1)..];

            var pRight = ParseHostPort(right);
            var pLeft  = ParseHostPort(left);

            if (pRight is { } hp1)
            {
                var (u, pw) = SplitCreds(left);
                return Ok(raw, "creds_at_host_port", defaultScheme, hp1.Host, hp1.Port, u, pw);
            }
            if (pLeft is { } hp2)
            {
                var (u, pw) = SplitCreds(right);
                return Ok(raw, "host_port_at_creds", defaultScheme, hp2.Host, hp2.Port, u, pw);
            }
        }

        // 8. IPv6 bracketed
        m = Ipv6.Match(line);
        if (m.Success)
        {
            var host = m.Groups[1].Value;
            var port = int.Parse(m.Groups[2].Value);
            var tail = m.Groups[4].Success ? m.Groups[4].Value : "";
            var (u, pw) = SplitCreds(tail);
            return Ok(raw, "ipv6_colon", defaultScheme, host, port, u, pw);
        }

        // 3 / 6 / 7 — colon-separated IPv4-style
        var parts = line.Split(':');
        switch (parts.Length)
        {
            case 2:
                {
                    if (TryPort(parts[1], out var port))
                        return Ok(raw, "host_port", defaultScheme, parts[0], port, "", "");
                    break;
                }
            case 4:
                {
                    var portA = TryPort(parts[1], out var pA) ? pA : 0;
                    var portB = TryPort(parts[3], out var pB) ? pB : 0;

                    if (portA > 0 && portB == 0)
                        return Ok(raw, "host_port_user_pass", defaultScheme,
                                  parts[0], portA, parts[2], parts[3]);
                    if (portB > 0 && portA == 0)
                        return Ok(raw, "user_pass_host_port", defaultScheme,
                                  parts[2], portB, parts[0], parts[1]);
                    if (portA > 0 && portB > 0)
                    {
                        // Tie-break: side that looks like a hostname wins
                        // (contains a dot or all-digit dotted IP).
                        if (LooksLikeHost(parts[0]))
                            return Ok(raw, "host_port_user_pass", defaultScheme,
                                      parts[0], portA, parts[2], parts[3]);
                        return Ok(raw, "user_pass_host_port", defaultScheme,
                                  parts[2], portB, parts[0], parts[1]);
                    }
                    break;
                }
        }

        return Fail(raw, "Couldn't recognize format. Try host:port, user:pass@host:port or full URL.");
    }

    // ─── Helpers ───

    private static (string Host, int Port, string? User, string? Pass)? SplitAuthority(string authority)
    {
        // Strip path/query — only authority part matters for proxies.
        var slash = authority.IndexOf('/');
        if (slash >= 0) authority = authority[..slash];

        string? user = null, pass = null;
        var atIdx = authority.LastIndexOf('@');
        if (atIdx >= 0)
        {
            var creds = authority[..atIdx];
            authority = authority[(atIdx + 1)..];
            (user, pass) = SplitCreds(creds);
        }

        var hp = ParseHostPort(authority);
        return hp is null ? null : (hp.Value.Host, hp.Value.Port, user, pass);
    }

    private static (string Host, int Port)? ParseHostPort(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        var colon = s.LastIndexOf(':');
        if (colon <= 0 || colon == s.Length - 1) return null;
        var host = s[..colon];
        if (!TryPort(s[(colon + 1)..], out var port)) return null;
        if (string.IsNullOrWhiteSpace(host)) return null;
        return (host, port);
    }

    private static (string? User, string? Pass) SplitCreds(string s)
    {
        if (string.IsNullOrEmpty(s)) return (null, null);
        var colon = s.IndexOf(':');
        if (colon < 0) return (s, null);
        return (s[..colon], s[(colon + 1)..]);
    }

    private static bool TryPort(string s, out int port)
    {
        port = 0;
        if (!int.TryParse(s, out var n)) return false;
        if (n is < 1 or > 65535) return false;
        port = n;
        return true;
    }

    private static bool LooksLikeHost(string s) =>
        s.Contains('.') || s.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private static ParsedProxy Ok(string raw, string format, string scheme,
                                   string host, int port, string? user, string? pass)
    {
        var creds = string.IsNullOrEmpty(user) ? "" :
                    string.IsNullOrEmpty(pass) ? Uri.EscapeDataString(user) + "@" :
                    $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass ?? "")}@";
        var url = $"{scheme}://{creds}{host}:{port}";
        return new ParsedProxy
        {
            Ok = true,
            Raw = raw,
            Url = url,
            Scheme = scheme,
            Host = host,
            Port = port,
            Username = string.IsNullOrEmpty(user) ? null : user,
            Password = string.IsNullOrEmpty(pass) ? null : pass,
            Format = format,
        };
    }

    private static ParsedProxy Fail(string raw, string error) => new()
    {
        Ok = false,
        Raw = raw,
        Error = error,
    };
}

public sealed class ParsedProxy
{
    public bool Ok { get; init; }
    public string Raw { get; init; } = "";
    public string? Url { get; init; }
    public string? Scheme { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>Which parser branch matched (for UI hint).</summary>
    public string? Format { get; init; }

    public string? Error { get; init; }

    /// <summary>Set by the import flow when a same-URL entry already exists.</summary>
    public bool IsDuplicate { get; set; }
}

public sealed record ParseError(int LineNumber, string Raw, string Error);

public sealed record BulkParseResult(
    IReadOnlyList<ParsedProxy> Valid,
    IReadOnlyList<ParseError> Errors,
    int TotalNonBlankLines);
