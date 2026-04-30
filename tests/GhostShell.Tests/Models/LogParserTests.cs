// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using Xunit;

namespace GhostShell.Tests.Models;

/// <summary>
/// LogParser is the contract between the Serilog file-sink format
/// and every consumer (Logs page, log-export tooling, future
/// scheduled-task viewers). The format is pinned by
/// <c>LoggingSetup.cs</c>'s outputTemplate. These tests freeze the
/// shape so a tweak to the template is immediately visible.
/// </summary>
public class LogParserTests
{
    [Fact]
    public void Parse_TypicalInfoLine()
    {
        var line = "[2026-04-29 22:55:09.250 +03:00 INF] [pid:11692] " +
                   "GhostShell.Bootstrap: Ghost Shell starting (v0.2.0)";
        var e = LogParser.Parse(line);

        Assert.Equal("INF",                  e.Level);
        Assert.Equal(11692,                  e.Pid);
        Assert.Equal("GhostShell.Bootstrap", e.Source);
        Assert.Equal("Ghost Shell starting (v0.2.0)", e.Message);
        Assert.Equal(2026, e.Timestamp.Year);
        Assert.Equal(4,    e.Timestamp.Month);
        Assert.Equal(29,   e.Timestamp.Day);
    }

    [Theory]
    [InlineData("VRB")]
    [InlineData("DBG")]
    [InlineData("INF")]
    [InlineData("WAR")]
    [InlineData("ERR")]
    [InlineData("FTL")]
    public void Parse_AcceptsAllLevelTags(string lvl)
    {
        var line = $"[2026-04-29 22:55:09.250 +03:00 {lvl}] [pid:1] X: hi";
        var e = LogParser.Parse(line);
        Assert.Equal(lvl, e.Level);
    }

    [Fact]
    public void Parse_HandlesMissingPid()
    {
        // The pid section is optional in our regex (bracket form
        // wraps a `?`). Make sure a line without it still parses
        // — covers Serilog's enrich-on-failure fallback.
        var line = "[2026-04-29 22:55:09.250 +03:00 INF] " +
                   "GhostShell.Bootstrap: hello";
        var e = LogParser.Parse(line);

        Assert.Null(e.Pid);
        Assert.Equal("GhostShell.Bootstrap", e.Source);
        Assert.Equal("hello", e.Message);
    }

    [Fact]
    public void Parse_StackTraceContinuationBecomesRaw()
    {
        // Exception lines after the head row start with whitespace
        // (Serilog indents them). We tag them RAW so consumers can
        // fold them into the previous entry's Message.
        var line = "   at System.Net.Sockets.TcpClient.Connect()";
        var e = LogParser.Parse(line);

        Assert.Equal("RAW", e.Level);
        Assert.Equal(line,  e.Message);
    }

    [Fact]
    public void Parse_EmptyLineProducesEmptyRawEntry()
    {
        var e = LogParser.Parse("");
        Assert.Equal("RAW", e.Level);
        Assert.Equal("",    e.Message);
    }

    [Fact]
    public void ShortSource_StripsNamespacePrefix()
    {
        var line = "[2026-04-29 22:55:09.250 +03:00 INF] [pid:1] " +
                   "GhostShell.Runtime.Browser.RealProfileRunner: started";
        var e = LogParser.Parse(line);
        Assert.Equal("RealProfileRunner", e.ShortSource);
    }

    [Fact]
    public void SemanticLevel_MapsLogLevel()
    {
        var lines = new (string Tag, LogLevel Expected)[]
        {
            ("VRB", LogLevel.Trace),
            ("DBG", LogLevel.Debug),
            ("INF", LogLevel.Information),
            ("WAR", LogLevel.Warning),
            ("ERR", LogLevel.Error),
            ("FTL", LogLevel.Error),
        };

        foreach (var (tag, expected) in lines)
        {
            var e = LogParser.Parse(
                $"[2026-04-29 22:55:09.250 +03:00 {tag}] [pid:1] X: hi");
            Assert.Equal(expected, e.SemanticLevel);
        }
    }
}
