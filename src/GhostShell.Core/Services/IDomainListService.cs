// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 34 — three-list domain registry (my / target / block) used by
/// the Domains page and the script engine.
///
/// All domains are normalised on write (lowercase, trim trailing
/// punctuation, optional <c>www.</c> drop). Membership checks should
/// use <see cref="IsMatchAsync"/> rather than direct equality so a
/// list entry of <c>example.com</c> matches both <c>example.com</c>
/// and <c>checkout.example.com</c> — same suffix-match semantics the
/// legacy web's runner uses.
/// </summary>
public interface IDomainListService
{
    /// <summary>List all entries for one kind, ordered alphabetically.</summary>
    Task<IReadOnlyList<DomainListEntry>> ListAsync(DomainListKind kind, CancellationToken ct = default);

    /// <summary>List all entries across all kinds (for the script
    /// engine — fewer DB round-trips on every step).</summary>
    Task<IReadOnlyList<DomainListEntry>> ListAllAsync(CancellationToken ct = default);

    /// <summary>Replace the list for one kind with the given domains.
    /// Domains are normalised + de-duplicated; empty lines and lines
    /// starting with <c>#</c> are dropped (the textarea UI uses these
    /// as comments).</summary>
    Task ReplaceAsync(DomainListKind kind, IEnumerable<string> domains, CancellationToken ct = default);

    /// <summary>Append a single domain to one kind. No-op if it
    /// already exists. Returns <c>true</c> when a new row was added.</summary>
    Task<bool> AddAsync(DomainListKind kind, string domain, string? note = null, CancellationToken ct = default);

    /// <summary>Remove a single domain. Returns <c>true</c> when a
    /// row was actually removed.</summary>
    Task<bool> RemoveAsync(DomainListKind kind, string domain, CancellationToken ct = default);

    /// <summary>Suffix-match the given <paramref name="adDomain"/>
    /// against the list of <paramref name="kind"/>. Returns the
    /// matching list entry, or null. Matches both exact equality and
    /// any-subdomain (<c>foo.example.com</c> matches list entry
    /// <c>example.com</c>).</summary>
    Task<DomainListEntry?> IsMatchAsync(DomainListKind kind, string? adDomain, CancellationToken ct = default);

    /// <summary>Normalise a domain string the same way the runner
    /// does on read. Public so the UI can show the user what their
    /// raw paste will become before they save.</summary>
    static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().ToLowerInvariant();
        // Strip protocol if pasted from a URL.
        var i = s.IndexOf("://", StringComparison.Ordinal);
        if (i > 0) s = s[(i + 3)..];
        // Strip path/query/fragment.
        i = s.IndexOfAny(new[] { '/', '?', '#' });
        if (i > 0) s = s[..i];
        // Strip leading "www.".
        if (s.StartsWith("www.")) s = s[4..];
        // Strip port.
        i = s.IndexOf(':');
        if (i > 0) s = s[..i];
        // Strip trailing dot.
        return s.TrimEnd('.');
    }
}
