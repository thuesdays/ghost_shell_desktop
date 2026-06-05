// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Numerics;

namespace GhostShell.Core.Chains;

/// <summary>
/// Phase 57 — pure unit math for chain amounts. Balances come back as integer
/// base units (wei / lamports) in hex (EVM) or decimal (Solana); these helpers
/// parse them and render a human amount without losing precision (BigInteger
/// throughout, decimal only at the formatting boundary). No I/O — fully tested.
/// </summary>
public static class ChainUnits
{
    /// <summary>Parse a hex quantity ("0x1a", with or without prefix) to a
    /// non-negative BigInteger. Returns 0 for null/empty/garbage.</summary>
    public static BigInteger ParseHexQuantity(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return BigInteger.Zero;
        var s = hex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 0) return BigInteger.Zero;
        // Prepend "0" so a leading hex digit ≥ 8 isn't read as negative.
        if (!BigInteger.TryParse("0" + s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return BigInteger.Zero;
        return v;
    }

    /// <summary>Convert integer base units to a decimal token amount.
    /// (e.g. 1_500_000_000_000_000_000 wei, 18 → 1.5). For DISPLAY-range
    /// values only — use <see cref="FormatBalance"/> for arbitrary magnitudes
    /// (it stays in BigInteger and never overflows decimal).</summary>
    public static decimal ToTokens(BigInteger baseUnits, int decimals)
        => decimal.Parse(FormatBalance(baseUnits, decimals, Math.Clamp(decimals, 0, 18)),
                         NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Render a balance with up to <paramref name="maxDecimals"/>
    /// fractional digits, trimming trailing zeros (e.g. "1.5", "0.0034", "0").
    /// Pure BigInteger string math — safe for whale-sized balances that would
    /// overflow <see cref="decimal"/>.</summary>
    public static string FormatBalance(BigInteger baseUnits, int decimals, int maxDecimals = 6)
    {
        if (baseUnits.IsZero) return "0";
        var neg = baseUnits.Sign < 0;
        var abs = BigInteger.Abs(baseUnits);

        string whole, frac = "";
        if (decimals <= 0)
        {
            whole = abs.ToString();
        }
        else
        {
            var divisor = BigInteger.Pow(10, decimals);
            whole = (abs / divisor).ToString();
            frac  = (abs % divisor).ToString().PadLeft(decimals, '0');
            var keep = Math.Clamp(maxDecimals, 0, decimals);
            frac = keep == 0 ? "" : frac[..keep].TrimEnd('0');
        }
        var s = frac.Length > 0 ? $"{whole}.{frac}" : whole;
        return neg ? "-" + s : s;
    }

    /// <summary>Wei → gwei as a decimal (1 gwei = 1e9 wei). BigInteger split so a
    /// garbage huge value can't overflow the decimal cast.</summary>
    public static decimal WeiToGwei(BigInteger wei)
    {
        const long g = 1_000_000_000;
        var whole = wei / g;
        var rem   = wei - whole * g;
        if (BigInteger.Abs(whole) > new BigInteger(decimal.MaxValue / 1m))
            return wei.Sign < 0 ? decimal.MinValue : decimal.MaxValue;
        return (decimal)whole + (decimal)rem / g;
    }

    /// <summary>Parse a human token amount ("0.05") into base units. Pure
    /// string/BigInteger math (no decimal overflow). Throws on bad input so a
    /// script's <c>assert_balance min</c> fails loudly, not silently.</summary>
    public static BigInteger TokensToBaseUnits(string amount, int decimals)
    {
        var s = (amount ?? "").Trim();
        if (s.Length == 0 || s.StartsWith('-'))
            throw new FormatException($"invalid token amount '{amount}'");
        if (s.StartsWith('+')) s = s[1..];

        var dot = s.IndexOf('.');
        var intPart  = dot < 0 ? s : s[..dot];
        var fracPart = dot < 0 ? "" : s[(dot + 1)..];
        if (intPart.Length == 0) intPart = "0";
        if (!intPart.All(char.IsDigit) || !fracPart.All(char.IsDigit))
            throw new FormatException($"invalid token amount '{amount}'");

        var d = Math.Max(0, decimals);
        fracPart = fracPart.Length > d ? fracPart[..d] : fracPart.PadRight(d, '0');

        var baseUnits = BigInteger.Parse(intPart) * BigInteger.Pow(10, d);
        if (fracPart.Length > 0) baseUnits += BigInteger.Parse(fracPart);
        return baseUnits;
    }
}
