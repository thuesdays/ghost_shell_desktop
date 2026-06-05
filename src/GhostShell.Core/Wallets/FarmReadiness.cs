// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Wallets;

/// <summary>Per-member crypto readiness inside a farm (group).</summary>
public sealed record FarmMemberStatus
{
    public required string Name { get; init; }
    /// <summary>True when the member has a bound wallet AND a stored password
    /// (everything <c>wallet_unlock</c> needs to run unattended).</summary>
    public required bool Ready { get; init; }
    /// <summary>Address shortened for display, e.g. <c>0x1234…aBcD</c>. Empty when none.</summary>
    public string AddressMasked { get; init; } = "";
    /// <summary>Full address — shown on hover; never a secret.</summary>
    public string AddressFull { get; init; } = "";
    /// <summary>Why the member is not ready (empty when ready).</summary>
    public string Reason { get; init; } = "";
    public bool HasWallet { get; init; }
    public bool HasPassword { get; init; }
}

/// <summary>Roll-up across a farm's members.</summary>
public sealed record FarmSummary
{
    public int Total { get; init; }
    public int Ready { get; init; }
    public int NeedsWallet { get; init; }
    public int NeedsPassword { get; init; }

    public string Label =>
        Total == 0 ? "no members"
        : $"{Ready}/{Total} ready"
          + (NeedsWallet > 0 ? $" · {NeedsWallet} no wallet" : "")
          + (NeedsPassword > 0 ? $" · {NeedsPassword} no password" : "");
}

/// <summary>
/// Phase 56 — pure readiness logic for the crypto-farm panel. No I/O: the
/// caller supplies, per member, whether a <c>crypto_wallet</c> vault item
/// exists, whether it carries a password, and the (non-secret) address. Kept
/// pure so it is exhaustively unit-testable.
/// </summary>
public static class FarmReadiness
{
    /// <summary>Shorten an address to <c>head…tail</c>. EVM (0x + 40 hex) →
    /// 6+4; everything longer than 10 chars → 6+4; short/empty → as-is.</summary>
    public static string MaskAddress(string? address)
    {
        var a = address?.Trim() ?? "";
        if (a.Length <= 12) return a;
        return $"{a[..6]}…{a[^4..]}";
    }

    /// <summary>Evaluate one member.</summary>
    public static FarmMemberStatus Evaluate(string name, bool hasWallet, bool hasPassword, string? address)
    {
        var addr = address?.Trim() ?? "";
        string reason;
        bool ready;
        if (!hasWallet)
        {
            ready = false; reason = "no wallet bound";
        }
        else if (!hasPassword)
        {
            ready = false; reason = "no wallet password";
        }
        else
        {
            ready = true; reason = "";
        }

        return new FarmMemberStatus
        {
            Name = name,
            Ready = ready,
            Reason = reason,
            HasWallet = hasWallet,
            HasPassword = hasPassword,
            AddressFull = addr,
            AddressMasked = MaskAddress(addr),
        };
    }

    /// <summary>Roll member statuses into a farm summary.</summary>
    public static FarmSummary Summarize(IEnumerable<FarmMemberStatus> members)
    {
        int total = 0, ready = 0, needsWallet = 0, needsPassword = 0;
        foreach (var m in members)
        {
            total++;
            if (m.Ready) ready++;
            else if (!m.HasWallet) needsWallet++;
            else if (!m.HasPassword) needsPassword++;
        }
        return new FarmSummary
        {
            Total = total,
            Ready = ready,
            NeedsWallet = needsWallet,
            NeedsPassword = needsPassword,
        };
    }
}
