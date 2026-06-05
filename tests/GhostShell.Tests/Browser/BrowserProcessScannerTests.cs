// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using System.Runtime.Versioning;
using GhostShell.Runtime.Browser;
using Xunit;

namespace GhostShell.Tests.Browser;

/// <summary>
/// Audit fix: BrowserProcessScanner replaced per-launch WMI with a native
/// PEB command-line read + a free skip when nothing matches. These pin the two
/// behaviours that matter for the launch hot path.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrowserProcessScannerTests
{
    [Fact]
    public void NoMatchingProcesses_ReturnsEmpty_NoWork()
    {
        // A name that can't be running → the cheap candidate scan returns empty
        // immediately (the common "no orphans" launch case pays nothing).
        var r = BrowserProcessScanner.Enumerate(new[] { "__ghostshell_no_such_proc__" });
        Assert.Empty(r);
    }

    [Fact]
    public void FindsCurrentProcess_AndReadsCommandLineNatively()
    {
        // Enumerate our own process name; we must find our PID and the native
        // PEB read must return a non-empty command line (proves the WMI-free
        // path actually works on this machine).
        var name = Process.GetCurrentProcess().ProcessName;
        var found = BrowserProcessScanner.Enumerate(new[] { name });

        var self = found.FirstOrDefault(p => p.Pid == Environment.ProcessId);
        Assert.NotEqual(default, self);
        Assert.False(string.IsNullOrEmpty(self.CommandLine),
            "native command-line read returned empty for the current process");
    }
}
