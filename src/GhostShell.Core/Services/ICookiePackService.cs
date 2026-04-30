// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// CRUD for portable cookie packs. Mirrors the legacy Python
/// <c>session/cookie_pack.py</c> + <c>db</c> surface; the marketplace
/// half of that module is intentionally not ported (per spec).
///
/// Packs are stored gzipped in <c>cookie_packs.payload_gz</c>; the
/// service handles compression transparently. The API exposes the
/// payload as a strongly-typed <see cref="SessionPayload"/> rather
/// than raw JSON to keep callers honest about validation.
///
/// Importing the same slug twice overwrites the existing row
/// (UPSERT) — same behaviour as legacy. <see cref="ApplyAsync"/>
/// is the runtime hook: takes a live <see cref="IBrowserSession"/>,
/// pushes the pack's cookies + storage into it.
/// </summary>
public interface ICookiePackService
{
    /// <summary>List all packs, ordered by recently-imported first.</summary>
    Task<IReadOnlyList<CookiePack>> ListAsync(CancellationToken ct = default);

    Task<CookiePack?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Get the full unpacked payload for a pack.</summary>
    Task<SessionPayload?> GetPayloadAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Create or replace a pack by <see cref="CookiePack.Slug"/>.
    /// The supplied <paramref name="meta"/> provides the labels /
    /// metadata; <paramref name="payload"/> is the actual cookies +
    /// storage that get gzipped and stored.
    /// </summary>
    Task<long> UpsertAsync(
        CookiePack meta, SessionPayload payload,
        CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Push a pack's cookies + storage into a live browser session.
    /// Returns counts so the UI can render "Applied 247 cookies,
    /// 4 storage origins". Cookies replace existing ones with the
    /// same (name, domain, path); storage is overlaid additively.
    /// </summary>
    Task<ApplyResult> ApplyAsync(
        long packId, IBrowserSession session,
        CancellationToken ct = default);

    /// <summary>
    /// Capture the current state of a live session into a fresh
    /// pack. The user-supplied <paramref name="slug"/> /
    /// <paramref name="label"/> identify it; domains list is
    /// derived from the captured cookies.
    /// </summary>
    Task<long> ExportFromSessionAsync(
        string slug, string label,
        IBrowserSession session,
        CancellationToken ct = default);
}

public sealed record ApplyResult(int CookiesSet, int StorageOriginsSet);
