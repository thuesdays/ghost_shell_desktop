// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

public interface IProxyService
{
    Task<IReadOnlyList<Proxy>> ListAsync(CancellationToken ct = default);
    Task<Proxy?> GetAsync(string slug, CancellationToken ct = default);
    Task<Proxy?> GetByUrlAsync(string url, CancellationToken ct = default);
    Task<Proxy>  CreateAsync(Proxy proxy, CancellationToken ct = default);

    /// <summary>
    /// Insert many proxies in one transaction. Skips entries whose
    /// URL already exists. Returns the (created, skipped) split so
    /// the bulk-import dialog can report results.
    /// </summary>
    Task<BulkCreateResult> BulkCreateAsync(
        IReadOnlyList<Proxy> proxies, CancellationToken ct = default);

    Task UpdateAsync(Proxy proxy, CancellationToken ct = default);
    Task DeleteAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// Persist last-test diagnostics for a proxy (latency, country,
    /// IP type, health) without touching its config fields.
    /// </summary>
    Task RecordTestResultAsync(string slug, ProxyTestResult result, CancellationToken ct = default);
}

public sealed record BulkCreateResult(
    IReadOnlyList<Proxy> Created,
    IReadOnlyList<string> Skipped);
