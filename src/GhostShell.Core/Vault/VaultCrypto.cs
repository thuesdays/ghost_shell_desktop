// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Security.Cryptography;
using System.Text;

namespace GhostShell.Core.Vault;

/// <summary>
/// Phase 24 — crypto primitives for the credential vault.
///
/// Two-stage scheme that mirrors the web port:
///   • <see cref="DeriveKey"/> turns a passphrase + 16-byte salt into a
///     32-byte AES key via PBKDF2-HMAC-SHA256 with 600 000 iterations
///     (OWASP 2023 baseline — bumped from 200k in the Phase 24 audit).
///   • <see cref="Encrypt"/> / <see cref="Decrypt"/> wrap a payload
///     with AES-GCM. Each call generates a fresh 12-byte random nonce;
///     the on-disk format is <c>[nonce 12B][ciphertext N][tag 16B]</c>.
///
/// AES-GCM gives us authenticated encryption — the <see cref="Decrypt"/>
/// path throws <see cref="CryptographicException"/> on tampering or
/// when the key is wrong. The vault uses that to validate unlock
/// attempts (decrypt the verifier string with the candidate key →
/// success ⇒ password is correct).
/// </summary>
public static class VaultCrypto
{
    public const int KeySizeBytes      = 32; // AES-256
    public const int NonceSizeBytes    = 12; // AES-GCM standard
    public const int TagSizeBytes      = 16; // AES-GCM auth tag
    public const int SaltSizeBytes     = 16;
    /// <summary>OWASP 2023 baseline for PBKDF2-HMAC-SHA256 — 600k.
    /// Used for vaults initialised at/after the Phase 24 audit, and
    /// the target count we re-wrap legacy vaults to on unlock.
    ///
    /// audit VAULT-02: this is the *current* count for NEW vaults, but
    /// it is no longer the only count we can derive at — see
    /// <see cref="LegacyPbkdf2Iterations"/> and the 3-arg
    /// <see cref="DeriveKey(string,byte[],int)"/> overload. The number
    /// of iterations a given vault actually used must be persisted
    /// (vault_config.kdf_iter) and fed back into DeriveKey, otherwise a
    /// vault written at 200k can never be unlocked at 600k.</summary>
    public const int Pbkdf2Iterations  = 600_000;

    /// <summary>audit VAULT-02: PBKDF2 iteration count used by the
    /// pre-Phase-24 ("200k") builds. Vaults created before the bump
    /// stored NO kdf_iter, so callers must treat "kdf_iter absent" as
    /// this value and derive with it — bumping the compile-time
    /// constant alone permanently bricks those vaults (the verifier
    /// GCM tag never matches a 600k key). Kept as a named constant so
    /// the unlock/migration path can fall back to it explicitly.</summary>
    public const int LegacyPbkdf2Iterations = 200_000;

    /// <summary>audit VAULT-02: the iteration count to assume when a
    /// vault has no persisted kdf_iter. MUST be the legacy count so
    /// existing on-disk vaults keep opening; new vaults persist
    /// <see cref="Pbkdf2Iterations"/> explicitly and so never rely on
    /// this default.</summary>
    public static int ResolveIterations(int? storedIterations)
        => storedIterations is int n && n > 0 ? n : LegacyPbkdf2Iterations;

    /// <summary>Plaintext that gets encrypted on initialise() and re-
    /// decrypted on unlock(). Successful decryption == password match.
    /// Phase 24 audit kept this as a stable string — the nonce per
    /// encryption already randomises the ciphertext, so a known
    /// plaintext doesn't help an offline attacker beyond what the
    /// AES-GCM tag already provides.</summary>
    public const string VerifierPlain  = "ghost_shell_vault_v1_ok";

    /// <summary>Generate a fresh cryptographically-strong salt.</summary>
    public static byte[] NewSalt()
    {
        var s = new byte[SaltSizeBytes];
        RandomNumberGenerator.Fill(s);
        return s;
    }

    /// <summary>Derive the 32-byte AES key from the user's passphrase.
    /// Phase 24 audit fix — wipes the intermediate UTF-8 byte buffer
    /// so the passphrase doesn't linger in the managed heap any longer
    /// than the KDF computation needs it.
    ///
    /// audit VAULT-02: this 2-arg form keeps the original signature
    /// (callers depend on it) but no longer hard-codes 600k. It now
    /// derives at <see cref="LegacyPbkdf2Iterations"/> (200k) — the
    /// count every pre-bump on-disk vault was actually written with —
    /// so existing vaults keep unlocking. New vaults and re-wrapped
    /// vaults MUST call the 3-arg overload with
    /// <see cref="Pbkdf2Iterations"/> and persist that count
    /// (vault_config.kdf_iter) so a later derive can reproduce the key.
    /// See <see cref="DeriveKey(string,byte[],int)"/>.</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt)
        => DeriveKey(passphrase, salt, LegacyPbkdf2Iterations);

    /// <summary>audit VAULT-02: iteration-count-aware derive. The count
    /// is a per-vault parameter (persisted as vault_config.kdf_iter)
    /// rather than a compile-time constant, so a vault created at 200k
    /// and a vault created at 600k both reproduce their original key.
    /// Use <see cref="ResolveIterations"/> to map an absent stored
    /// count to the legacy default before calling this.</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        if (salt is null || salt.Length == 0)
            throw new ArgumentException("salt is required", nameof(salt));
        if (iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(iterations), "iterations must be positive");
        var phraseBytes = Encoding.UTF8.GetBytes(passphrase ?? "");
        try
        {
            using var kdf = new Rfc2898DeriveBytes(
                phraseBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA256);
            return kdf.GetBytes(KeySizeBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(phraseBytes);
        }
    }

    /// <summary>
    /// Encrypt the payload with AES-GCM. Returns a single byte array
    /// that combines nonce + ciphertext + tag. Caller stores it
    /// verbatim (in vault_items.secrets_enc or the verifier slot).
    /// </summary>
    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (key.Length != KeySizeBytes)
            throw new ArgumentException($"key must be {KeySizeBytes} bytes", nameof(key));

        var nonce      = new byte[NonceSizeBytes];
        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[TagSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        Buffer.BlockCopy(nonce,      0, output, 0,                                 NonceSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSizeBytes,                    ciphertext.Length);
        Buffer.BlockCopy(tag,        0, output, NonceSizeBytes + ciphertext.Length, TagSizeBytes);
        return output;
    }

    /// <summary>
    /// Decrypt a blob produced by <see cref="Encrypt"/>. Throws
    /// <see cref="CryptographicException"/> on a wrong key or
    /// tampered ciphertext (AES-GCM auth tag mismatch).
    /// </summary>
    public static byte[] Decrypt(byte[] key, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(blob);
        if (key.Length != KeySizeBytes)
            throw new ArgumentException($"key must be {KeySizeBytes} bytes", nameof(key));
        if (blob.Length < NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("vault blob too short");

        var nonce      = new byte[NonceSizeBytes];
        var tag        = new byte[TagSizeBytes];
        var ctLen      = blob.Length - NonceSizeBytes - TagSizeBytes;
        var ciphertext = new byte[ctLen];
        var plaintext  = new byte[ctLen];

        Buffer.BlockCopy(blob, 0,                                       nonce,      0, NonceSizeBytes);
        Buffer.BlockCopy(blob, NonceSizeBytes,                          ciphertext, 0, ctLen);
        Buffer.BlockCopy(blob, NonceSizeBytes + ctLen,                  tag,        0, TagSizeBytes);

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>Convenience for encrypting a UTF-8 string payload.
    /// Phase 26 audit fix — wipes the temporary UTF-8 byte buffer holding
    /// the cleartext after handing it to <see cref="Encrypt"/>, so the
    /// only place plaintext lives in memory after the call returns is
    /// the GC-managed string the CALLER passed in (which is unavoidable
    /// in .NET — strings are immutable + interned).</summary>
    public static byte[] EncryptString(byte[] key, string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext ?? "");
        try { return Encrypt(key, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Convenience for decrypting back to a UTF-8 string.
    /// Phase 26 audit fix — wipes the intermediate plaintext byte
    /// buffer after the string conversion. The returned string itself
    /// can't be wiped (CLR string immutability) but the byte array we
    /// owned can be.</summary>
    public static string DecryptString(byte[] key, byte[] blob)
    {
        var plain = Decrypt(key, blob);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
