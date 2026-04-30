// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Single point on the Proxy Health Timeline. Captures meaningful
/// transitions in a proxy's lifecycle: when it was first seen, when
/// it rotated, when a captcha was triggered while routing through
/// it, when the proxy got "burned" (IP-blocked by a target).
///
/// Mirrors the timeline data used by the legacy web project's
/// `proxy/health-timeline` endpoint so we can mass-import history
/// later without translation.
/// </summary>
public sealed class ProxyHealthEvent
{
    public long Id { get; init; }

    /// <summary>Foreign key to <see cref="Proxy.Slug"/>.</summary>
    public required string ProxySlug { get; init; }

    public ProxyHealthEventKind Kind { get; init; }

    public DateTime At { get; init; }

    /// <summary>Optional context — IP at the moment, error, etc.</summary>
    public string? Detail { get; init; }
}

public enum ProxyHealthEventKind
{
    /// <summary>First time the proxy was successfully reached.</summary>
    FirstSeen,
    /// <summary>Manual or scheduled rotation cycle.</summary>
    Rotation,
    /// <summary>A captcha was triggered while this proxy was the route.</summary>
    Captcha,
    /// <summary>Burn — target rejected the IP entirely.</summary>
    Burn,
}
