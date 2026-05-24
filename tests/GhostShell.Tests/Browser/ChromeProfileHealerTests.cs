// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System;
using System.IO;
using GhostShell.Runtime.Browser;
using OpenQA.Selenium;
using Xunit;

namespace GhostShell.Tests.Browser;

/// <summary>
/// Phase 71nn — covers <see cref="ChromeProfileHealer"/>. The class is
/// the only thing standing between a single 0-byte <c>Local State</c>
/// file and a forever-looping scheduler retry storm, so the tests are
/// paranoid:
///   • every known JSON target file (Local State at the top, the two
///     under Default/) heals correctly
///   • healthy files are left strictly alone
///   • non-existent files / dirs are a no-op (not a crash)
///   • the error-message classifier matches the real SessionNotCreated
///     payload chromedriver emits
///   • the startup sweep tolerates one bad profile dir and keeps going
///
/// Every test uses an isolated temp dir under <c>%TEMP%</c> and tears
/// it down on disposal so parallel xUnit runs don't collide.
/// </summary>
public sealed class ChromeProfileHealerTests : IDisposable
{
    private readonly string _root;

    public ChromeProfileHealerTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "gs-healer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string NewProfileDir(string name = "p1")
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(d, "Default"));
        return d;
    }

    // ── healing of individual files ──────────────────────────────────

    [Fact]
    public void HealProfile_ZeroByteLocalState_IsQuarantined()
    {
        // Arrange: simulate the exact in-the-wild failure — a 0-byte
        // top-level "Local State" file at the user-data-dir root.
        var profile = NewProfileDir();
        var path    = Path.Combine(profile, "Local State");
        File.WriteAllBytes(path, Array.Empty<byte>());

        // Act
        var healed = ChromeProfileHealer.HealProfile(profile);

        // Assert: file gone, broken-stamped sibling present.
        Assert.Equal(1, healed);
        Assert.False(File.Exists(path), "Original 0-byte file should have been renamed");
        var siblings = Directory.GetFiles(profile, "Local State.broken-*");
        Assert.Single(siblings);
        Assert.Equal(0, new FileInfo(siblings[0]).Length);
    }

    [Fact]
    public void HealProfile_MalformedPreferences_IsQuarantined()
    {
        // Truncated JSON — Chrome's parser bails at the very first
        // unfinished value, which is the exact "line 1 column 0"
        // error string.
        var profile = NewProfileDir();
        var path    = Path.Combine(profile, "Default", "Preferences");
        File.WriteAllText(path, "{\"profile\":{\"name\":\"al");

        var healed = ChromeProfileHealer.HealProfile(profile);

        Assert.Equal(1, healed);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.Combine(profile, "Default"),
            "Preferences.broken-*"));
    }

    [Fact]
    public void HealProfile_MalformedSecurePreferences_IsQuarantined()
    {
        var profile = NewProfileDir();
        var path    = Path.Combine(profile, "Default", "Secure Preferences");
        // Pure garbage — exercises the JsonException branch, not the
        // 0-byte short-circuit.
        File.WriteAllText(path, "this is not json at all");

        var healed = ChromeProfileHealer.HealProfile(profile);

        Assert.Equal(1, healed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void HealProfile_AllThreeBroken_HealsAll()
    {
        // Realistic post-crash state: Chrome was killed mid-write and
        // every JSON state file ended up either empty or truncated.
        var profile = NewProfileDir();
        File.WriteAllBytes(Path.Combine(profile, "Local State"),               Array.Empty<byte>());
        File.WriteAllText (Path.Combine(profile, "Default", "Preferences"),       "{");
        File.WriteAllText (Path.Combine(profile, "Default", "Secure Preferences"), "");

        var healed = ChromeProfileHealer.HealProfile(profile);

        Assert.Equal(3, healed);
    }

    // ── healthy files are NOT touched ───────────────────────────────

    [Fact]
    public void HealProfile_ValidLocalState_LeftAlone()
    {
        // Real Local State is a moderately-sized object — but a tiny
        // valid JSON object exercises the same parse path.
        var profile = NewProfileDir();
        var path    = Path.Combine(profile, "Local State");
        File.WriteAllText(path, "{\"profile\":{\"info_cache\":{}}}");

        var healed = ChromeProfileHealer.HealProfile(profile);

        Assert.Equal(0, healed);
        Assert.True(File.Exists(path), "Healthy file must NOT be quarantined");
        Assert.Empty(Directory.GetFiles(profile, "Local State.broken-*"));
    }

    [Fact]
    public void HealProfile_ValidPreferences_LeftAlone()
    {
        var profile = NewProfileDir();
        var path    = Path.Combine(profile, "Default", "Preferences");
        // Multi-KB-ish valid JSON to exercise the streaming parse path.
        File.WriteAllText(path,
            "{\"profile\":{\"name\":\"alice\"}," +
            "\"browser\":{\"window_placement\":{\"top\":0,\"left\":0}}}");

        Assert.Equal(0, ChromeProfileHealer.HealProfile(profile));
        Assert.True(File.Exists(path));
    }

    // ── non-existence is a clean no-op ──────────────────────────────

    [Fact]
    public void HealProfile_NonExistentDir_IsNoOp()
    {
        var bogus = Path.Combine(_root, "does-not-exist");
        Assert.Equal(0, ChromeProfileHealer.HealProfile(bogus));
    }

    [Fact]
    public void HealProfile_NoFilesYet_IsNoOp()
    {
        // Fresh profile dir — Chrome hasn't run yet, so no JSON state
        // files exist. Heal must not invent files, must not throw,
        // must return 0.
        var profile = NewProfileDir();
        Assert.Equal(0, ChromeProfileHealer.HealProfile(profile));
    }

    [Fact]
    public void HealProfile_NoDefaultDir_StillChecksLocalState()
    {
        // The Default/ subdir doesn't exist yet (first launch), but
        // a 0-byte Local State at the top must still be caught.
        var profile = Path.Combine(_root, "p-nodef");
        Directory.CreateDirectory(profile);
        File.WriteAllBytes(Path.Combine(profile, "Local State"), Array.Empty<byte>());

        Assert.Equal(1, ChromeProfileHealer.HealProfile(profile));
    }

    [Fact]
    public void HealProfile_NullOrWhitespacePath_IsNoOp()
    {
        // Defensive: callers (BrowserLauncher) compute the path
        // dynamically. A null/empty slipping through must not crash.
        Assert.Equal(0, ChromeProfileHealer.HealProfile(null!));
        Assert.Equal(0, ChromeProfileHealer.HealProfile(""));
        Assert.Equal(0, ChromeProfileHealer.HealProfile("   "));
    }

    // ── HealAllProfiles sweep ───────────────────────────────────────

    [Fact]
    public void HealAllProfiles_VisitsEveryDirAndSurvivesABadOne()
    {
        // Three profile dirs:
        //   • one with a bad Local State           (1 heal)
        //   • one with a bad Default/Preferences    (1 heal)
        //   • one fully healthy                    (0 heal)
        // Expect total = 2, no exception thrown.
        var a = NewProfileDir("a");
        File.WriteAllBytes(Path.Combine(a, "Local State"), Array.Empty<byte>());

        var b = NewProfileDir("b");
        File.WriteAllText(Path.Combine(b, "Default", "Preferences"), "broken{");

        var c = NewProfileDir("c");
        File.WriteAllText(Path.Combine(c, "Local State"), "{}");

        var total = ChromeProfileHealer.HealAllProfiles(_root);

        Assert.Equal(2, total);
    }

    [Fact]
    public void HealAllProfiles_NonExistentRoot_IsNoOp()
    {
        Assert.Equal(0, ChromeProfileHealer.HealAllProfiles(
            Path.Combine(_root, "no-such-root")));
    }

    [Fact]
    public void HealAllProfiles_EmptyRoot_IsNoOp()
    {
        // %LocalAppData%\GhostShell\profiles exists but has no
        // sub-directories yet (fresh install). Sweep returns 0.
        Assert.Equal(0, ChromeProfileHealer.HealAllProfiles(_root));
    }

    // ── error classification ────────────────────────────────────────

    [Fact]
    public void LooksLikeCorruptJsonFailure_MatchesRealSelinumMessage()
    {
        // Verbatim copy of the message Selenium 4 wraps around the
        // chromedriver error in the log the user pasted.
        var real = new InvalidOperationException(
            "session not created\n" +
            "from unknown error: cannot parse internal JSON template: " +
            "EOF while parsing a value at line 1 column 0 (SessionNotCreated)");

        Assert.True(ChromeProfileHealer.LooksLikeCorruptJsonFailure(real));
    }

    [Fact]
    public void LooksLikeCorruptJsonFailure_MatchesViaInnerException()
    {
        // Defensive: when the call site re-wraps the Selenium error
        // (e.g. inside a `Task.Run`), the classifier still has to
        // recognise it via InnerException walk.
        var inner = new WebDriverException(
            "session not created: cannot parse internal JSON template ...");
        var outer = new InvalidOperationException("retry threw", inner);

        Assert.True(ChromeProfileHealer.LooksLikeCorruptJsonFailure(outer));
    }

    [Fact]
    public void LooksLikeCorruptJsonFailure_IgnoresUnrelatedFailures()
    {
        // Connection refused, DevToolsActivePort missing, generic
        // crash — these are NOT the corruption we target. Healer must
        // NOT run, otherwise we'd quarantine perfectly fine files
        // every time chrome.exe crashes for any other reason.
        Assert.False(ChromeProfileHealer.LooksLikeCorruptJsonFailure(
            new InvalidOperationException("DevToolsActivePort file doesn't exist")));
        Assert.False(ChromeProfileHealer.LooksLikeCorruptJsonFailure(
            new InvalidOperationException("Connection refused at localhost:9515")));
        Assert.False(ChromeProfileHealer.LooksLikeCorruptJsonFailure(
            new InvalidOperationException("Chrome failed to start: exited normally")));
        Assert.False(ChromeProfileHealer.LooksLikeCorruptJsonFailure(null));
    }
}
