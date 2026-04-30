// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Display-friendly entry for the Proxy combo in
/// <see cref="ProfileEditorDialog"/>. Wraps a real Proxy slug with
/// a precomputed two-line label so the combo template can stay
/// dumb (no value converters, no service lookups during render).
///
/// One option in the list is the synthetic "(none)" entry used to
/// clear the binding — flagged via <see cref="IsNone"/>.
/// </summary>
public sealed class ProxyOption
{
    /// <summary>Stable id stored in <c>profiles.proxy_slug</c>. Empty string for the "(none)" entry.</summary>
    public string Slug { get; init; } = "";

    /// <summary>Top line — what the user reads first.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Bottom line — country / IP type / latency. May be empty.</summary>
    public string SubLine { get; init; } = "";

    public bool IsNone { get; init; }

    public static ProxyOption None() => new()
    {
        DisplayName = "(none)",
        SubLine     = "Profile launches without a proxy",
        IsNone      = true,
    };

    public static ProxyOption FromProxy(Proxy p)
    {
        // Display: explicit Name wins; otherwise parse URL into "host:port"
        // for a recognizable string. Falls back to raw URL if parse fails.
        string display;
        if (!string.IsNullOrWhiteSpace(p.Name))
            display = p.Name!;
        else if (ProxyUrl.TryParse(p.Url, out var pu) && pu.IsValid)
            display = pu.HostPort;
        else
            display = p.Url;

        // Sub-line: collect what we know about the proxy's diagnostics.
        var bits = new List<string>(4);
        if (!string.IsNullOrEmpty(p.Country))    bits.Add(p.Country!);
        if (p.IpType != IpType.Unknown)          bits.Add(p.IpType.ToString());
        if (p.LatencyMs is > 0)                  bits.Add($"{p.LatencyMs} ms");
        if (p.IsDefault)                         bits.Add("default");

        return new ProxyOption
        {
            Slug        = p.Slug,
            DisplayName = display,
            SubLine     = bits.Count == 0 ? "untested" : string.Join(" · ", bits),
        };
    }
}
