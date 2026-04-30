// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.RegularExpressions;
using GhostShell.Core.Models;
using LogLevel = GhostShell.Core.Models.LogLevel;
using Xunit;

namespace GhostShell.Tests.Models;

/// <summary>
/// Filter logic tests for the Logs page. Uses LogFilter directly so
/// the WPF dispatcher doesn't need to be running. The ViewModel
/// delegates to this same helper so any bug here would surface in
/// the live page; tests act as the contract.
/// </summary>
public class LogFilterTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 30, 12, 0, 0, DateTimeKind.Local);

    private static LogEntry MakeEntry(
        string level = "INF", string source = "GhostShell.Runtime.Browser.RealProfileRunner",
        string message = "hello", DateTime? ts = null) =>
        new(ts ?? FixedNow, level, 1, source, message);

    // ─── Level ───────────────────────────────────────────────────

    [Fact]
    public void NoFilter_AllPass()
    {
        var c = Empty();
        Assert.True(LogFilter.Passes(MakeEntry("INF"), c));
        Assert.True(LogFilter.Passes(MakeEntry("DBG"), c));
        Assert.True(LogFilter.Passes(MakeEntry("ERR"), c));
    }

    [Theory]
    [InlineData(LogLevel.Error,       "INF", false)]
    [InlineData(LogLevel.Error,       "ERR", true)]
    [InlineData(LogLevel.Error,       "FTL", true)]
    [InlineData(LogLevel.Warning,     "INF", false)]
    [InlineData(LogLevel.Warning,     "WAR", true)]
    [InlineData(LogLevel.Information, "DBG", false)]
    [InlineData(LogLevel.Information, "INF", true)]
    public void MinLevel_Filters(LogLevel min, string entryLvl, bool shouldPass)
    {
        var c = Empty() with { MinLevel = min };
        Assert.Equal(shouldPass, LogFilter.Passes(MakeEntry(entryLvl), c));
    }

    [Fact]
    public void MinLevel_DoesNotFilter_RawContinuations()
    {
        // RAW lines bypass level filter — they ride along with the
        // head row above. Filtering a stack-trace away from its
        // exception line would be useless.
        var c = Empty() with { MinLevel = LogLevel.Error };
        Assert.True(LogFilter.Passes(MakeEntry(level: "RAW"), c));
    }

    // ─── Source ──────────────────────────────────────────────────

    [Fact]
    public void SourceFilter_SubstringMatch()
    {
        var c = Empty() with { SourceContains = "Browser" };
        Assert.True (LogFilter.Passes(MakeEntry(source: "GhostShell.Runtime.Browser.RealProfileRunner"), c));
        Assert.False(LogFilter.Passes(MakeEntry(source: "GhostShell.Data.Services.RunService"), c));
    }

    [Fact]
    public void SourceFilter_CaseInsensitive()
    {
        var c = Empty() with { SourceContains = "BROWSER" };
        Assert.True(LogFilter.Passes(MakeEntry(source: "GhostShell.Runtime.Browser.X"), c));
    }

    [Fact]
    public void SourceFilter_Empty_PassesAll()
    {
        var c = Empty() with { SourceContains = "  " };
        Assert.True(LogFilter.Passes(MakeEntry(source: "anything"), c));
    }

    // ─── Profile (matches against message body) ──────────────────

    [Fact]
    public void ProfileFilter_MatchesProfileNameInMessage()
    {
        var c = Empty() with { ProfileContains = "profile_48" };
        Assert.True (LogFilter.Passes(MakeEntry(message: "Profile 'profile_48' started → run #2"), c));
        Assert.False(LogFilter.Passes(MakeEntry(message: "Profile 'profile_99' started"), c));
    }

    // ─── Search (plain) ──────────────────────────────────────────

    [Fact]
    public void SearchText_PlainContainsMatch()
    {
        var c = Empty() with { SearchText = "started" };
        Assert.True (LogFilter.Passes(MakeEntry(message: "Profile 'x' started"), c));
        Assert.False(LogFilter.Passes(MakeEntry(message: "Profile 'x' stopped"), c));
    }

    // ─── Search (regex) ──────────────────────────────────────────

    [Fact]
    public void SearchText_RegexMatchesWhenCompiled()
    {
        var c = Empty() with { SearchText = @"run #\d+", UseRegex = true };
        var rx = new Regex(@"run #\d+", RegexOptions.IgnoreCase);
        Assert.True (LogFilter.Passes(MakeEntry(message: "started → run #5"), c, rx));
        Assert.False(LogFilter.Passes(MakeEntry(message: "starting up"),     c, rx));
    }

    [Fact]
    public void SearchText_Regex_NullCompiled_HidesEverything()
    {
        // Invalid regex caller surface: regex toggle is on, search
        // text is set, but compilation failed. Filter hides all
        // entries until the user fixes the regex (visual cue is
        // the red border on the textbox).
        var c = Empty() with { SearchText = "[bad", UseRegex = true };
        Assert.False(LogFilter.Passes(MakeEntry(message: "anything"), c, compiledRegex: null));
    }

    [Fact]
    public void SearchText_RegexCatastrophicBacktracking_Skipped()
    {
        // Pattern triggers exponential backtracking. Filter must
        // NOT crash — RegexMatchTimeoutException is caught and the
        // entry is treated as "doesn't match" (skipped).
        var pattern = new Regex(@"^(a+)+$",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
        var msg = new string('a', 30) + "X";
        var c = Empty() with { SearchText = "(a+)+", UseRegex = true };

        // No throw, returns false.
        Assert.False(LogFilter.Passes(MakeEntry(message: msg), c, pattern));
    }

    // ─── Time range ──────────────────────────────────────────────

    [Fact]
    public void TimeRange_FiltersOldEntries()
    {
        var oldEntry = MakeEntry(ts: FixedNow.AddHours(-2));
        var newEntry = MakeEntry(ts: FixedNow.AddMinutes(-5));
        var c = Empty() with { TimeRange = TimeSpan.FromMinutes(30) };

        Assert.False(LogFilter.Passes(oldEntry, c));
        Assert.True (LogFilter.Passes(newEntry, c));
    }

    [Fact]
    public void TimeRange_IgnoredForRawContinuations()
    {
        // RAW entries have Timestamp = MinValue (parser convention).
        // Time filter should NOT exclude them or the stack trace
        // disappears the moment its parent times out.
        var raw = MakeEntry(level: "RAW", ts: DateTime.MinValue,
            message: "   at System.Bar()");
        var c = Empty() with { TimeRange = TimeSpan.FromMinutes(5) };
        Assert.True(LogFilter.Passes(raw, c));
    }

    // ─── Combinations ────────────────────────────────────────────

    [Fact]
    public void AllFilters_AndCombined()
    {
        var c = Empty() with
        {
            MinLevel        = LogLevel.Information,
            SourceContains  = "Browser",
            ProfileContains = "p1",
            SearchText      = "started",
        };
        var entry = MakeEntry(
            level: "INF",
            source: "GhostShell.Runtime.Browser.RealProfileRunner",
            message: "Profile 'p1' started → run #1");
        Assert.True(LogFilter.Passes(entry, c));

        // Drop any single condition → fail.
        Assert.False(LogFilter.Passes(MakeEntry(level: "DBG", source: entry.Source!,  message: entry.Message), c));
        Assert.False(LogFilter.Passes(MakeEntry(level: "INF", source: "Other.Source", message: entry.Message), c));
        Assert.False(LogFilter.Passes(MakeEntry(level: "INF", source: entry.Source!,  message: "Profile 'p1' stopped"), c));
        Assert.False(LogFilter.Passes(MakeEntry(level: "INF", source: entry.Source!,  message: "Profile 'p2' started"), c));
    }

    private static LogFilterCriteria Empty() => new(
        MinLevel: null, SourceContains: null, ProfileContains: null,
        SearchText: null, UseRegex: false, TimeRange: null,
        Now: FixedNow);
}
