// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Parses a proxy URL string into structured components without
/// throwing. Used by:
///   • the dialog editor (validation, "host:port" preview),
///   • the runtime (to feed Chromium --proxy-server flags),
///   • diagnostics (display "1.2.3.4 / port 8080" in the table).
///
/// Accepts any of these shapes:
///     http://user:pass@host:port
///     https://host:port
///     socks5://1.2.3.4:1080
///     user:pass@host:port      (scheme defaults to http)
///     host:port                (bare — scheme http, no creds)
/// </summary>
public sealed record ProxyUrl
{
    public string Scheme { get; init; } = "http";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535;

    /// <summary>Tries to parse. Returns false (and a default-shaped
    /// instance) when input is empty or malformed beyond recognition.</summary>
    public static bool TryParse(string? input, out ProxyUrl result)
    {
        result = new ProxyUrl();
        if (string.IsNullOrWhiteSpace(input)) return false;
        input = input.Trim();

        // Inject a synthetic scheme if the caller pasted a bare
        // host:port or user:pass@host:port — Uri.TryCreate only
        // works on absolute URIs.
        var withScheme = input.Contains("://") ? input : "http://" + input;

        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri))
            return false;

        var port = uri.Port;
        if (port <= 0)
        {
            // The Uri parser fills Port=-1 for schemes without a
            // default known to .NET (socks5). Pull it out manually.
            var lastColon = uri.Authority.LastIndexOf(':');
            if (lastColon > 0
                && int.TryParse(uri.Authority[(lastColon + 1)..], out var p))
                port = p;
        }

        string? user = null, pass = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var i = uri.UserInfo.IndexOf(':');
            if (i >= 0)
            {
                user = Uri.UnescapeDataString(uri.UserInfo[..i]);
                pass = Uri.UnescapeDataString(uri.UserInfo[(i + 1)..]);
            }
            else
            {
                user = Uri.UnescapeDataString(uri.UserInfo);
            }
        }

        result = new ProxyUrl
        {
            Scheme   = uri.Scheme.ToLowerInvariant(),
            Host     = uri.Host,
            Port     = port,
            Username = user,
            Password = pass,
        };
        return result.IsValid;
    }

    /// <summary>Compact "host:port" line for table cells.</summary>
    public string HostPort =>
        IsValid ? $"{Host}:{Port}" : "—";
}
