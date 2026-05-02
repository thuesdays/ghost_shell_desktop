// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Data.Services;
using Xunit;

namespace GhostShell.Tests;

/// <summary>
/// Phase 28 — deterministic bucket-math + helper tests for the traffic
/// service. The DB-touching reader/writer paths require a real SQLite
/// connection, so we keep those out of the unit tier and exercise just
/// the pure functions here.
/// </summary>
public class TrafficServiceTests
{
    [Fact]
    public void BucketHour_Formats_LocalTime_With_Hour_Resolution()
    {
        var dt = new DateTime(2026, 5, 2, 14, 35, 12, DateTimeKind.Local);
        var b  = TrafficService.BucketHour(dt);
        Assert.Equal("2026-05-02 14", b);
    }

    [Fact]
    public void BucketDay_Drops_Hour_Component()
    {
        var dt = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Local);
        Assert.Equal("2026-12-31", TrafficService.BucketDay(dt));
    }

    [Fact]
    public void BucketHour_Sortable_Lexicographically()
    {
        // Lex order over "YYYY-MM-DD HH" must equal chronological order.
        var earlier = TrafficService.BucketHour(new DateTime(2026, 5, 2, 9, 0, 0, DateTimeKind.Local));
        var later   = TrafficService.BucketHour(new DateTime(2026, 5, 2, 10, 0, 0, DateTimeKind.Local));
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Theory]
    [InlineData(1,    "hour")]
    [InlineData(24,   "hour")]
    [InlineData(48,   "hour")]
    [InlineData(49,   "day")]
    [InlineData(720,  "day")]
    public void AutoBucket_Switches_To_Day_Above_48h(int hours, string expected)
        => Assert.Equal(expected, TrafficService.AutoBucket(hours));

    // ─── Batch merge ──────────────────────────────────────────────────

    [Fact]
    public void MergeDeltas_Sums_Same_Profile_Domain_Hour()
    {
        var hour = new DateTime(2026, 5, 2, 14, 30, 0, DateTimeKind.Local);
        var batch = new[]
        {
            new TrafficDelta { ProfileName = "p1", Domain = "a.com", Timestamp = hour, Bytes = 100, ReqCount = 1 },
            new TrafficDelta { ProfileName = "p1", Domain = "a.com", Timestamp = hour, Bytes = 200, ReqCount = 3 },
        };
        var merged = TrafficService.MergeDeltas(batch);
        Assert.Single(merged);
        var (bytes, requests, runId) = merged[("p1", "a.com", "2026-05-02 14")];
        Assert.Equal(300, bytes);
        Assert.Equal(4, requests);
        Assert.Null(runId);
    }

    [Fact]
    public void MergeDeltas_Splits_Different_Hour_Buckets()
    {
        var batch = new[]
        {
            new TrafficDelta { ProfileName = "p1", Domain = "a.com",
                Timestamp = new DateTime(2026, 5, 2, 14, 0, 0, DateTimeKind.Local),
                Bytes = 100, ReqCount = 1 },
            new TrafficDelta { ProfileName = "p1", Domain = "a.com",
                Timestamp = new DateTime(2026, 5, 2, 15, 0, 0, DateTimeKind.Local),
                Bytes = 200, ReqCount = 2 },
        };
        var merged = TrafficService.MergeDeltas(batch);
        Assert.Equal(2, merged.Count);
        Assert.Equal(100L, merged[("p1", "a.com", "2026-05-02 14")].Bytes);
        Assert.Equal(200L, merged[("p1", "a.com", "2026-05-02 15")].Bytes);
    }

    [Fact]
    public void MergeDeltas_RunId_First_NonNull_Wins()
    {
        var hour = new DateTime(2026, 5, 2, 14, 0, 0, DateTimeKind.Local);
        var batch = new[]
        {
            new TrafficDelta { ProfileName = "p1", Domain = "a.com", Timestamp = hour, Bytes = 1, RunId = null },
            new TrafficDelta { ProfileName = "p1", Domain = "a.com", Timestamp = hour, Bytes = 2, RunId = 42  },
            new TrafficDelta { ProfileName = "p1", Domain = "a.com", Timestamp = hour, Bytes = 3, RunId = 99  },
        };
        var merged = TrafficService.MergeDeltas(batch);
        Assert.Equal(42L, merged.Single().Value.RunId);
    }

    [Fact]
    public void MergeDeltas_Skips_Blank_Profile_Or_Domain()
    {
        var hour = new DateTime(2026, 5, 2, 14, 0, 0, DateTimeKind.Local);
        var batch = new[]
        {
            new TrafficDelta { ProfileName = "",      Domain = "a.com", Timestamp = hour, Bytes = 1 },
            new TrafficDelta { ProfileName = "p1",    Domain = "",      Timestamp = hour, Bytes = 2 },
            new TrafficDelta { ProfileName = "  ",    Domain = "a.com", Timestamp = hour, Bytes = 3 },
            new TrafficDelta { ProfileName = "p1",    Domain = "a.com", Timestamp = hour, Bytes = 4 },
        };
        var merged = TrafficService.MergeDeltas(batch);
        Assert.Single(merged);
        Assert.Equal(4L, merged.Single().Value.Bytes);
    }

    [Fact]
    public void MergeDeltas_Empty_Or_Null_Returns_Empty()
    {
        Assert.Empty(TrafficService.MergeDeltas(Array.Empty<TrafficDelta>()));
        Assert.Empty(TrafficService.MergeDeltas(null!));
    }
}

/// <summary>Tests for the byte-formatting helper used by both the
/// dashboard stat-bar and the chart's left-axis labels.</summary>
public class FormatBytesTests
{
    [Theory]
    [InlineData(0,        "0 B")]
    [InlineData(-1,       "0 B")]
    [InlineData(1,                                "1.00 B")]
    [InlineData(1023,                             "1023 B")]
    [InlineData(1024,                             "1.00 KB")]
    [InlineData(1536,                             "1.50 KB")]
    [InlineData(1024 * 1024,                      "1.00 MB")]
    [InlineData(1536 * 1024,                      "1.50 MB")]
    [InlineData(1L * 1024 * 1024 * 1024,          "1.00 GB")]
    [InlineData(2L * 1024 * 1024 * 1024 * 1024,   "2.00 TB")]
    public void FormatBytes_Scales_Through_TB(long input, string expected)
        => Assert.Equal(expected, ByteFormat.Human(input));
}
