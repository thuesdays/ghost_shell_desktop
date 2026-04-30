// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Xunit;

namespace GhostShell.Tests.Services;

/// <summary>
/// Lightweight in-memory test of run-history filter logic. We don't
/// spin up SQLite for these — the production RunService delegates
/// SQL filtering to the DB, but the IN-MEMORY equivalent that
/// RunsViewModel uses for client-side filters has its own logic
/// path (post-fetch projection) that needs coverage.
///
/// The RunsViewModel filter pipeline is reproduced in this test
/// fixture as a static helper so we can assert against it without
/// instantiating WPF.
/// </summary>
public class RunServiceTests
{
    [Fact]
    public void Run_StatusLabel_RunningWhenUnfinished()
    {
        var r = new Run { ProfileName = "p", StartedAt = DateTime.UtcNow };
        Assert.True(r.IsRunning);
        Assert.False(r.IsSuccess);
        Assert.False(r.IsFailed);
        Assert.Equal("running", r.StatusLabel);
    }

    [Fact]
    public void Run_StatusLabel_OkWhenExitZero()
    {
        var r = new Run
        {
            ProfileName = "p",
            StartedAt   = DateTime.UtcNow.AddMinutes(-1),
            FinishedAt  = DateTime.UtcNow,
            ExitCode    = 0,
        };
        Assert.False(r.IsRunning);
        Assert.True(r.IsSuccess);
        Assert.Equal("OK", r.StatusLabel);
    }

    [Fact]
    public void Run_StatusLabel_ShowsExitCodeForFailure()
    {
        var r = new Run
        {
            ProfileName = "p",
            StartedAt   = DateTime.UtcNow,
            FinishedAt  = DateTime.UtcNow,
            ExitCode    = -99,
        };
        Assert.False(r.IsRunning);
        Assert.False(r.IsSuccess);
        Assert.True(r.IsFailed);
        Assert.Equal("-99", r.StatusLabel);
    }

    [Fact]
    public void Run_Duration_IsNullWhileRunning()
    {
        var r = new Run { ProfileName = "p", StartedAt = DateTime.UtcNow };
        Assert.Null(r.Duration);
    }

    [Fact]
    public void Run_Duration_ComputesAfterFinish()
    {
        var start  = DateTime.UtcNow.AddSeconds(-90);
        var finish = DateTime.UtcNow;
        var r = new Run
        {
            ProfileName = "p",
            StartedAt   = start,
            FinishedAt  = finish,
            ExitCode    = 0,
        };
        Assert.NotNull(r.Duration);
        Assert.InRange(r.Duration!.Value.TotalSeconds, 89.5, 90.5);
    }

    [Theory]
    [InlineData(RunStatusFilter.All,        4)]
    [InlineData(RunStatusFilter.Successful, 1)]
    [InlineData(RunStatusFilter.Failed,     2)]
    [InlineData(RunStatusFilter.Running,    1)]
    public void StatusFilter_PartitionsCorrectly(RunStatusFilter f, int expected)
    {
        var rows = MakeMixedRows();
        var matched = rows.Where(r => Match(r, f)).Count();
        Assert.Equal(expected, matched);
    }

    private static List<Run> MakeMixedRows() =>
    [
        new Run { ProfileName = "a", StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow, ExitCode = 0  },
        new Run { ProfileName = "b", StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow, ExitCode = 1  },
        new Run { ProfileName = "c", StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow, ExitCode = -99 },
        new Run { ProfileName = "d", StartedAt = DateTime.UtcNow }, // running
    ];

    private static bool Match(Run r, RunStatusFilter f) => f switch
    {
        RunStatusFilter.Successful => r.IsSuccess,
        RunStatusFilter.Failed     => r.IsFailed,
        RunStatusFilter.Running    => r.IsRunning,
        _                          => true,
    };
}
