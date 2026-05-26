// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Common;

/// <summary>
/// Phase 71oo — thrown by <c>BrowserLauncher.LaunchAsync</c> when a
/// browser launch for the same profile is already in flight. Carries
/// the profile name so callers can compose user-facing messages
/// without parsing the exception text.
///
/// This is NOT a failure in the traditional sense — it's a deliberate
/// "back off, try again later" signal that prevents two concurrent
/// launches from killing each other in the preflight orphan-reaper.
/// Callers must NOT count it against scheduler back-off / fail_count /
/// run-failure counters; the standard handler is "log info, retry
/// next tick (or surface a friendly UI message)".
///
/// Derives from <see cref="InvalidOperationException"/> so any existing
/// `catch (Exception)` blocks keep working without an explicit catch
/// for the new type — but specialised handlers can match on
/// <see cref="ProfileBusyException"/> for clean back-off semantics.
/// </summary>
public sealed class ProfileBusyException : InvalidOperationException
{
    /// <summary>The profile whose launch was already in flight.</summary>
    public string ProfileName { get; }

    public ProfileBusyException(string profileName)
        : base($"Profile '{profileName}' is already launching — refusing concurrent launch")
    {
        ProfileName = profileName ?? string.Empty;
    }
}
