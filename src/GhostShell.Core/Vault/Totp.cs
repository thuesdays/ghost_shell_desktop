// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Security.Cryptography;

namespace GhostShell.Core.Vault;

/// <summary>
/// Phase 24 — RFC 6238 TOTP (HMAC-SHA1, 30 s window, 6 digits) for
/// vault entries that store a base32-encoded shared secret. Pure
/// stdlib, no external dependencies — same algorithm Google
/// Authenticator / Authy / etc. use.
///
/// Returns ("000000", 30) on a malformed or empty secret rather than
/// throwing — UI displays it as "—" so a busted entry doesn't take
/// down the page.
/// </summary>
public static class Totp
{
    public const int CodeDigits        = 6;
    public const int WindowSeconds     = 30;

    /// <summary>Compute the current 6-digit code + seconds remaining
    /// until the next window rolls over.</summary>
    public static (string code, int remaining) Compute(string base32Secret)
    {
        if (string.IsNullOrWhiteSpace(base32Secret)) return ("000000", WindowSeconds);
        byte[] key;
        try { key = Base32Decode(base32Secret); }
        catch { return ("000000", WindowSeconds); }
        if (key.Length == 0) return ("000000", WindowSeconds);

        var unixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var counter = unixSec / WindowSeconds;
        var remaining = (int)(WindowSeconds - (unixSec % WindowSeconds));

        // Counter is encoded as big-endian 8 bytes per RFC 4226.
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0F;
        var binCode =
            ((hash[offset]     & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) <<  8) |
             (hash[offset + 3] & 0xFF);
        var truncated = binCode % (int)Math.Pow(10, CodeDigits);
        var code = truncated.ToString("D" + CodeDigits);
        return (code, remaining);
    }

    /// <summary>RFC 4648 base32 decoder (uppercase letters + 2..7).
    /// Tolerates lowercase, spaces, and padding (=). Throws on any
    /// other character.</summary>
    public static byte[] Base32Decode(string input)
    {
        if (string.IsNullOrEmpty(input)) return Array.Empty<byte>();
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = input.Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        var buffer = 0;
        var bits   = 0;
        foreach (var c in clean)
        {
            var idx = Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException("invalid base32 character: " + c);
            buffer = (buffer << 5) | idx;
            bits  += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }
        return output.ToArray();
    }
}
