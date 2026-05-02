// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Common;

/// <summary>
/// Phase 28 — single source of truth for human-readable byte sizes.
/// Lives in Core so both the WPF VM and the unit tests can call it
/// (the test project doesn't depend on WPF). Output ladder: B → KB →
/// MB → GB → TB. Two decimal places below 10, one between 10 and 100,
/// none above. Negative / zero collapses to "0 B".
/// </summary>
public static class ByteFormat
{
    public static string Human(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return v >= 100 ? $"{v:0} {units[u]}"
             : v >= 10  ? $"{v:0.0} {units[u]}"
                        : $"{v:0.00} {units[u]}";
    }
}
