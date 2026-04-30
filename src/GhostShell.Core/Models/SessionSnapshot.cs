// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// One row in <c>cookie_snapshots</c>. Lightweight metadata used by
/// the Sessions list page; the actual cookie/storage payload lives
/// in <see cref="SessionPayload"/> and is loaded lazily.
///
/// <see cref="Trigger"/> is one of:
///   • <c>manual</c>           — user clicked "Save snapshot"
///   • <c>auto_clean_run</c>   — auto-saved after a clean shutdown
///                               (exit_code = 0, no captcha)
///   • <c>auto_warmup</c>      — saved after a successful warmup
///                               (Phase 5)
/// </summary>
public sealed class SessionSnapshot
{
    public long Id { get; init; }
    public required string ProfileName { get; init; }
    public DateTime CreatedAt { get; init; }
    public long? RunId { get; init; }
    public string? Trigger { get; init; }
    public int CookieCount { get; init; }
    public int DomainCount { get; init; }
    public int Bytes { get; init; }
    public string? Reason { get; init; }
}
