// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GhostShell.App.Browser;

/// <summary>
/// Reads/writes the Chromium <c>ApplicationBoundEncryptionEnabled</c> enterprise
/// policy (HKLM) per browser brand. This is the only reliable way to make a
/// browser's v127+ "App-Bound" (v20) cookies importable: with the policy set to
/// 0 the browser re-encrypts cookies in the DPAPI-readable v10 format on its
/// next writes, so the standard import can then decrypt them.
///
/// Writing HKLM requires admin (the app already runs elevated). Effect is not
/// instant — the user must restart the browser and let it re-save cookies.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ChromeAppBoundPolicy
{
    private const string ValueName = "ApplicationBoundEncryptionEnabled";

    /// <summary>HKLM policy subkey for a discovered browser brand, or null when
    /// the brand has no known Chromium policy namespace.</summary>
    private static string? PolicySubKey(string brandLabel) => brandLabel switch
    {
        "Google Chrome"  => @"SOFTWARE\Policies\Google\Chrome",
        "Microsoft Edge" => @"SOFTWARE\Policies\Microsoft\Edge",
        "Brave Browser"  => @"SOFTWARE\Policies\BraveSoftware\Brave",
        "Chromium"       => @"SOFTWARE\Policies\Chromium",
        _ => null,
    };

    public static bool IsSupported(string brandLabel) => PolicySubKey(brandLabel) is not null;

    /// <summary>Current policy state: 0 = disabled, 1 = enabled, null = not set.</summary>
    public static int? GetState(string brandLabel)
    {
        var sub = PolicySubKey(brandLabel);
        if (sub is null) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(sub);
            return key?.GetValue(ValueName) is int i ? i : null;
        }
        catch { return null; }
    }

    /// <summary>Set the policy to disabled (DWORD 0). Returns success + a
    /// human-readable message. Fails cleanly (no throw) when not elevated.</summary>
    public static (bool Ok, string Message) Disable(string brandLabel)
    {
        var sub = PolicySubKey(brandLabel);
        if (sub is null)
            return (false, $"No App-Bound policy is defined for {brandLabel}.");
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(sub, writable: true);
            if (key is null)
                return (false, $"Couldn't open the {brandLabel} policy key (admin required).");
            key.SetValue(ValueName, 0, RegistryValueKind.DWord);
            return (true,
                $"App-Bound encryption disabled for {brandLabel}. Restart {brandLabel}, browse for a " +
                "bit so it re-saves cookies in the readable format, then run the import again.");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Administrator rights are required to set this machine policy. " +
                           "Run GhostShell as administrator and try again.");
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't set the policy: {ex.Message}");
        }
    }
}
