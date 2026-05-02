// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.Vault;

/// <summary>
/// Phase 26 — auto-lock idle watcher.
///
/// Subscribes to global app input (PreviewMouseDown / PreviewKeyDown
/// on the Application, plus low-cost Win32 LASTINPUTINFO polling for
/// activity in OTHER windows the user has focused) and, when the vault
/// has been unlocked, ticks a 30-second timer. If the user has been
/// idle for longer than <see cref="IVaultService.GetAutoLockMinutesAsync"/>,
/// the vault is locked.
///
/// 0 minutes = auto-lock disabled. The watcher still observes activity
/// (cheap), it just never fires <c>Lock()</c>.
///
/// We layer two activity sources so the user doesn't get logged out
/// while typing into another desktop app:
///   • WPF input events stamp <see cref="IVaultService.NotifyActivity"/>.
///   • <see cref="GetLastInputInfo"/> reports the system-wide last
///     input tick — used as a tiebreaker if it's more recent than our
///     own stamp.
/// </summary>
public sealed class VaultIdleWatcher : IDisposable
{
    private readonly IVaultService _vault;
    private readonly ILogger<VaultIdleWatcher> _log;
    private readonly DispatcherTimer _timer;
    private bool _started;

    public VaultIdleWatcher(IVaultService vault, ILogger<VaultIdleWatcher> log)
    {
        _vault = vault;
        _log = log;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>Hook input events on the running app + start the tick
    /// loop. Idempotent; safe to call once App.OnStartup has built the
    /// MainWindow.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        // WPF-level input — fires for any focused control inside our
        // app, regardless of which window it's in (modal dialogs too).
        EventManager.RegisterClassHandler(typeof(Window),
            UIElement.PreviewMouseDownEvent, new RoutedEventHandler(OnInput));
        EventManager.RegisterClassHandler(typeof(Window),
            UIElement.PreviewKeyDownEvent, new RoutedEventHandler(OnInput));
        EventManager.RegisterClassHandler(typeof(Window),
            UIElement.PreviewMouseWheelEvent, new RoutedEventHandler(OnInput));

        _timer.Start();
        _log.LogInformation("VaultIdleWatcher started");
    }

    private void OnInput(object? sender, RoutedEventArgs e)
    {
        _vault.NotifyActivity();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        try
        {
            if (!_vault.IsUnlocked) return;

            // If MainWindow is hidden and user hid it explicitly (not just minimized),
            // skip auto-lock evaluation to avoid false-positive idle detection
            var mw = Application.Current?.MainWindow;
            if (mw is not null && mw.Visibility != Visibility.Visible)
            {
                // Window is hidden; skip the idle check. The watcher will resume
                // evaluation when the window is restored.
                return;
            }

            // Take the more-recent of (WPF stamp, system stamp). The
            // system stamp covers cases where the user is working in
            // another app — we don't want to lock them out for that.
            var lastUtc = _vault.LastActivityUtc;
            var sysIdleSec = TryGetSystemIdleSeconds();
            if (sysIdleSec is { } seconds)
            {
                var sysLast = DateTime.UtcNow - TimeSpan.FromSeconds(seconds);
                if (sysLast > lastUtc) lastUtc = sysLast;
            }

            int minutes;
            try { minutes = await _vault.GetAutoLockMinutesAsync(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Couldn't read auto-lock setting");
                return;
            }
            if (minutes <= 0) return;

            var idle = DateTime.UtcNow - lastUtc;
            if (idle >= TimeSpan.FromMinutes(minutes))
            {
                _log.LogInformation(
                    "Vault auto-lock fired (idle {IdleMin:F1}m ≥ threshold {ThrMin}m)",
                    idle.TotalMinutes, minutes);
                _vault.Lock();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "VaultIdleWatcher tick failed");
        }
    }

    public void Dispose()
    {
        try { _timer.Stop(); _timer.Tick -= OnTick; } catch { /* ignore */ }
    }

    // ─── Win32 last-input ─────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Seconds since the system saw any user input (mouse/key)
    /// in any process. Returns null on failure (the caller falls back
    /// to our own activity stamp).</summary>
    private static double? TryGetSystemIdleSeconds()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) return null;
            // Environment.TickCount wraps every 49.7 days; using the
            // unsigned uint diff handles wrap-around cleanly.
            uint now = (uint)Environment.TickCount;
            uint idleMs = unchecked(now - lii.dwTime);
            return idleMs / 1000.0;
        }
        catch { return null; }
    }
}
