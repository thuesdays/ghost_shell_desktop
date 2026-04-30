// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Real Windows-cursor movement via P/Invoke <c>SendInput</c>. Used by
/// <c>Humanizer.ClickAsync</c> when the patched-Chromium window is in
/// the foreground; falls back to the JS-event chain otherwise (the
/// JS fallback is enough for most pages — only sites that read
/// raw <c>MouseEvent.movementX</c> via <c>requestPointerLock</c> can
/// distinguish the two).
///
/// Bezier curve: 3 control points (start, mid-deviation, end).
/// Mid-deviation is per-call random within ±15% of the straight-line
/// distance, so two clicks on the same target are visually distinct.
///
/// Coordinate space: SendInput's MOUSEINPUT.dx / dy are in
/// "normalised absolute coordinates" (0..65535 across the whole
/// virtual screen). We multiply by SCREEN_SIZE_NORM / actual pixel
/// counts to convert.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeMouse
{
    /// <summary>
    /// Move the cursor along a Bezier path from current position to
    /// (<paramref name="x"/>, <paramref name="y"/>) over
    /// <paramref name="totalMs"/> milliseconds. Steps every ~16ms
    /// (60fps) so the path is visible to a screen recorder.
    ///
    /// TOCTOU defense (Phase 14 audit B/D): we re-check the
    /// foreground window on every step. The user can alt-tab
    /// mid-curve; without this re-check we'd keep injecting cursor
    /// movements + clicks into whatever window now has focus
    /// (potentially a password manager, calculator, etc.). On loss
    /// of foreground we abort the move silently — caller's
    /// fallback path (JS-event chain in Humanizer) picks up.
    /// </summary>
    public static async Task MoveToAsync(
        int x, int y, int totalMs = 350, CancellationToken ct = default)
    {
        if (totalMs < 16) totalMs = 16;
        if (!IsChromiumForeground()) return;
        if (!GetCursorPos(out var start)) return;

        // Bezier control point — perpendicular offset from the
        // midpoint, sized to ~15% of the move distance. Sign-flipped
        // 50% of the time so the arc bows up half the runs and down
        // the other half.
        var mx = (start.X + x) / 2;
        var my = (start.Y + y) / 2;
        var dx = x - start.X;
        var dy = y - start.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var off = (int)(len * 0.15);
        if (off < 4) off = 4;
        // Perpendicular: rotate (dx, dy) by 90° → (-dy, dx). Normalise.
        var px = len < 0.001 ? 0 : (-dy / len);
        var py = len < 0.001 ? 0 : ( dx / len);
        var sign = Random.Shared.Next(2) == 0 ? 1 : -1;
        var cpX = mx + (int)(px * off * sign);
        var cpY = my + (int)(py * off * sign);

        // Step count: one step per ~16ms.
        var steps = Math.Max(4, totalMs / 16);
        for (var i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            // TOCTOU re-check inside the loop — user could alt-tab
            // between steps. Abort on focus loss.
            if (!IsChromiumForeground()) return;
            var t = (double)i / steps;
            // Quadratic Bezier:  B(t) = (1-t)² P0 + 2(1-t)t P1 + t² P2
            var u = 1.0 - t;
            var bx = (int)(u * u * start.X + 2 * u * t * cpX + t * t * x);
            var by = (int)(u * u * start.Y + 2 * u * t * cpY + t * t * y);
            SetAbsolute(bx, by);
            await Task.Delay(totalMs / steps, ct);
        }
        // Snap to exact endpoint — eases away the integer-rounding
        // drift over many steps. Final foreground check protects
        // the snap as well.
        if (IsChromiumForeground()) SetAbsolute(x, y);
    }

    /// <summary>
    /// Click at the current cursor position via SendInput. Two events:
    /// LEFTDOWN + LEFTUP, with a 30-90ms gap (real users hold a click
    /// briefly).
    /// </summary>
    public static async Task ClickAsync(CancellationToken ct = default)
    {
        if (!IsChromiumForeground()) return;
        SendButton(MOUSEEVENTF_LEFTDOWN);
        await Task.Delay(Random.Shared.Next(30, 91), ct);
        if (!IsChromiumForeground()) return; // user alt-tabbed mid-press; abort up
        SendButton(MOUSEEVENTF_LEFTUP);
    }

    /// <summary>
    /// True iff the foreground window's process matches the patched
    /// Chromium binary. Without this check we'd potentially be
    /// driving the cursor over arbitrary other apps the user has
    /// open — definitively not what they want.
    /// </summary>
    public static bool IsChromiumForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return false;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = p.ProcessName.ToLowerInvariant();
            return name == "chrome" || name == "chromium";
        }
        catch { return false; }
    }

    // ─── P/Invoke ─────────────────────────────────────────────────

    private static void SetAbsolute(int x, int y)
    {
        // Convert pixel coordinates to "normalised absolute"
        // (0..65535 over the primary monitor's logical size).
        var sw = GetSystemMetrics(SM_CXSCREEN);
        var sh = GetSystemMetrics(SM_CYSCREEN);
        if (sw <= 0 || sh <= 0) return;
        var nx = (int)(x * 65535.0 / sw);
        var ny = (int)(y * 65535.0 / sh);
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx          = nx,
                    dy          = ny,
                    dwFlags     = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                    time        = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private static void SendButton(uint flags)
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } },
        };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    private const int  INPUT_MOUSE             = 0;
    private const uint MOUSEEVENTF_MOVE        = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN    = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP      = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE    = 0x8000;
    private const int  SM_CXSCREEN             = 0;
    private const int  SM_CYSCREEN             = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
