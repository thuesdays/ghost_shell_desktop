// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 57 — persists the last-known nonce per (chain, address). With
/// popup-signing the wallet manages the real nonce; this store is for
/// diagnostics and for any future raw-tx flow. Keyed case-insensitively on a
/// lower-cased address so "0xABC" and "0xabc" share one slot.
/// </summary>
public interface INonceTracker
{
    /// <summary>Last persisted nonce for (chain, address), or 0 if none.</summary>
    Task<long> PeekAsync(string chainId, string address, CancellationToken ct = default);

    /// <summary>Persist an explicit nonce.</summary>
    Task SetAsync(string chainId, string address, long nonce, CancellationToken ct = default);

    /// <summary>Increment the stored nonce by one and return the new value.</summary>
    Task<long> BumpAsync(string chainId, string address, CancellationToken ct = default);
}
