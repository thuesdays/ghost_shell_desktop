// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Services;

namespace GhostShell.Data.Services;

/// <summary>
/// Phase 57 — KV-backed nonce store on top of <see cref="ISettingsService"/>.
/// Key shape: <c>nonce:&lt;chainId&gt;:&lt;address-lowercased&gt;</c>.
/// </summary>
public sealed class NonceTracker : INonceTracker
{
    private readonly ISettingsService _settings;
    public NonceTracker(ISettingsService settings) => _settings = settings;

    private static string Key(string chainId, string address)
        => $"nonce:{(chainId ?? "").ToLowerInvariant()}:{(address ?? "").Trim().ToLowerInvariant()}";

    public async Task<long> PeekAsync(string chainId, string address, CancellationToken ct = default)
    {
        var raw = await _settings.GetStringAsync(Key(chainId, address), ct);
        return long.TryParse(raw, out var v) && v >= 0 ? v : 0;
    }

    public Task SetAsync(string chainId, string address, long nonce, CancellationToken ct = default)
        => _settings.SetStringAsync(Key(chainId, address), Math.Max(0, nonce).ToString(), ct);

    public async Task<long> BumpAsync(string chainId, string address, CancellationToken ct = default)
    {
        var next = await PeekAsync(chainId, address, ct) + 1;
        await SetAsync(chainId, address, next, ct);
        return next;
    }
}
