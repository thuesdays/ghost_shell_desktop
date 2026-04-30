// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Single browser cookie. Shape matches Selenium 4's
/// <c>OpenQA.Selenium.Cookie</c> with the extra fields Chromium's
/// CDP <c>Network.setCookie</c> expects (sameSite, expires).
///
/// Persisted to disk and over the wire as JSON; round-trips via
/// <see cref="System.Text.Json"/> with the camelCase property
/// resolver. Field names mirror the legacy Python tree
/// (<c>session/manager.py</c> + <c>session/cookie_pack.py</c>) so
/// pack files exported from either project can be imported by the
/// other.
/// </summary>
public sealed record CookieEntry
{
    public required string Name   { get; init; }
    public required string Value  { get; init; }
    public required string Domain { get; init; }
    public string Path             { get; init; } = "/";
    public bool   Secure           { get; init; }
    public bool   HttpOnly         { get; init; }
    /// <summary>One of "Strict", "Lax", "None", or null/empty.</summary>
    public string? SameSite        { get; init; }
    /// <summary>Unix epoch seconds (UTC). Null = session cookie.</summary>
    public long? ExpiresUnixSec    { get; init; }
}
