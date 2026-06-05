// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Fast process enumeration with command lines, used by the launch preflight
/// orphan-reaper and shutdown reaper to find chrome/chromedriver processes
/// holding a specific --user-data-dir.
///
/// Why this exists: the previous implementation queried WMI
/// (<c>SELECT … CommandLine FROM Win32_Process</c>) on EVERY launch. WMI's COM
/// init + per-process marshalling is slow — often 0.3–2 s on weak machines —
/// and it ran on the launch hot path. This reads the command line directly from
/// each process's PEB via <c>NtQueryInformationProcess</c> (sub-millisecond, no
/// COM), and — crucially — skips all work entirely when no candidate process is
/// even running. WMI remains a correctness fallback if the native read fails.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrowserProcessScanner
{
    public readonly record struct ProcInfo(int Pid, string Name, string CommandLine);

    /// <summary>Enumerate running processes whose name (without ".exe") is in
    /// <paramref name="processNames"/>, each with its command line. Native-first,
    /// WMI fallback. Returns empty immediately when none are running.</summary>
    public static IReadOnlyList<ProcInfo> Enumerate(IEnumerable<string> processNames, ILogger? log = null)
    {
        // 1) Cheap candidate scan (no WMI, no command-line read yet). If nothing
        //    is running we're done — the common "no orphans" case pays nothing.
        var candidates = new List<(int Pid, string Name)>();
        foreach (var name in processNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in procs)
            {
                try { candidates.Add((p.Id, name + ".exe")); }
                catch { }
                finally { p.Dispose(); }
            }
        }
        if (candidates.Count == 0) return Array.Empty<ProcInfo>();

        // 2) Native command-line read per candidate. If ALL succeed, return.
        var result = new List<ProcInfo>(candidates.Count);
        var nativeComplete = true;
        foreach (var (pid, name) in candidates)
        {
            var cmd = TryGetCommandLineNative(pid);
            if (cmd is null) { nativeComplete = false; break; }
            result.Add(new ProcInfo(pid, name, cmd));
        }
        if (nativeComplete) return result;

        // 3) Native couldn't read some PID (rare: permissions / odd build).
        //    Fall back to WMI for a complete, correct view.
        log?.LogDebug("BrowserProcessScanner: native command-line read incomplete — falling back to WMI");
        return EnumerateViaWmi(processNames, log);
    }

    // ── Native PEB command-line read (x64) ────────────────────────────────

    private static string? TryGetCommandLineNative(int pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint PROCESS_VM_READ = 0x0010;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            var status = NtQueryInformationProcess(h, 0 /*ProcessBasicInformation*/, ref pbi,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;

            // x64 offsets: PEB->ProcessParameters = +0x20;
            // RTL_USER_PROCESS_PARAMETERS->CommandLine (UNICODE_STRING) = +0x70.
            if (!TryReadPtr(h, pbi.PebBaseAddress + 0x20, out var procParams) || procParams == IntPtr.Zero)
                return null;
            // UNICODE_STRING { ushort Length; ushort MaxLength; (pad) IntPtr Buffer }
            var usAddr = procParams + 0x70;
            var usBuf = new byte[16];
            if (!ReadMem(h, usAddr, usBuf)) return null;
            int length = BitConverter.ToUInt16(usBuf, 0);          // bytes
            var bufferPtr = (IntPtr)BitConverter.ToInt64(usBuf, 8);
            if (length <= 0 || length > 64 * 1024 || bufferPtr == IntPtr.Zero) return null;

            var strBuf = new byte[length];
            if (!ReadMem(h, bufferPtr, strBuf)) return null;
            return System.Text.Encoding.Unicode.GetString(strBuf);
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    private static bool TryReadPtr(IntPtr h, IntPtr addr, out IntPtr value)
    {
        var b = new byte[8];
        if (!ReadMem(h, addr, b)) { value = IntPtr.Zero; return false; }
        value = (IntPtr)BitConverter.ToInt64(b, 0);
        return true;
    }

    private static bool ReadMem(IntPtr h, IntPtr addr, byte[] buf)
        => ReadProcessMemory(h, addr, buf, buf.Length, out var read) && read == (IntPtr)buf.Length;

    // ── WMI fallback (correctness) ────────────────────────────────────────

    private static IReadOnlyList<ProcInfo> EnumerateViaWmi(IEnumerable<string> processNames, ILogger? log)
    {
        var wanted = new HashSet<string>(
            processNames.Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe"),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<ProcInfo>();
        try
        {
            var clause = string.Join(" OR ", wanted.Select(n => $"Name = '{n.Replace("'", "''")}'"));
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE {clause}");
            using var results = searcher.Get();
            foreach (var o in results)
            {
                using var mo = (System.Management.ManagementObject)o;
                if (mo["ProcessId"] is null) continue;
                result.Add(new ProcInfo(
                    Convert.ToInt32(mo["ProcessId"]),
                    mo["Name"] as string ?? "",
                    mo["CommandLine"] as string ?? ""));
            }
        }
        catch (Exception ex) { log?.LogDebug(ex, "BrowserProcessScanner: WMI fallback failed"); }
        return result;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
