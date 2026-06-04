// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Pre-flight checks + cleanup that run BEFORE we hand the launch
/// to Selenium. Mirrors the same set of guards the legacy Python
/// runtime keeps under <c>process_reaper.py</c> and the front of
/// <c>runtime.py</c>:
///
///   1. Kill any chrome.exe / chromedriver.exe still holding the
///      target user-data-dir from a previous failed launch.
///      Failure to do this is the #1 cause of
///      "DevToolsActivePort file doesn't exist" — the new chrome
///      can't bind the singleton-mutex / port, so it crashes
///      before writing the file Selenium polls for.
///
///   2. Wipe Chrome session-restore state (Current Session, Last
///      Session, Sessions/) so the new run starts fresh and
///      doesn't try to re-open tabs from the previous crash.
///
///   3. Ensure the user-data-dir exists.
///
/// All steps are best-effort — failures get logged but don't abort
/// the launch (the launch itself will surface a real error if the
/// problem still bites). This file is internal because it's only
/// useful to the launcher; the public API is the static
/// <see cref="Run(string,string,ILogger)"/> entry.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LaunchPreflight
{
    /// <summary>
    /// Walk every chrome.exe / chromedriver.exe on the box, kill the
    /// ones whose command line references <paramref name="userDataDir"/>,
    /// then wipe stale session-restore state and ensure the dir
    /// exists.
    /// </summary>
    public static void Run(string userDataDir, string profileName, ILogger log)
    {
        // ── 1. orphan sweep ────────────────────────────────────────
        try
        {
            var killed = KillProcessesUsingDir(userDataDir, log);
            if (killed > 0)
                log.LogWarning(
                    "Preflight: reaped {Count} orphan chrome/chromedriver process(es) " +
                    "still holding profile '{Name}' from a previous run",
                    killed, profileName);
            else
                log.LogDebug("Preflight: no orphan chrome processes for '{Name}'", profileName);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Preflight: orphan sweep failed (non-fatal)");
        }

        // ── 2. ensure dir exists ───────────────────────────────────
        try
        {
            Directory.CreateDirectory(userDataDir);
            // The Default subdirectory is what --profile-directory=Default
            // points at; pre-creating it removes a small race window
            // where chrome's first-write happens during a crash.
            Directory.CreateDirectory(Path.Combine(userDataDir, "Default"));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Preflight: could not pre-create user-data-dir '{Dir}' (non-fatal)",
                userDataDir);
        }

        // ── 3. wipe session-restore state ──────────────────────────
        // After a crash, chrome will try to "restore" the previous
        // window/tab set on next launch. For an automation profile
        // that's never what we want — those URLs may have side-
        // effects or auto-close anyway. Plus the Sessions/ DB is a
        // common source of corruption that bricks chrome silently.
        var defaultDir = Path.Combine(userDataDir, "Default");
        var sessionFiles = new[]
        {
            "Current Session",
            "Current Tabs",
            "Last Session",
            "Last Tabs",
        };
        foreach (var name in sessionFiles)
        {
            var path = Path.Combine(defaultDir, name);
            TryDelete(path, log);
        }
        TryDeleteDir(Path.Combine(defaultDir, "Sessions"), log);

        // ── 4. drop SingletonLock if stale ─────────────────────────
        // Chrome on Windows uses Local State + SingletonLock files
        // to enforce one-instance-per-user-data-dir. If a previous
        // chrome.exe died without unwinding cleanly, the lock can
        // outlive it, and the next launch refuses to start. The
        // orphan sweep above takes care of MOST of these, but the
        // file itself is a separate gate worth clearing too.
        TryDelete(Path.Combine(userDataDir, "SingletonLock"),    log);
        TryDelete(Path.Combine(userDataDir, "SingletonCookie"),  log);
        TryDelete(Path.Combine(userDataDir, "SingletonSocket"),  log);
    }

    // ─────────────────────────────────────────────────────────
    // Internals
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the number of processes killed.
    /// </summary>
    private static int KillProcessesUsingDir(string userDataDir, ILogger log)
    {
        // audit LAUNCH-08: a raw substring match on `--user-data-dir={path}`
        // collides with sibling profiles whose name is a prefix of ours
        // (e.g. profile 'acme' matches 'acme2'), which would reap an
        // unrelated live session. Instead of substring-matching, we parse
        // the actual --user-data-dir argument value out of each command
        // line and compare it to the target for exact path equality.
        var killed = 0;
        // WMI query gives us each process plus its full command line —
        // standard Process API on Windows only exposes the file name
        // and we'd need a second native call per process to get argv.
        const string query =
            "SELECT ProcessId, Name, CommandLine FROM Win32_Process " +
            "WHERE Name = 'chrome.exe' OR Name = 'chromedriver.exe'";
        using var searcher = new ManagementObjectSearcher(query);
        using var results  = searcher.Get();

        foreach (var obj in results)
        {
            using var mo = (ManagementObject)obj;
            var pidObj = mo["ProcessId"];
            var cmd    = mo["CommandLine"] as string ?? "";
            if (pidObj is null) continue;
            // audit LAUNCH-08: exact, separator-normalized path equality
            // on the parsed argument value — not a prefix-prone substring.
            if (!CommandLineTargetsDir(cmd, userDataDir))
                continue;

            var pid = Convert.ToInt32(pidObj);
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited) continue;
                log.LogInformation(
                    "Preflight: killing orphan {Name} pid={Pid} (matches user-data-dir)",
                    proc.ProcessName, pid);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(2000);
                killed++;
            }
            catch (ArgumentException) { /* process already gone */ }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Preflight: could not kill pid={Pid}", pid);
            }
        }
        return killed;
    }

    /// <summary>
    /// audit LAUNCH-08: returns true only when <paramref name="cmd"/> contains a
    /// <c>--user-data-dir</c> switch whose value resolves to the SAME directory as
    /// <paramref name="userDataDir"/>. We extract the argument value (handling both
    /// quoted and unquoted forms, with the value either inline as
    /// <c>--user-data-dir=VALUE</c> or in the following token) and compare with
    /// separator-/case-normalized path equality, so a sibling profile whose name is
    /// a prefix of ours (e.g. 'acme' vs 'acme2') no longer matches.
    /// </summary>
    private static bool CommandLineTargetsDir(string cmd, string userDataDir)
    {
        if (string.IsNullOrEmpty(cmd)) return false;

        const string flag = "--user-data-dir";
        var search = 0;
        while (true)
        {
            var idx = cmd.IndexOf(flag, search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            var after = idx + flag.Length;
            // The switch may legitimately appear as a substring of a longer,
            // unrelated token (none ship today, but be defensive): the real
            // flag is terminated by '=' (inline value) or whitespace (next-token
            // value). Anything else is a false positive — skip and keep scanning.
            string? value = null;
            if (after < cmd.Length && cmd[after] == '=')
            {
                value = ReadArgValue(cmd, after + 1);
            }
            else if (after >= cmd.Length || char.IsWhiteSpace(cmd[after]))
            {
                // value is the next whitespace-delimited token
                var p = after;
                while (p < cmd.Length && char.IsWhiteSpace(cmd[p])) p++;
                if (p < cmd.Length) value = ReadArgValue(cmd, p);
            }

            if (value is not null && PathsEqual(value, userDataDir))
                return true;

            search = after;
        }
    }

    /// <summary>
    /// Reads a single command-line argument value starting at <paramref name="start"/>.
    /// Honors a leading double-quote (value runs to the closing quote); otherwise the
    /// value runs to the next whitespace.
    /// </summary>
    private static string ReadArgValue(string cmd, int start)
    {
        if (start >= cmd.Length) return string.Empty;
        if (cmd[start] == '"')
        {
            var end = cmd.IndexOf('"', start + 1);
            if (end < 0) end = cmd.Length;
            return cmd.Substring(start + 1, end - (start + 1));
        }

        var p = start;
        while (p < cmd.Length && !char.IsWhiteSpace(cmd[p])) p++;
        return cmd.Substring(start, p - start);
    }

    /// <summary>
    /// Case-insensitive, separator-normalized path equality. Trailing separators
    /// and forward/back slash differences are ignored. Falls back to a normalized
    /// string compare if the path cannot be canonicalized (e.g. malformed argv).
    /// </summary>
    private static bool PathsEqual(string a, string b)
    {
        static string Normalize(string p)
        {
            p = p.Trim().Trim('"');
            try
            {
                // GetFullPath resolves relative segments and unifies separators;
                // it does not require the path to exist on disk.
                p = Path.GetFullPath(p);
            }
            catch
            {
                // best-effort: unify separators manually
                p = p.Replace('/', '\\');
            }
            return p.TrimEnd('\\', '/');
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path, ILogger log)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Preflight: could not delete {Path}", path);
        }
    }

    private static void TryDeleteDir(string path, ILogger log)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Preflight: could not delete dir {Path}", path);
        }
    }
}
