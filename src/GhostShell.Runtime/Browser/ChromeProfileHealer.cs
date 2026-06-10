// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Phase 71nn — heals corrupted Chrome JSON state files that cause the
/// dreaded
/// <c>"session not created — cannot parse internal JSON template:
/// EOF while parsing a value at line 1 column 0 (SessionNotCreated)"</c>
/// failure on driver init.
///
/// Root cause: Chrome reads several JSON files on startup (Local State,
/// Default/Preferences, Default/Secure Preferences). If any of them is
/// 0 bytes or otherwise malformed (truncated by power loss, killed
/// mid-write during a previous launch, clobbered by an antivirus, or
/// left half-written after a self-update/reinstall) — Chrome's JSON
/// parser bails at "line 1 column 0" and chromedriver reports it back
/// to Selenium as <c>SessionNotCreated</c>. The browser process exits
/// instantly, no DevTools port is ever opened, and we get an infinite
/// retry loop because the back-off path re-fires every 30 s without
/// touching the bad file.
///
/// Healing strategy: rename each corrupted file to
/// <c>&lt;name&gt;.broken-&lt;yyyyMMdd-HHmmss&gt;</c> so Chrome
/// re-creates a fresh copy with safe defaults on next launch. We
/// quarantine instead of deleting so the user can post-mortem the
/// failure if they want to.
///
/// Pure-static + DI-free on purpose: this needs to run from
/// <see cref="BrowserLauncher"/> (before every launch, sync) AND from
/// <see cref="GhostShell.App.App.OnStartup"/> (once on boot, before
/// the host is built). Adding interface + DI plumbing for what is
/// effectively a 4-file health check would be ceremony for no benefit.
/// </summary>
public static class ChromeProfileHealer
{
    /// <summary>Top-level JSON file Chrome reads BEFORE picking a profile.
    /// If this one is corrupt, Chrome aborts immediately — even before
    /// it ever touches Default/.</summary>
    private const string LocalStateFile = "Local State";

    /// <summary>Per-profile JSON files. Both live under
    /// <c>&lt;user-data-dir&gt;/Default/</c> — that's the only profile
    /// Ghost Shell ever materialises (we don't use Chrome's multi-
    /// profile feature).</summary>
    private static readonly string[] DefaultProfileJsonFiles =
    {
        "Preferences",
        "Secure Preferences",
    };

    /// <summary>
    /// Validate every known JSON file in a single user-data-dir and
    /// quarantine any that are empty or unparseable. Returns the number
    /// of files quarantined (0 = profile was healthy).
    ///
    /// Tolerates missing directories / files — they just count as
    /// "nothing to heal". A profile that has never been launched has
    /// no JSON state yet; Chrome will create everything fresh.
    /// </summary>
    public static int HealProfile(string userDataDir, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(userDataDir))
            return 0;
        if (!Directory.Exists(userDataDir))
            return 0;

        var healed = 0;

        // Top-level "Local State" — sits directly in the user-data-dir,
        // NOT inside Default/.
        if (QuarantineIfBroken(Path.Combine(userDataDir, LocalStateFile), log))
            healed++;

        // Per-profile files under Default/. If Default/ doesn't exist
        // yet (very first launch) there's nothing to validate.
        var defaultDir = Path.Combine(userDataDir, "Default");
        if (Directory.Exists(defaultDir))
        {
            foreach (var name in DefaultProfileJsonFiles)
            {
                if (QuarantineIfBroken(Path.Combine(defaultDir, name), log))
                    healed++;
            }
        }

        if (healed > 0)
        {
            log?.LogWarning(
                "ChromeProfileHealer: quarantined {Count} corrupt JSON file(s) in '{Dir}' — " +
                "Chrome will regenerate them with safe defaults on next launch",
                healed, userDataDir);
        }

        return healed;
    }

    /// <summary>
    /// Walk every immediate sub-directory of the profiles root and run
    /// <see cref="HealProfile"/> on each. Designed for the startup
    /// path — runs once on boot to catch leftover damage from a
    /// previous crash / self-update / reinstall, so the user doesn't
    /// need to wait for the first scheduled fire to surface (and then
    /// retry-loop on) the broken file.
    ///
    /// Tolerates a missing root entirely — fresh installs have no
    /// profiles dir yet. Caps the scan at a sane upper bound so a
    /// pathological setup with thousands of profile dirs can't pin
    /// the startup thread.
    /// </summary>
    public static int HealAllProfiles(string profilesRoot, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(profilesRoot))
            return 0;
        if (!Directory.Exists(profilesRoot))
            return 0;

        var totalHealed   = 0;
        var profilesScanned = 0;
        const int scanCap = 2000; // defensive — we don't expect >100 in practice

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(profilesRoot))
            {
                if (profilesScanned++ >= scanCap)
                {
                    log?.LogWarning(
                        "ChromeProfileHealer: scan cap ({Cap}) reached in '{Root}' — " +
                        "skipping remaining profile dirs",
                        scanCap, profilesRoot);
                    break;
                }

                try
                {
                    totalHealed += HealProfile(dir, log);
                }
                catch (Exception ex)
                {
                    // One bad profile dir must NOT stop the sweep —
                    // we'd rather heal 99 of 100 than zero.
                    log?.LogWarning(ex,
                        "ChromeProfileHealer: heal pass threw for '{Dir}' — continuing",
                        dir);
                }
            }
        }
        catch (Exception ex)
        {
            // Enumeration itself can throw if the root is yanked mid-
            // scan (rare, but seen on network-mounted profile dirs).
            log?.LogWarning(ex,
                "ChromeProfileHealer: couldn't enumerate profiles root '{Root}'", profilesRoot);
        }

        if (totalHealed > 0)
        {
            log?.LogInformation(
                "ChromeProfileHealer: startup sweep healed {Total} corrupt file(s) " +
                "across {Profiles} profile dir(s) under '{Root}'",
                totalHealed, profilesScanned, profilesRoot);
        }
        else
        {
            log?.LogDebug(
                "ChromeProfileHealer: startup sweep clean ({Profiles} profile dir(s) under '{Root}')",
                profilesScanned, profilesRoot);
        }

        return totalHealed;
    }

    /// <summary>
    /// True when the exception looks like the "cannot parse internal
    /// JSON template" SessionNotCreated failure that this healer
    /// targets. Used by <see cref="BrowserLauncher"/> to decide whether
    /// to invoke <see cref="HealProfile"/> + retry, vs. propagate the
    /// failure unchanged (e.g. a real Chrome crash / OOM / proxy refusal).
    ///
    /// We match by message substring because Selenium wraps the inner
    /// chromedriver error string verbatim inside an
    /// <see cref="InvalidOperationException"/> — there's no exception
    /// type or error code to switch on.
    /// </summary>
    public static bool LooksLikeCorruptJsonFailure(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message;
            if (string.IsNullOrEmpty(msg)) continue;

            // The canonical signatures we've seen in the wild. Both are
            // generated by Chromium's base/json/json_parser.cc — the
            // first when the file is literally empty, the second when
            // it's truncated mid-value.
            if (msg.Contains("cannot parse internal JSON template", StringComparison.OrdinalIgnoreCase))
                return true;
            if (msg.Contains("EOF while parsing", StringComparison.OrdinalIgnoreCase)
                && msg.Contains("SessionNotCreated", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the driver-init failure looks like a STALE-PROFILE-LOCK
    /// failure — i.e. an orphaned chrome.exe / chromedriver.exe from a
    /// previous run is still holding the profile's <c>--user-data-dir</c>
    /// (or a leftover SingletonLock), so the new launch can't take it over.
    /// The launcher responds by reaping those orphans + clearing the
    /// singleton locks and retrying once.
    ///
    /// The dominant field signature is
    ///   <c>"session not created: from unknown error: failed to write prefs
    ///    file (SessionNotCreated)"</c>
    /// which is chromedriver being unable to (over)write
    /// <c>Default/Preferences</c> because the directory is held by a live
    /// chrome.exe. Pre-fix this was NOT recognised as recoverable (only the
    /// corrupt-JSON signature was), so the launch fell straight through to
    /// the generic catch and rethrew — the profile then failed on EVERY
    /// subsequent attempt until the zombie chrome happened to exit, which is
    /// exactly the 30-minute "launch_failed" loop seen in the logs.
    ///
    /// <c>"DevToolsActivePort file doesn't exist"</c> is the same family
    /// (chrome died at boot because the dir was in use). We also match a bare
    /// <c>"session not created"</c> that ISN'T the corrupt-JSON case, since a
    /// reap+retry is a safe, cheap thing to try for any SessionNotCreated
    /// that the heal path doesn't already own.
    /// </summary>
    public static bool LooksLikeProfileLockFailure(Exception? ex)
    {
        // Corrupt-JSON has its own dedicated heal path; don't double-claim it.
        if (LooksLikeCorruptJsonFailure(ex)) return false;

        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message;
            if (string.IsNullOrEmpty(msg)) continue;

            if (msg.Contains("failed to write prefs file", StringComparison.OrdinalIgnoreCase))
                return true;
            if (msg.Contains("DevToolsActivePort", StringComparison.OrdinalIgnoreCase))
                return true;
            if (msg.Contains("session not created", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("SessionNotCreated", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// If the file at <paramref name="path"/> is broken (0 bytes or
    /// invalid JSON), rename it to <c>&lt;name&gt;.broken-&lt;ts&gt;</c>
    /// and return true. If it's healthy, missing, or unreadable,
    /// return false. Never throws — a heal that itself crashes would
    /// be worse than the original symptom.
    ///
    /// audit LAUNCH-09: returns false when the file was broken but
    /// could only be truncated (not moved aside) — the bad file
    /// survives under its original name, so reporting it as healed
    /// would mislead the launcher's heal+retry logic.
    /// </summary>
    private static bool QuarantineIfBroken(string path, ILogger? log)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var info = new FileInfo(path);

            // 0-byte file — the dominant failure mode. Chrome aborted
            // mid-write or an antivirus zeroed it. Skip the JSON-parse
            // path; we know the answer.
            //
            // audit LAUNCH-09: only report a successful heal when the
            // file was actually moved aside. A truncate-only fallback
            // leaves a fresh 0-byte file at the ORIGINAL name (still
            // bad), so it must NOT count toward `healed` — otherwise the
            // launcher's retry proceeds on an unchanged-bad file and
            // burns its single retry budget on a no-op.
            if (info.Length == 0)
            {
                return Quarantine(path, "zero-length", log);
            }

            // Tiny size guard before reading: Chrome's smallest valid
            // JSON state file is ~50 bytes ("{}"+whitespace+a few keys).
            // Anything below 2 bytes can't even be "{}" so we don't
            // need to bother round-tripping it through JsonDocument.
            if (info.Length < 2)
            {
                return Quarantine(path, $"impossibly small ({info.Length} B)", log);
            }

            // Full parse. JsonDocument streams from the file so we don't
            // load the whole thing into RAM (some Preferences files
            // hit a few MB after a long browsing session).
            //
            // CRITICAL — release the FileStream BEFORE calling Quarantine.
            // On Windows, File.Move on a path with an open handle fails
            // with "file is being used by another process" → fallback
            // truncate runs → original filename survives at 0 bytes and
            // the test/launch keeps tripping on it. Pulled the parse
            // into its own scope so `using` runs Dispose before we
            // attempt the rename below.
            string? parseError = null;
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var doc = JsonDocument.Parse(fs);
                // Parsed OK → file is healthy from a JSON standpoint.
                // We don't validate the schema here; Chrome is tolerant
                // of missing/extra keys, the only thing that kills the
                // browser at boot is unparseable bytes.
            }
            catch (JsonException jx)
            {
                parseError = jx.Message;
            }

            if (parseError is not null)
            {
                // audit LAUNCH-09: propagate the real move-vs-truncate
                // outcome so a truncate-only fallback isn't counted as
                // healed (see the zero-length branch above).
                return Quarantine(path, $"invalid JSON ({parseError})", log);
            }
            return false;
        }
        catch (Exception ex)
        {
            // Couldn't even stat / open the file (locked by chrome.exe
            // we're racing against, ACL'd, removable drive yanked).
            // Don't pretend we healed it; let the launch path surface
            // its own error.
            log?.LogDebug(ex,
                "ChromeProfileHealer: probe failed for '{Path}' (treating as healthy)", path);
            return false;
        }
    }

    /// <summary>
    /// Move the offending file out of the way. Renamed to
    /// <c>&lt;name&gt;.broken-&lt;yyyyMMdd-HHmmss&gt;</c> so Chrome
    /// regenerates a clean copy on its next launch (and the user can
    /// inspect the original after the fact if they want).
    ///
    /// Returns <c>true</c> only when the file was actually moved aside
    /// (so the caller can count it as a real heal). A truncate-only
    /// fallback returns <c>false</c>: the original filename still
    /// exists and is still bad, so it must not advance the launcher's
    /// heal+retry logic (audit LAUNCH-09).
    /// </summary>
    private static bool Quarantine(string path, string reason, ILogger? log)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var dest  = $"{path}.broken-{stamp}";

            // Tiny race-free retry: if the same file was healed twice
            // in the same second (clock granularity) the destination
            // would clash. Append a short counter.
            var i = 0;
            while (File.Exists(dest) && i < 10)
                dest = $"{path}.broken-{stamp}-{++i}";

            File.Move(path, dest, overwrite: false);
            log?.LogWarning(
                "ChromeProfileHealer: quarantined '{Path}' → '{Dest}' (reason: {Reason})",
                path, Path.GetFileName(dest), reason);
            // The bad file is gone from its original name — genuine heal.
            return true;
        }
        catch (Exception ex)
        {
            // Move can fail if chrome.exe still has a handle on the
            // file (we should have killed it in LaunchPreflight, but
            // defensive code is cheap). Fall back to truncating: an
            // empty file with the original name is JUST as bad as a
            // 0-byte file, but at least the next heal pass will hit
            // the "0 bytes" branch which deletes it instead of moving
            // — and chromedriver fails at the same point so we don't
            // make anything worse.
            log?.LogWarning(ex,
                "ChromeProfileHealer: rename of '{Path}' failed — trying truncate-to-empty fallback",
                path);
            try
            {
                File.WriteAllText(path, string.Empty);
            }
            catch (Exception ex2)
            {
                log?.LogError(ex2,
                    "ChromeProfileHealer: truncate fallback ALSO failed for '{Path}' — " +
                    "this profile will continue to fail until the file can be replaced manually",
                    path);
            }

            // audit LAUNCH-09: the file still lives under its ORIGINAL
            // name (now 0 bytes, or unchanged if the truncate also
            // failed). Report this as NOT healed so the launcher's
            // retry doesn't proceed on a file that is still bad and
            // would fail identically — wasting its single retry budget
            // on a no-op heal. The next full heal pass (or a successful
            // reaper kill of the locking handle) can quarantine it for
            // real once the lock clears.
            return false;
        }
    }
}
