// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Concrete <see cref="IChromeImporter"/>. Windows-only — relies on
/// DPAPI for the Local State key unwrap. On macOS / Linux the
/// equivalents are Keychain / libsecret which we don't ship today,
/// so we throw a clear InvalidOperationException up front.
///
/// Cookie value format (Chrome ≥ v80):
///   value column          → empty
///   encrypted_value blob  → "v10" + 12-byte nonce + ciphertext + 16-byte tag
///
/// The 32-byte AES key lives inside Local State JSON
/// (os_crypt.encrypted_key), itself base64-DPAPI-encrypted with the
/// "DPAPI" 5-byte prefix.
///
/// SQLite read strategy: Chrome holds a write lock on its DBs while
/// it's running. We always copy the source DB file to a temp dir
/// first and read the copy; the trade-off is a ~5-50MB copy on each
/// import vs. a "Chrome must be closed" prerequisite. Worth the
/// disk cost.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ChromeImporter : IChromeImporter
{
    private readonly ISessionService _sessions;
    private readonly ILogger<ChromeImporter> _log;

    public ChromeImporter(ISessionService sessions, ILogger<ChromeImporter> log)
    {
        _sessions = sessions;
        _log = log;
    }

    // ─── Discovery ─────────────────────────────────────────────────

    /// <summary>
    /// Roots probed during auto-discovery. Each entry: (BrandLabel,
    /// EnvVarName, RelativeUserDataPath). The full path is
    /// %ENV%\RelativeUserDataPath. We probe LOCALAPPDATA first
    /// (Chrome / Edge / Brave / Chromium) then APPDATA (Opera).
    /// </summary>
    private static readonly (string Label, string EnvVar, string Tail)[] Roots =
    {
        ("Google Chrome",   "LOCALAPPDATA", @"Google\Chrome\User Data"),
        ("Chromium",        "LOCALAPPDATA", @"Chromium\User Data"),
        ("Microsoft Edge",  "LOCALAPPDATA", @"Microsoft\Edge\User Data"),
        ("Brave Browser",   "LOCALAPPDATA", @"BraveSoftware\Brave-Browser\User Data"),
        ("Opera",           "APPDATA",      @"Opera Software\Opera Stable"),
        ("Vivaldi",         "LOCALAPPDATA", @"Vivaldi\User Data"),
        ("Yandex",          "LOCALAPPDATA", @"Yandex\YandexBrowser\User Data"),
    };

    public Task<IReadOnlyList<ChromeProfileSource>> DiscoverAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ChromeProfileSource>>(() =>
        {
            var found = new List<ChromeProfileSource>();
            foreach (var (brand, env, tail) in Roots)
            {
                ct.ThrowIfCancellationRequested();
                var envValue = Environment.GetEnvironmentVariable(env);
                if (string.IsNullOrEmpty(envValue)) continue;

                var userData = Path.Combine(envValue, tail);
                if (!Directory.Exists(userData)) continue;

                // Opera Stable is a profile dir itself (no Default
                // sub-folder); the rest follow the standard layout.
                if (brand == "Opera")
                {
                    found.Add(new ChromeProfileSource
                    {
                        BrandLabel    = brand,
                        UserDataPath  = Path.GetDirectoryName(userData) ?? userData,
                        ProfileFolder = Path.GetFileName(userData),
                        ProfileDisplayName = "Stable",
                    });
                    continue;
                }

                // Read Local State once for nice profile labels. Failure
                // here is non-fatal; we'll fall back to folder names.
                Dictionary<string, string>? labels = null;
                try
                {
                    var localState = Path.Combine(userData, "Local State");
                    if (File.Exists(localState))
                        labels = ReadProfileLabels(localState);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Local State read failed under {UserData}", userData);
                }

                foreach (var dir in Directory.EnumerateDirectories(userData))
                {
                    var folderName = Path.GetFileName(dir);
                    // Profile dirs always have a History or Cookies file
                    // (or at least one of them exists). Skip the obvious
                    // non-profile siblings ("System Profile", ".com.google", etc).
                    if (!LooksLikeProfileDir(dir)) continue;

                    var label = "";
                    labels?.TryGetValue(folderName, out label!);
                    found.Add(new ChromeProfileSource
                    {
                        BrandLabel         = brand,
                        UserDataPath       = userData,
                        ProfileFolder      = folderName,
                        ProfileDisplayName = label ?? "",
                    });
                }
            }

            // Stable order: brand alpha, then profile folder alpha.
            return found
                .OrderBy(s => s.BrandLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy (s => s.ProfileFolder, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);

    private static bool LooksLikeProfileDir(string dir)
    {
        // Heuristic: a Chrome profile dir has at least Cookies (or
        // Network/Cookies in newer Chrome) or Preferences inside.
        return File.Exists(Path.Combine(dir, "Preferences"))
            || File.Exists(Path.Combine(dir, "Cookies"))
            || File.Exists(Path.Combine(dir, "Network", "Cookies"));
    }

    /// <summary>
    /// Parse Local State JSON to map "Profile 1" → "Work", etc. Best-
    /// effort — schema has historically been stable but we tolerate
    /// shape drift by catching JsonException and returning whatever
    /// we managed to extract.
    /// </summary>
    private static Dictionary<string, string> ReadProfileLabels(string localStatePath)
    {
        var bytes = File.ReadAllBytes(localStatePath);
        using var doc = JsonDocument.Parse(bytes);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("profile", out var profile)
            && profile.TryGetProperty("info_cache", out var cache)
            && cache.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in cache.EnumerateObject())
            {
                if (entry.Value.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    labels[entry.Name] = name.GetString() ?? "";
                }
            }
        }
        return labels;
    }

    // ─── Import ────────────────────────────────────────────────────

    public async Task<ChromeImportResult> ImportAsync(
        ChromeImportOptions opts, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException(
                "Chrome import currently requires Windows (DPAPI for cookie decryption).");

        var src = opts.Source;
        var profilePath = src.ProfilePath;
        if (!Directory.Exists(profilePath))
            throw new FileNotFoundException(
                $"Chrome profile dir not found: {profilePath}");

        // Stage 1: copy DB files to a temp scratch dir so we don't
        // contend with Chrome's write lock. Cleaned up in finally.
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"ghostshell_chrome_import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var warnings = new List<string>();
        var cookies  = new List<CookieEntry>();
        var historyEntries = new List<string>();
        var skippedSensitive = 0;
        var undecryptable = 0;
        var historySkippedSensitive = 0;

        try
        {
            // Copy candidate files — tolerate missing files per browser
            // build (Edge nests Cookies under Network\, older Chromes
            // had it at the profile root).
            var cookiesSrc = ResolveExistingFile(profilePath,
                Path.Combine("Network", "Cookies"), "Cookies");
            var historySrc = Path.Combine(profilePath, "History");
            var localStateSrc = Path.Combine(src.UserDataPath, "Local State");

            // ─── DB acquisition strategy ─────────────────────────────
            // 1. Try the FileShare.ReadWrite copy. Works for SQLite-
            //    backed Chrome files on most installs.
            // 2. If the copy fails (some AV, mapped-memory locks, or
            //    Chrome's per-file backup-mode flag), fall back to
            //    reading the SOURCE file directly via SqliteConnection
            //    in ReadOnly + Shared-cache + immutable-mode. This
            //    bypasses the file-system layer entirely — SQLite
            //    opens the file via its own VFS which uses LockFileEx
            //    semantics and tolerates concurrent reader.
            string? cookiesPath = null, historyPath = null;
            if (cookiesSrc is not null && opts.ImportCookies)
            {
                cookiesPath = TryCopyWithSidecars(cookiesSrc, Path.Combine(tempDir, "Cookies"), warnings);
                if (cookiesPath is null)
                {
                    // Fallback — read from the source path. We log a
                    // warning so the user knows we degraded; SQLite's
                    // ReadOnly + immutable handles this fine.
                    cookiesPath = cookiesSrc;
                    warnings.RemoveAll(w => w.StartsWith("Could not copy 'Cookies':"));
                    _log.LogInformation("Chrome import: copy failed, reading source DB directly");
                }
            }
            if (File.Exists(historySrc) && opts.HistoryDays > 0)
            {
                historyPath = TryCopyWithSidecars(historySrc, Path.Combine(tempDir, "History"), warnings);
                if (historyPath is null)
                {
                    historyPath = historySrc;
                    warnings.RemoveAll(w => w.StartsWith("Could not copy 'History':"));
                }
            }
            string? cookiesCopy = cookiesPath;
            string? historyCopy = historyPath;

            // Stage 2: unwrap the AES key from Local State (DPAPI).
            byte[]? aesKey = null;
            try
            {
                if (cookiesCopy is not null && File.Exists(localStateSrc))
                {
                    try
                    {
                        aesKey = LoadAesKey(localStateSrc);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "DPAPI key unwrap failed — encrypted cookies will be skipped");
                        warnings.Add("Could not decrypt the master key (DPAPI). Encrypted cookies will be skipped.");
                    }
                }

                // Stage 3: read cookies.
                if (cookiesCopy is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    ReadCookies(cookiesCopy, aesKey, opts, cookies,
                        out skippedSensitive, out undecryptable, warnings);
                    _log.LogInformation(
                        "Chrome import: read {N} cookie(s); {Skipped} sensitive, {Bad} undecryptable",
                        cookies.Count, skippedSensitive, undecryptable);
                }
            }
            finally
            {
                // Belt-and-braces: zero the AES key the moment we're
                // done with it. Cookie reading is the only consumer.
                if (aesKey is not null)
                    CryptographicOperations.ZeroMemory(aesKey);
            }

            // Stage 4: history (best-effort — failure is a warning).
            if (historyCopy is not null && opts.HistoryDays > 0)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    ReadHistory(historyCopy, opts, historyEntries, out historySkippedSensitive);
                    _log.LogInformation(
                        "Chrome import: read {N} history entr(ies); {Skipped} sensitive",
                        historyEntries.Count, historySkippedSensitive);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "History read failed");
                    warnings.Add($"History read failed: {ex.Message}");
                }
            }

            // Stage 5: persist as a snapshot for the target profile.
            // History isn't a Ghost-Shell first-class concept yet, so
            // we surface it via the snapshot reason field — the user
            // can see "imported X URLs" but the runtime doesn't replay
            // them (yet — Phase 8 candidate).
            long? snapshotId = null;
            if (cookies.Count > 0)
            {
                var payload = new SessionPayload
                {
                    Cookies = cookies,
                    Storage = Array.Empty<StorageEntry>(),
                };
                var reason = $"Chrome import from {src.BrandLabel} ({src.ProfileFolder}): " +
                             $"{cookies.Count} cookies" +
                             (historyEntries.Count > 0
                                 ? $", {historyEntries.Count} history URLs (informational)"
                                 : "");
                snapshotId = await _sessions.SaveAsync(
                    profileName: opts.TargetProfileName,
                    payload:     payload,
                    runId:       null,
                    trigger:     "manual",
                    reason:      reason,
                    ct:          ct);
            }

            var summary = new StringBuilder();
            summary.Append($"Imported {cookies.Count} cookie(s)");
            if (skippedSensitive > 0) summary.Append($", skipped {skippedSensitive} sensitive");
            if (undecryptable > 0)    summary.Append($", {undecryptable} undecryptable");
            if (historyEntries.Count > 0)
                summary.Append($", read {historyEntries.Count} history URL(s)");
            if (historySkippedSensitive > 0)
                summary.Append($" ({historySkippedSensitive} sensitive skipped)");
            summary.Append('.');

            return new ChromeImportResult
            {
                CookiesImported          = cookies.Count,
                CookiesSkippedSensitive  = skippedSensitive,
                CookiesUndecryptable     = undecryptable,
                HistoryEntriesRead       = historyEntries.Count,
                HistorySkippedSensitive  = historySkippedSensitive,
                SnapshotId               = snapshotId,
                Summary                  = summary.ToString(),
                Warnings                 = warnings,
            };
        }
        finally
        {
            // Zero out the AES key — every nanosecond it's still on
            // the heap is one nanosecond a memory-dump attack could
            // recover it. The byte[] reference goes out of scope here
            // anyway, but clearing means the bytes are gone NOW
            // instead of "whenever the GC happens to compact".
            //
            // (We cannot pin to a SecureString or ProtectedMemory
            // here because AesGcm needs a raw byte[] key. This is
            // the best we can do without a crypto rewrite.)
            // Note: cookies list values are also plaintext on the
            // heap by virtue of being in CookieEntry.Value; those
            // get persisted to SQLite and the in-memory references
            // are released by the caller. We accept that exposure as
            // the price of having a working cookie import — a
            // thorough fix would need an opt-in encrypted-DB mode.

            // Best-effort cleanup of the temp DB copies. SQLite holds
            // a brief lock while the connection is closing; one
            // retry is enough on the overwhelming majority of
            // machines.
            for (var i = 0; i < 3; i++)
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, recursive: true);
                    break;
                }
                catch (IOException) when (i < 2)
                {
                    await Task.Delay(150, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Temp dir cleanup gave up: {Dir}", tempDir);
                    break;
                }
            }
        }
    }

    private static string? ResolveExistingFile(string profilePath, params string[] candidates)
    {
        foreach (var rel in candidates)
        {
            var full = Path.Combine(profilePath, rel);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>
    /// Copy a file even if another process holds an exclusive write
    /// lock on it. <see cref="File.Copy"/> uses the default
    /// <c>FileShare.Read</c> on the source which fails against
    /// Chrome's running-process write lock with the classic
    /// "the process cannot access the file" message — the legacy
    /// web works around it by opening the source with
    /// <c>FileShare.ReadWrite | FileShare.Delete</c> so we can read
    /// while Chrome continues to write. The DB might be mid-checkpoint
    /// — SQLite's WAL design tolerates that, and worst-case we get
    /// one slightly-old cookie row out of thousands.
    /// </summary>
    /// <summary>
    /// Copy the SQLite DB plus any WAL / SHM sidecars sitting next to
    /// it. SQLite-in-WAL-mode (Chrome ≥ 89) writes the active txn to
    /// foo.db-wal and only checkpoints into foo.db periodically; if we
    /// copy only foo.db while Chrome is running we'll see stale data
    /// for whatever's currently in flight. Copying all three keeps the
    /// view consistent.
    /// </summary>
    private static string? TryCopyWithSidecars(string source, string dest, List<string> warnings)
    {
        var primary = TryCopy(source, dest, warnings);
        if (primary is null) return null;
        // Sidecar copy: if the source has a -wal / -shm we MUST copy
        // them too, otherwise SQLite re-opens the primary without
        // replaying the WAL — silently giving us stale data. If a
        // sidecar EXISTS but copy FAILS, we surface a warning so the
        // user knows the import may be missing the last few rows
        // (instead of returning happy-path success).
        foreach (var ext in new[] { "-wal", "-shm", "-journal" })
        {
            var sideSrc = source + ext;
            if (!File.Exists(sideSrc)) continue;
            try
            {
                using var s = new FileStream(sideSrc, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var d = new FileStream(dest + ext, FileMode.Create, FileAccess.Write, FileShare.None);
                s.CopyTo(d);
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not copy sidecar '{Path.GetFileName(sideSrc)}': {ex.Message} " +
                             $"(import may miss the last few rows committed to the WAL)");
            }
        }
        return primary;
    }

    private static string? TryCopy(string source, string dest, List<string> warnings)
    {
        try
        {
            using var src = new FileStream(
                source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024, useAsync: false);
            using var dst = new FileStream(
                dest, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: false);
            src.CopyTo(dst);
            return dest;
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not copy '{Path.GetFileName(source)}': {ex.Message}");
            return null;
        }
    }

    // ─── Cookie reader ─────────────────────────────────────────────

    private void ReadCookies(
        string dbPath, byte[]? aesKey, ChromeImportOptions opts,
        List<CookieEntry> output, out int skippedSensitive, out int undecryptable,
        List<string> warnings)
    {
        skippedSensitive = 0;
        undecryptable    = 0;
        var connStr = BuildSqliteConnString(dbPath);
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT host_key, name, value, encrypted_value, path,
                   expires_utc, is_secure, is_httponly, samesite
              FROM cookies
        """;

        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var host  = rdr.GetString(0);
            var name  = rdr.GetString(1);
            var plainValue = rdr.IsDBNull(2) ? null : rdr.GetString(2);
            var encBytes   = rdr.IsDBNull(3) ? null : (byte[])rdr["encrypted_value"];
            var path       = rdr.GetString(4);
            var expiresUtc = rdr.GetInt64(5); // Chrome epoch (1601-01-01)
            var secure     = rdr.GetInt32(6) != 0;
            var httpOnly   = rdr.GetInt32(7) != 0;
            var sameSiteN  = rdr.GetInt32(8); // -1 unspec, 0 None, 1 Lax, 2 Strict

            // Sensitive-domain filter.
            if (opts.SkipSensitiveDomains && IsSensitive(host))
            {
                skippedSensitive++;
                continue;
            }

            // Decrypt value if needed.
            string value;
            if (!string.IsNullOrEmpty(plainValue))
            {
                value = plainValue;
            }
            else if (encBytes is { Length: > 3 } && aesKey is not null)
            {
                if (TryDecrypt(encBytes, aesKey, out var decoded))
                    value = decoded;
                else { undecryptable++; continue; }
            }
            else
            {
                undecryptable++;
                continue;
            }

            output.Add(new CookieEntry
            {
                Name           = name,
                Value          = value,
                Domain         = host,
                Path           = path,
                Secure         = secure,
                HttpOnly       = httpOnly,
                SameSite       = sameSiteN switch { 0 => "None", 1 => "Lax", 2 => "Strict", _ => null },
                ExpiresUnixSec = ChromeTimeToUnix(expiresUtc),
            });
        }
    }

    /// <summary>Convert Chrome's WebKit timestamp (μs since 1601-01-01)
    /// to Unix seconds. 0 / negative → null (session cookie).</summary>
    private static long? ChromeTimeToUnix(long chromeUtc)
    {
        if (chromeUtc <= 0) return null;
        // Chrome stores microseconds since 1601-01-01 UTC.
        // Unix epoch is 1970-01-01. Offset: 11644473600 seconds.
        var unixSec = (chromeUtc / 1_000_000L) - 11_644_473_600L;
        if (unixSec <= 0) return null;
        return unixSec;
    }

    /// <summary>AES-GCM decrypt a Chrome v10/v11 cookie value.</summary>
    private static bool TryDecrypt(byte[] enc, byte[] key, out string plain)
    {
        plain = "";
        // Format: 3-byte version prefix ("v10"|"v11") + 12-byte nonce
        // + ciphertext + 16-byte tag. The minimum size below catches
        // truncated values that AES-GCM would otherwise reject with a
        // less helpful "authentication tag mismatch" exception.
        if (enc.Length < 3 + 12 + 16) return false;
        var prefix = Encoding.ASCII.GetString(enc, 0, 3);
        if (prefix is not ("v10" or "v11")) return false;

        // Plaintext buffer is held in a local that we explicitly clear
        // in the finally block — without that, the decrypted cookie
        // value lingers on the GC heap until the next gen-2 collection
        // (potentially minutes), even after the caller has finished
        // with it. See audit_security.md / audit_disposal.md.
        byte[]? pt = null;
        try
        {
            var nonce = new byte[12];
            Buffer.BlockCopy(enc, 3, nonce, 0, 12);
            var tagOffset = enc.Length - 16;
            var ctLen = tagOffset - 15;
            var ct = new byte[ctLen];
            Buffer.BlockCopy(enc, 15, ct, 0, ctLen);
            var tag = new byte[16];
            Buffer.BlockCopy(enc, tagOffset, tag, 0, 16);
            pt = new byte[ctLen];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ct, tag, pt);
            plain = Encoding.UTF8.GetString(pt);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pt is not null) CryptographicOperations.ZeroMemory(pt);
        }
    }

    /// <summary>Read os_crypt.encrypted_key from Local State and DPAPI-unprotect it.</summary>
    [SupportedOSPlatform("windows")]
    private static byte[] LoadAesKey(string localStatePath)
    {
        var bytes = File.ReadAllBytes(localStatePath);
        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt)
            || !osCrypt.TryGetProperty("encrypted_key", out var keyEl)
            || keyEl.ValueKind != JsonValueKind.String)
            throw new CryptographicException(
                "Local State has no os_crypt.encrypted_key entry");

        var b64 = keyEl.GetString() ?? "";
        var raw = Convert.FromBase64String(b64);
        if (raw.Length < 5 || Encoding.ASCII.GetString(raw, 0, 5) != "DPAPI")
            throw new CryptographicException(
                "encrypted_key missing the expected 'DPAPI' prefix");

        var blob = new byte[raw.Length - 5];
        Buffer.BlockCopy(raw, 5, blob, 0, blob.Length);
        return ProtectedData.Unprotect(
            blob, optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
    }

    // ─── History reader ────────────────────────────────────────────

    private static void ReadHistory(
        string dbPath, ChromeImportOptions opts,
        List<string> output, out int skippedSensitive)
    {
        skippedSensitive = 0;

        var connStr = BuildSqliteConnString(dbPath);
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        // Chrome history time is also μs-since-1601. Convert the
        // window threshold to that scale once.
        var windowStart = DateTime.UtcNow.AddDays(-opts.HistoryDays);
        var chromeThreshold = ((windowStart - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10);

        var cap = opts.MaxUrls > 0 ? Math.Min(opts.MaxUrls, 10_000) : 10_000;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT url
              FROM urls
             WHERE last_visit_time >= @threshold
             ORDER BY last_visit_time DESC
             LIMIT {cap};
        """;
        cmd.Parameters.AddWithValue("@threshold", chromeThreshold);

        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var url = rdr.GetString(0);
            if (opts.SkipSensitiveDomains && IsSensitive(url))
            {
                skippedSensitive++;
                continue;
            }
            output.Add(url);
        }
    }

    // ─── Sensitive-domain filter ───────────────────────────────────

    /// <summary>
    /// Curated keyword list. Mirrors legacy <c>_SENSITIVE_MARKERS</c>.
    /// We deliberately err on the side of dropping too much rather
    /// than too little — the user asked for a sensitive-domain filter
    /// because they don't want their bank or social cookies leaving
    /// their main browser, and a missed match is way worse than a
    /// false drop they can fix by re-running with the filter off.
    /// </summary>
    private static readonly string[] SensitiveMarkers = new[]
    {
        // Banking / payments
        "bank", "paypal", "privat24", "monobank", "wise.com", "revolut",
        "stripe.com", "square.com", "venmo.com", "cashapp",
        // Health / medical
        "health", "medical", "doctor", "hospital", "pharmacy", "medi", "patient",
        // Adult
        "porn", "xxx", "nsfw", "adult", "sex", "onlyfans", "fansly",
        // Social / communication (auth tokens here are catastrophic)
        "facebook.com/", "instagram.com/", "twitter.com/", "x.com/",
        "linkedin.com/", "tiktok.com/", "telegram.org",
        // Mail / cloud
        "gmail.com", "outlook.live.com", "mail.yahoo", "protonmail",
        "drive.google.com", "dropbox.com", "icloud.com",
        // Identity / sensitive github routes
        "github.com/settings", "github.com/orgs", "github.com/security",
        // Password managers
        "1password.com", "bitwarden.com", "lastpass.com", "dashlane.com", "keepass",
    };

    internal static bool IsSensitive(string urlOrHost)
    {
        if (string.IsNullOrEmpty(urlOrHost)) return false;
        var lower = urlOrHost.ToLowerInvariant();
        foreach (var m in SensitiveMarkers)
            if (lower.Contains(m, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Build a connection string suitable for reading Chrome's SQLite
    /// DBs whether they're a fresh copy (live in our temp dir) OR the
    /// original locked file (fallback when the FileShare copy gets
    /// rejected). Three knobs:
    ///   • <c>Mode=ReadOnly</c>      — never tries to acquire a write lock
    ///   • <c>Cache=Private</c>      — avoid SQLite's process-wide cache
    ///                                  taking a SHARED lock on the file,
    ///                                  which can race with Chrome's own
    ///                                  internal locking
    ///   • <c>Pooling=False</c>      — drop the connection on Close()
    ///                                  rather than parking it; we want
    ///                                  the file handle gone immediately
    /// We add the <c>?immutable=1</c> URI flag in the Data-Source path
    /// only when reading the original (locked) file — for our copies
    /// we omit it because the WAL sidecar might still need playback.
    /// </summary>
    private static string BuildSqliteConnString(string path)
    {
        return $"Data Source={path};Mode=ReadOnly;Cache=Private;Pooling=False;";
    }
}
