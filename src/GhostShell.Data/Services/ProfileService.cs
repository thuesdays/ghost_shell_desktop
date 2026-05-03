// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

internal sealed class ProfileService : IProfileService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<ProfileService> _log;

    public ProfileService(DatabaseConnection db, ILogger<ProfileService> log)
    {
        _db  = db;
        _log = log;
    }

    private const string SelectColumns = """
        name,
        group_name           AS GroupName,
        template_id          AS TemplateId,
        language,
        proxy_slug           AS ProxySlug,
        is_ready             AS IsReady,
        enrich_on_first_run  AS EnrichOnFirstRun,
        last_run_at          AS LastRunAt,
        run_count            AS RunCount,
        note,
        created_at           AS CreatedAt,
        updated_at           AS UpdatedAt,
        fp_regen_salt        AS FpRegenSalt,
        fp_noise_salt        AS FpNoiseSalt,
        assigned_script_id   AS AssignedScriptId,
        my_domains           AS MyDomainsCsv,
        target_domains       AS TargetDomainsCsv
    """;

    public async Task<IReadOnlyList<Profile>> ListAsync(CancellationToken ct = default)
    {
        // QueueAsync serialises every read against the shared
        // SqliteConnection. Without this, two ViewModels reloading
        // concurrently (e.g. RunsViewModel.ActiveChanged firing
        // ReloadAsync while the user is mid-navigate to Profiles)
        // hit "There's already an open DataReader" — the page renders
        // empty because the second call throws and Items.Add never
        // happens after the preceding Items.Clear(). The semaphore
        // is process-wide and held only for the duration of the SQL
        // round-trip.
        var sql = $"""
            SELECT  {SelectColumns}
              FROM  profiles
          ORDER BY  COALESCE(last_run_at, created_at) DESC;
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<ProfileRow>(sql), ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<Profile?> GetAsync(string name, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM profiles WHERE name = @name;";
        var row = await _db.QueueAsync(
            c => c.QuerySingleOrDefaultAsync<ProfileRow>(sql, new { name }), ct);
        return row is null ? null : ToModel(row);
    }

    public async Task<Profile> CreateAsync(Profile profile, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var p = ToRow(profile) with
        {
            CreatedAt = profile.CreatedAt == default ? now : profile.CreatedAt,
            UpdatedAt = now,
        };

        const string sql = """
            INSERT INTO profiles
                (name, group_name, template_id, language, proxy_slug,
                 is_ready, enrich_on_first_run, last_run_at, run_count,
                 note, created_at, updated_at,
                 my_domains, target_domains)
            VALUES
                (@Name, @GroupName, @TemplateId, @Language, @ProxySlug,
                 @IsReady, @EnrichOnFirstRun, @LastRunAt, @RunCount,
                 @Note, @CreatedAt, @UpdatedAt,
                 @MyDomainsCsv, @TargetDomainsCsv);
        """;
        await _db.Get().ExecuteAsync(sql, p);
        _log.LogInformation(
            "Created profile '{Name}' (group={Group}, template={Template}, lang={Lang}, proxy={Proxy}, enrich={Enrich})",
            p.Name, p.GroupName ?? "—", p.TemplateId ?? "auto",
            p.Language ?? "—", p.ProxySlug ?? "—", p.EnrichOnFirstRun);
        return ToModel(p);
    }

    public async Task UpdateAsync(Profile profile, CancellationToken ct = default)
    {
        var p = ToRow(profile) with { UpdatedAt = DateTime.UtcNow };

        const string sql = """
            UPDATE profiles
               SET group_name           = @GroupName,
                   template_id          = @TemplateId,
                   language             = @Language,
                   proxy_slug           = @ProxySlug,
                   is_ready             = @IsReady,
                   enrich_on_first_run  = @EnrichOnFirstRun,
                   last_run_at          = @LastRunAt,
                   run_count            = @RunCount,
                   note                 = @Note,
                   updated_at           = @UpdatedAt,
                   my_domains           = @MyDomainsCsv,
                   target_domains       = @TargetDomainsCsv
             WHERE name                 = @Name;
        """;
        await _db.Get().ExecuteAsync(sql, p);
        _log.LogInformation("Updated profile '{Name}'", p.Name);
    }

    /// <summary>
    /// Phase 59 — atomic counter bump on run start. Single UPDATE so
    /// concurrent runs of the same profile (which the runner technically
    /// forbids, but the SQL guarantee is free) can't lose increments.
    /// We don't touch updated_at here because run-start isn't a
    /// "user edited the profile" event and we don't want it to leak
    /// into the "Recently modified" sort on the Profiles page.
    /// </summary>
    public async Task RecordRunStartedAsync(
        string name, DateTime startedAt, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE profiles
               SET run_count   = run_count + 1,
                   last_run_at = @StartedAt
             WHERE name        = @Name;
        """;
        var rows = await _db.Get().ExecuteAsync(
            sql, new { Name = name, StartedAt = startedAt });
        if (rows == 0)
        {
            _log.LogWarning(
                "RecordRunStartedAsync: no profile row updated for name='{Name}'", name);
        }
        else
        {
            _log.LogDebug(
                "RecordRunStartedAsync: incremented run_count for '{Name}'", name);
        }
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        // Phase 24 audit fix — application-level cascade. None of the
        // child tables (vault_items, warmup_runs, script_runs, …)
        // declare FK constraints, so we manually clean up any rows
        // pointing at the deleted profile in one transaction.
        // Phase 27 — also wipe profile_extensions overrides so renames /
        // re-creates with the same name don't inherit stale state.
        await _db.QueueAsync(async c =>
        {
            using var tx = c.BeginTransaction();
            await c.ExecuteAsync(
                "DELETE FROM vault_items WHERE profile_name = @name;",
                new { name }, tx);
            await c.ExecuteAsync(
                "DELETE FROM profile_extensions WHERE profile_name = @name;",
                new { name }, tx);
            // Phase 28 — also cascade traffic_stats so a deleted profile
            // doesn't leave orphan bandwidth accounting in the dashboard.
            await c.ExecuteAsync(
                "DELETE FROM traffic_stats WHERE profile_name = @name;",
                new { name }, tx);
            // Phase 31 — cascade external tester probe results too.
            await c.ExecuteAsync(
                "DELETE FROM external_tester_results WHERE profile_name = @name;",
                new { name }, tx);
            await c.ExecuteAsync(
                "DELETE FROM profiles WHERE name = @name;",
                new { name }, tx);
            tx.Commit();
            return 0;
        }, ct);
        _log.LogInformation("Deleted profile '{Name}' (vault items + extension overrides cascaded)", name);
    }

    public async Task<BulkCreateProfilesResult> BulkCreateAsync(
        BulkCreateProfilesRequest req, CancellationToken ct = default)
    {
        if (req.Count <= 0)
            return new BulkCreateProfilesResult(Array.Empty<Profile>(), Array.Empty<string>());

        // Pre-load existing names so we can skip collisions client-side
        // rather than catching a UNIQUE-constraint exception per row.
        // Keeps the round-trip count to one preflight + one transaction.
        var existing = (await _db.QueueAsync(
            c => c.QueryAsync<string>("SELECT name FROM profiles;"), ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = new List<Profile>(req.Count);
        var skipped = new List<string>();

        var now    = DateTime.UtcNow;
        var prefix = req.Prefix?.Trim() ?? "profile_";
        // Empty pool → all profiles get null proxy (use global default).
        // Non-empty pool → round-robin index advances per CREATED row,
        //                  not per attempted row, so collisions don't
        //                  silently waste a slot.
        var pool = req.ProxyPool ?? Array.Empty<string>();

        var insertSql = """
            INSERT INTO profiles
                (name, group_name, template_id, language, proxy_slug,
                 is_ready, enrich_on_first_run, last_run_at, run_count,
                 note, created_at, updated_at)
            VALUES
                (@Name, @GroupName, @TemplateId, @Language, @ProxySlug,
                 @IsReady, @EnrichOnFirstRun, @LastRunAt, @RunCount,
                 @Note, @CreatedAt, @UpdatedAt);
        """;

        // Single transaction so a mid-run failure rolls everything back.
        // Without this a half-created bulk leaves the DB in a "20 of 50
        // created, the rest invisible" state — the user has to clean
        // up by hand.
        await _db.QueueAsync(async (Microsoft.Data.Sqlite.SqliteConnection c) =>
        {
            using var tx = c.BeginTransaction();
            try
            {
                for (var i = 0; i < req.Count; i++)
                {
                    // probeIdx as long so a hostile StartIndex (e.g.
                    // int.MaxValue - 5) can't wrap into negative
                    // territory and let the collision-skip loop run
                    // forever. The format string still emits the
                    // numeric portion fine for any positive long.
                    long idx  = (long)req.StartIndex + i;
                    var  name = $"{prefix}{idx:000}";

                    // Auto-skip collisions. Keep counting forward until
                    // we either land on a free name or pass a sane
                    // ceiling so we don't loop forever on a saturated
                    // namespace.
                    long probeIdx = idx;
                    while (existing.Contains(name) && probeIdx - idx < 1000)
                    {
                        skipped.Add(name);
                        probeIdx++;
                        name = $"{prefix}{probeIdx:000}";
                    }
                    if (existing.Contains(name))
                    {
                        // Hit the ceiling — record + bail on this row.
                        skipped.Add(name);
                        continue;
                    }

                    var proxy = pool.Count > 0
                        ? pool[created.Count % pool.Count]
                        : null;

                    var row = new ProfileRow
                    {
                        Name             = name,
                        GroupName        = null,
                        TemplateId       = req.TemplateId,
                        Language         = req.Language,
                        ProxySlug        = proxy,
                        IsReady          = 1,
                        EnrichOnFirstRun = req.EnrichOnFirstRun ? 1 : 0,
                        LastRunAt        = null,
                        RunCount         = 0,
                        Note             = null,
                        CreatedAt        = now,
                        UpdatedAt        = now,
                    };
                    await c.ExecuteAsync(insertSql, row, tx);
                    created.Add(ToModel(row));
                    existing.Add(name);
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }, ct);

        _log.LogInformation(
            "Bulk-created {Created} profile(s); skipped {Skipped} collision(s)",
            created.Count, skipped.Count);

        return new BulkCreateProfilesResult(created, skipped);
    }

    // ─── Row mapping ───
    private sealed record ProfileRow
    {
        public required string Name { get; init; }
        public string? GroupName { get; init; }
        public string? TemplateId { get; init; }
        public string? Language { get; init; }
        public string? ProxySlug { get; init; }
        public int IsReady { get; init; }
        public int EnrichOnFirstRun { get; init; }
        public DateTime? LastRunAt { get; init; }
        public int RunCount { get; init; }
        public string? Note { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string? FpRegenSalt { get; init; }
        public string? FpNoiseSalt { get; init; }
        public long? AssignedScriptId { get; init; }
        // Phase 20 — per-profile ad-domain configuration.
        public string? MyDomainsCsv { get; init; }
        public string? TargetDomainsCsv { get; init; }
    }

    private static Profile ToModel(ProfileRow r) => new()
    {
        Name             = r.Name,
        GroupName        = r.GroupName,
        TemplateId       = r.TemplateId,
        Language         = r.Language,
        ProxySlug        = r.ProxySlug,
        IsReady          = r.IsReady == 1,
        EnrichOnFirstRun = r.EnrichOnFirstRun == 1,
        LastRunAt        = r.LastRunAt,
        RunCount         = r.RunCount,
        Note             = r.Note,
        CreatedAt        = r.CreatedAt,
        UpdatedAt        = r.UpdatedAt,
        FpRegenSalt      = r.FpRegenSalt,
        FpNoiseSalt      = r.FpNoiseSalt,
        AssignedScriptId = r.AssignedScriptId,
        MyDomainsCsv     = r.MyDomainsCsv,
        TargetDomainsCsv = r.TargetDomainsCsv,
    };

    private static ProfileRow ToRow(Profile p) => new()
    {
        Name             = p.Name,
        GroupName        = p.GroupName,
        TemplateId       = p.TemplateId,
        Language         = p.Language,
        ProxySlug        = p.ProxySlug,
        IsReady          = p.IsReady ? 1 : 0,
        EnrichOnFirstRun = p.EnrichOnFirstRun ? 1 : 0,
        LastRunAt        = p.LastRunAt,
        RunCount         = p.RunCount,
        Note             = p.Note,
        CreatedAt        = p.CreatedAt,
        UpdatedAt        = p.UpdatedAt,
        MyDomainsCsv     = p.MyDomainsCsv,
        TargetDomainsCsv = p.TargetDomainsCsv,
    };
}
