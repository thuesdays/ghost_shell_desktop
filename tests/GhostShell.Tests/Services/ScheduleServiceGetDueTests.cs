// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Data.Database;
using GhostShell.Data.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GhostShell.Tests.Services;

/// <summary>
/// Regression tests for <see cref="ScheduleService.GetDueAsync"/>'s date
/// comparison against a real on-disk SQLite file — the exact production path.
///
/// Guards the date-format bug: the schedules table binds its date columns as
/// .NET DateTime, so Microsoft.Data.Sqlite stores them as
/// "yyyy-MM-dd HH:mm:ss.FFFFFFF" (SPACE separator, no 'Z'). The old query
/// compared that against `now.ToString("O")` ("yyyy-MM-ddTHH:mm:ss.fffffffZ",
/// 'T' separator). Lexicographically a space (0x20) sorts before 'T' (0x54) at
/// index 10, so EVERY stored next_fire_at compared as "due" on EVERY tick and
/// the back-off / next-fire scheduling was silently ignored — a failing target
/// got re-fired every 30 s. These tests fail on the old code and pass on the
/// fixed `datetime(next_fire_at) <= datetime(@nowIso)` comparison.
/// </summary>
public sealed class ScheduleServiceGetDueTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ScheduleService _svc;

    public ScheduleServiceGetDueTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ghostshell-sched-{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath, NullLogger<DatabaseConnection>.Instance);
        new MigrationRunner(_db, NullLogger<MigrationRunner>.Instance).Run();
        _svc = new ScheduleService(_db, NullLogger<ScheduleService>.Instance);
    }

    private static Schedule NewSchedule(string name, DateTime nextFireUtc, bool enabled = true) => new()
    {
        Name        = name,
        TargetKind  = ScheduleTargetKind.Profile,
        TargetName  = "profile_2",
        TriggerKind = ScheduleTriggerKind.Simple,
        RunsPerDay  = 150,
        Enabled     = enabled,
        NextFireAt  = nextFireUtc,
    };

    [Fact]
    public async Task GetDue_excludes_a_schedule_whose_next_fire_is_in_the_future()
    {
        var now = DateTime.UtcNow;
        await _svc.CreateAsync(NewSchedule("future", now.AddHours(1)));

        var due = await _svc.GetDueAsync(now);

        // The whole point: a future next_fire_at must NOT be returned. On the
        // buggy code the space-vs-'T' mismatch returned it anyway.
        Assert.DoesNotContain(due, s => s.Name == "future");
    }

    [Fact]
    public async Task GetDue_includes_a_schedule_whose_next_fire_is_in_the_past()
    {
        var now = DateTime.UtcNow;
        await _svc.CreateAsync(NewSchedule("past", now.AddMinutes(-5)));

        var due = await _svc.GetDueAsync(now);

        Assert.Contains(due, s => s.Name == "past");
    }

    [Fact]
    public async Task RecordFailure_backoff_actually_defers_the_next_fire()
    {
        // Reproduces the field scenario: a launch keeps failing, the back-off
        // pushes next_fire_at ~1h ahead, and the schedule must then NOT be due
        // on the next 30 s tick. Pre-fix it re-fired immediately every tick
        // ("back-off 3600s" in the log yet a new launch_failed every 33 s).
        var now = DateTime.UtcNow;
        var created = await _svc.CreateAsync(NewSchedule("flaky", now.AddMinutes(-1)));

        // Initially due (next_fire in the past).
        Assert.Contains(await _svc.GetDueAsync(now), s => s.Id == created.Id);

        // Back-off: push next_fire 1h out, exactly as RunnerHost does on a
        // failed fire.
        await _svc.RecordFailureAsync(created.Id, now.AddHours(1));

        // Now it must be deferred — not returned for the next ~hour of ticks.
        Assert.DoesNotContain(await _svc.GetDueAsync(now), s => s.Id == created.Id);
        Assert.DoesNotContain(await _svc.GetDueAsync(now.AddMinutes(30)), s => s.Id == created.Id);

        // And it becomes due again once its time arrives.
        Assert.Contains(await _svc.GetDueAsync(now.AddHours(1).AddSeconds(1)), s => s.Id == created.Id);
    }

    [Fact]
    public async Task GetDue_orders_by_next_fire_ascending()
    {
        var now = DateTime.UtcNow;
        await _svc.CreateAsync(NewSchedule("later",   now.AddMinutes(-1)));
        await _svc.CreateAsync(NewSchedule("earlier", now.AddMinutes(-10)));

        var due = (await _svc.GetDueAsync(now)).Where(s => s.Name is "later" or "earlier").ToList();

        Assert.Equal(2, due.Count);
        Assert.Equal("earlier", due[0].Name);
        Assert.Equal("later",   due[1].Name);
    }

    [Fact]
    public async Task GetDue_skips_disabled_schedules_even_when_past_due()
    {
        var now = DateTime.UtcNow;
        await _svc.CreateAsync(NewSchedule("disabled", now.AddMinutes(-5), enabled: false));

        Assert.DoesNotContain(await _svc.GetDueAsync(now), x => x.Name == "disabled");
    }

    public void Dispose()
    {
        _db.Dispose();
        for (var i = 0; i < 5; i++)
        {
            try { File.Delete(_dbPath); break; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}
