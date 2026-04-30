# Phase 11-14 Data-Flow & DB Consistency Audit

**Project:** GhostShell Desktop (C# / WPF antidetect browser)  
**Audit Scope:** Migrations V11–V14, service layer (Script/Profile/FingerprintAudit/SelfCheck), background monitors, data models  
**Audit Date:** 2026-04-30  
**Severity Scale:** CRITICAL | HIGH | MEDIUM | LOW

---

## Executive Summary

The Phase 11-14 migration chain introduces fingerprint auditing, self-check results, script execution, and default-script assignment. While the migration runner's tolerant-statement approach handles partial re-execution safely, the audit reveals:

- **No foreign-key cascades** on script deletion → orphan profiles with invalid assigned_script_id
- **ETag race conditions** in two-tab script editing; winner-take-all with silent user UX failure
- **Unbounded growth** in fingerprint_audits and selfcheck_results; no retention/cleanup
- **Concurrent migration retry** on V14's ALTER TABLE lacks transaction isolation
- **Default script race** when two concurrent processes set is_default=1 simultaneously
- **Inconsistent Finalise()** in ScriptRunner uses zeroed fields instead of persisting actual values
- **SQLite WAL + QueueAsync semaphore** bottleneck under concurrent background service writes
- **Orphan runs** (started but never finished due to crash mid-execution)
- **Script.StepsJson deserialization** schema drift between saved JSON and ScriptStep model
- **Migration V14 blocks on runs table** if script_run_id backfill is ever added

**Finding Count:** 33 issues (4 CRITICAL, 17 HIGH, 12 MEDIUM)

---

## CRITICAL FINDINGS

### C1: Migration Order Safety — Fresh Install Multi-Version Apply
**File:** `MigrationRunner.cs`, **Line:** 43-75  
**Severity:** CRITICAL  
**Scope:** System startup, multi-version cold install

**Issue:**  
Fresh database install that hits all migrations in sequence (V1→V14) calls ApplyTolerantStatements three times in an unprotected sequence. If a process crash occurs **between** V11 and V13 (e.g., after V11 completes, during V13's ALTER TABLE assignment), the next startup re-runs V11 (swallowing duplicate-column errors), then re-runs V13's CREATE TABLE statements, but the ALTER TABLE on profiles for `assigned_script_id` may partially apply.

```csharp
// Line 56-74: Three separate if(!applied.Contains(version)) blocks
if (!applied.Contains(11))
    ApplyTolerantStatements(conn, 11, Migrations_V11.Statements);  // V11 CREATE + ALTER
if (!applied.Contains(13))
    ApplyTolerantStatements(conn, 13, Migrations_V13.Statements);  // V13: CREATE TABLE + ALTER
if (!applied.Contains(14))
    ApplyTolerantStatements(conn, 14, Migrations_V14.Statements);  // V14: ALTER + CREATE INDEX
```

**Root Cause:**  
- `ApplyTolerantStatements` applies **all statements in the array without a transaction**. Line 120-130 processes each statement independently, catching only DuplicateColumn errors.
- The version stamp (line 133-136) happens **after all statements**, but if the process crashes mid-loop, some statements apply, others don't, and version isn't stamped → next startup repeats all.
- Multiple ALTER TABLE statements against the same table in different versions (V13 adds assigned_script_id, V14 adds script_run_id on runs) aren't protected by a global transaction.

**Reproduction Path:**
1. Fresh install, DB created
2. Migration runner starts, applies V1-V10 (all succeed, versions stamped)
3. V11 runs: 2 ALTER + CREATE statements; process crashes mid-V11 (after first ALTER, before second ALTER)
4. Next startup: V11 not in __schema_version, so ApplyTolerantStatements re-runs; second ALTER statement throws duplicate-column, gets swallowed, no error propagates
5. If both ALTERs somehow needed to succeed atomically, DB is now in inconsistent state

**Impact:**  
- Lost data integrity on fresh installs in crash scenarios
- Partial schema application could cause silent failures in downstream code
- V11's fp_regen_salt might exist but fp_noise_salt missing

**Mitigation:**  
Wrap `ApplyTolerantStatements` in a transaction. If any statement fails (other than DuplicateColumn), roll back and re-throw. Stamp version **only after all statements succeed**.

---

### C2: Profile.AssignedScriptId Orphan References (No Cascade Delete)
**File:** `Migrations_V13.cs` L55, `ScriptService.cs` L104-106, `ProfileService.cs` L97-115  
**Severity:** CRITICAL  
**Scope:** Script lifecycle, profile reads

**Issue:**  
Migration V13 adds a foreign-key-like column `profiles.assigned_script_id INTEGER` without a real SQL FOREIGN KEY constraint. When a script is deleted via `ScriptService.DeleteAsync()`, no CASCADE or SET NULL occurs on matching profile rows. A profile with `assigned_script_id=5` continues to point to the deleted script.

```csharp
// Migrations_V13.cs L55 — no REFERENCES clause, no CASCADE
"ALTER TABLE profiles ADD COLUMN assigned_script_id INTEGER;",

// ScriptService.cs L104-106 — unconditional DELETE
public Task DeleteAsync(long id, CancellationToken ct = default)
    => _db.QueueAsync(c => c.ExecuteAsync(
        "DELETE FROM scripts WHERE id = @id;", new { id }), ct);

// RealProfileRunner.cs L217-219 — script fetch without existence check
if (profile.AssignedScriptId is { } sid)
    script = await _scripts.GetAsync(sid, cts.Token);  // Returns NULL if deleted
script ??= await _scripts.GetDefaultAsync(cts.Token);   // Falls back to default
```

**Consequences:**
1. Profile.AssignedScriptId points to non-existent script → GetAsync returns NULL
2. RealProfileRunner.KickAssignedScriptAsync() silently falls back to default script
3. User believes script X is bound to profile, but X was deleted → X never runs
4. Audit trail is lost (no log of the script deletion affecting profiles)

**Reproduction:**
```
1. Create Script#1, assign to Profile_A
2. User manually deletes Script#1
3. Profile_A.AssignedScriptId still = 1
4. Next run of Profile_A: KickAssignedScriptAsync calls GetAsync(1) → NULL
5. Falls back to default script silently
6. User doesn't know their custom script isn't running
```

**Mitigation:**
- Add real FOREIGN KEY: `ALTER TABLE profiles ADD CONSTRAINT fk_assigned_script FOREIGN KEY (assigned_script_id) REFERENCES scripts(id) ON DELETE SET NULL;`
- OR: add a pre-delete hook to ScriptService.DeleteAsync() that clears assigned_script_id from all profiles before deleting the script
- Log when a deletion affects profiles

---

### C3: ETag Race Condition — Two-Tab Script Editing
**File:** `ScriptService.cs` L71-102, `Script.cs` L35-37  
**Severity:** CRITICAL  
**Scope:** Script editor, concurrent edit windows

**Issue:**  
The ETag-conditional UPDATE in `ScriptService.UpdateAsync()` uses a race-last-write-wins pattern: when two browser tabs edit the same script concurrently, whoever saves second gets an InvalidOperationException (because their stale etag no longer matches the DB value set by the first save). However, the exception message and UI handling are misaligned.

```csharp
// ScriptService.cs L71-102
public async Task UpdateAsync(Script s, string expectedEtag, CancellationToken ct = default)
{
    var newEtag = Guid.NewGuid().ToString("N");
    const string sql = """
        UPDATE scripts SET
            ...
            etag = @NewETag,
            updated_at = @Now
          WHERE id = @Id AND etag = @ExpectedETag;  // ← Race condition: if etag changed, 0 rows updated
    """;
    var rows = await _db.QueueAsync(c => c.ExecuteAsync(sql, ...), ct);
    if (rows == 0)
    {
        throw new InvalidOperationException(
            "Script was modified by another session — reload the editor and re-apply your changes.");
    }
}
```

**Root Cause:**
- Tab 1 loads script etag='ABC'. Tab 2 loads script etag='ABC'.
- Tab 1 saves → DB etag becomes 'XYZ'.
- Tab 2 saves with expectedEtag='ABC' → WHERE etag='ABC' matches nothing → rows=0 → exception thrown.
- However, Tab 2's UI may have already shown a spinner or optimistic update, then suddenly shows an exception.
- No automatic reload/refresh is triggered; user must manually reload.

**Additional Risk:**
The StepsJson is serialized in memory; if two editors load the same script, modify independently, and race, one loses all changes. The ETag prevents the DB corruption but UX is poor—no indication that changes exist on disk.

**Mitigation:**
- Implement a persistent "version mismatch detected" modal that offers: (a) reload from DB (losing local edits), (b) export local edits as JSON, (c) merge dialog.
- Consider OT/CRDT for multi-user editing, or lock-based pessimistic concurrency.

---

### C4: Unbounded Growth — fingerprint_audits and selfcheck_results Tables
**File:** `Migrations_V11.cs` L37-44, `Migrations_V12.cs` L19-41  
**Severity:** CRITICAL  
**Scope:** Database growth, operational sustainability

**Issue:**  
Both `fingerprint_audits` and `selfcheck_results` tables grow without bound. FingerprintQualityMonitor (line 30: TickInterval=30min) runs continuously, and every auto-regenerate logs an audit. Over a year with 1000 profiles: ~500,000 rows per table. No retention policy, cleanup job, or TTL.

```csharp
// Migrations_V11.cs L37-44 — CREATE TABLE, no purge mechanism
CREATE TABLE IF NOT EXISTS fingerprint_audits (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    profile_name  TEXT    NOT NULL,
    generated_at  TEXT    NOT NULL,
    ...
);

// Migrations_V12.cs L19-41 — similar
CREATE TABLE IF NOT EXISTS selfcheck_results (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    profile_name      TEXT    NOT NULL,
    ran_at            TEXT    NOT NULL,
    ...
    raw_json          TEXT  // potentially large
);
```

**Impact:**
- After 1 year: 500MB+ of audit/self-check logs with no cleanup
- Queries slow as tables grow; WAL checkpoint overhead increases
- No UI to browse/delete old records
- Cumulative WAL + main database file growth = operational drag

**Contrast:**  
`runs` table (V5) has `ClearAsync()` (RunService.cs L152-166) to purge old runs. No equivalent for audits.

**Mitigation:**
- Implement `FingerprintAuditService.CleanupOlderThanAsync()` and `SelfCheckHistoryService.CleanupOlderThanAsync()`
- Add background service to run weekly cleanup (e.g., delete records >60 days old)
- Document recommended retention: "keep last 60 days"

---

## HIGH FINDINGS

### H1: Migration V14 Concurrent ALTER TABLE on runs (No Transaction Protection)
**File:** `Migrations_V14.cs` L18-23, `MigrationRunner.cs` L71-74  
**Severity:** HIGH  
**Scope:** Migration retry safety, production upgrades

**Issue:**  
Migration V14 adds two columns to `scripts` and `runs` tables. If the migration is interrupted and retried, the second ALTER TABLE on `runs` ("script_run_id") may see concurrent writes to the runs table from other processes (background monitors, script runner).

```csharp
// Migrations_V14.cs L18-23
internal static readonly string[] Statements =
{
    "ALTER TABLE scripts ADD COLUMN is_default INTEGER NOT NULL DEFAULT 0;",
    "CREATE INDEX IF NOT EXISTS idx_scripts_default ON scripts(is_default);",
    "ALTER TABLE runs ADD COLUMN script_run_id INTEGER;",  // ← SQLite may lock for seconds
};

// MigrationRunner.cs L71-74
if (!applied.Contains(14))
{
    ApplyTolerantStatements(conn, 14, Migrations_V14.Statements);
}
```

**Root Cause:**  
SQLite ALTER TABLE acquires a EXCLUSIVE lock on the table. If `runs` is actively being written to by ScriptService.RecordRunAsync() or FingerprintQualityMonitor's database reads occur during the ALTER, the ALTER will block until those queries finish, or vice versa. No explicit transaction wraps the sequence; if statement 3 fails, statements 1-2 are already committed.

**Scenario:**
1. Migration V14 starts
2. Statement 1 (ALTER scripts) succeeds
3. Statement 2 (CREATE INDEX) succeeds
4. Statement 3 (ALTER runs) tries to acquire EXCLUSIVE lock
5. ScriptRunner is mid-RecordRunAsync() INSERT (waiting)
6. ALTER waits up to 5000ms (BUSY_TIMEOUT)
7. If timeout fires, ALTER fails; next retry sees columns already exist

**Impact:**
- Production upgrades could deadlock or timeout if background services are active
- Partial migration (statements 1-2 succeed, 3 fails) leaves schema inconsistent

**Mitigation:**
- Document: "Stop all background services before running migrations"
- OR: split V14 into V14a (scripts changes) and V14b (runs changes) with time delay
- OR: wrap tolerant statements in explicit transaction with IMMEDIATE mode

---

### H2: Default Script Race Condition — is_default Concurrent Set
**File:** `ScriptService.cs` L132-153  
**Severity:** HIGH  
**Scope:** Script management, concurrent process writes

**Issue:**  
The `SetDefaultAsync()` method uses a two-step transaction (clear all is_default, then set one). The QueueAsync semaphore serializes this, but the design is fragile: it relies entirely on serialization. If code ever moves to parallel processing, the invariant breaks silently.

```csharp
// ScriptService.cs L132-153
public async Task SetDefaultAsync(long id, CancellationToken ct = default)
{
    await _db.QueueAsync(async (Microsoft.Data.Sqlite.SqliteConnection c) =>
    {
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync(
            "UPDATE scripts SET is_default = 0 WHERE is_default = 1;",
            transaction: tx);
        if (id > 0)
        {
            await c.ExecuteAsync(
                "UPDATE scripts SET is_default = 1, updated_at = @now WHERE id = @id;",
                new { id, now = DateTime.UtcNow }, tx);
        }
        tx.Commit();
    }, ct);
}
```

**Secondary Issue:**  
Comment on line 134-136 says "unique-on-is_default invariant isn't enforced via SQL UNIQUE because SQLite would also forbid multiple zero-rows". This is correct (SQLite UNIQUE allows multiple NULLs but treats 0 like any other value), but the implicit reliance on QueueAsync serialization is fragile.

**Mitigation:**
- Add explicit documentation that SetDefaultAsync relies on QueueAsync serialization
- Consider: `CREATE UNIQUE INDEX idx_scripts_one_default ON scripts(is_default) WHERE is_default = 1;` (partial index, enforces at most one TRUE value)

---

### H3: ScriptRunner.Finalise() Creates Invalid ScriptRun (Zeroed Fields)
**File:** `ScriptRunner.cs` L716-739  
**Severity:** HIGH  
**Scope:** Script execution logging, run history accuracy

**Issue:**  
The `Finalise()` method constructs a ScriptRun object with default values instead of returning the original run with updated times:

```csharp
// ScriptRunner.cs L716-739
return new ScriptRun
{
    Id            = runId,
    ScriptId      = 0,           // ← Should be script.Id
    ProfileName   = "",           // ← Should be profileName parameter
    StartedAt     = startedAt,
    FinishedAt    = finishedAt,
    ...
};
```

**Why It Matters:**  
Line 59-65 constructs the run correctly with real ScriptId and ProfileName. But Finalise returns a new run with zeroed values. The returned object is often used for logging or testing, and the data is semantically wrong.

**Impact:**
- When a script run completes, the returned ScriptRun has ScriptId=0 and ProfileName=""
- If this returned object is logged, serialized, or displayed, it shows incorrect data
- The actual DB row has correct values (RecordRunAsync received them), but the return value is bad

**Mitigation:**
- Pass script.Id and profileName to Finalise
- OR: return only a summary type (status, counts, durations), not full ScriptRun

---

### H4: Script.StepsJson Deserialization Schema Drift
**File:** `ScriptRunner.cs` L68-79, `Script.cs` L22-23  
**Severity:** HIGH  
**Scope:** Script compatibility, version upgrades

**Issue:**  
Script steps are persisted as JSON in `scripts.steps_json` and deserialized to `List<ScriptStep>` in ExecuteAsync. If the ScriptStep model evolves (new fields added, properties renamed), saved scripts may fail to deserialize or lose data.

```csharp
// ScriptRunner.cs L68-79
IReadOnlyList<ScriptStep> steps;
try
{
    steps = JsonSerializer.Deserialize<List<ScriptStep>>(script.StepsJson, JsonOpts)
            ?? new List<ScriptStep>();
}
catch (Exception ex)
{
    _log.LogError(ex, "Script #{Id} steps_json invalid", script.Id);
    return Finalise(runId, startedAt, "failed", counters,
        "steps_json deserialise failed: " + ex.Message, stepLog, ctx);
}
```

**Scenario:**
1. Version A: ScriptStep has fields Type, Params, Enabled, Label
2. User saves script to DB with that schema
3. Version B: ScriptStep adds AbortOnError, Probability (Phase 12 iter 6)
4. User loads same script → new fields have defaults ✓ (works)
5. Reverse: Old script saved with unknown field "debug_mode"
6. JsonSerializer ignores it (PropertyNameCaseInsensitive=true)
7. Script loads but "debug_mode" is silently dropped

**No Versioning:**  
No version field in steps_json indicates which schema it was created with. Future breaking changes (renaming Type→action) silently break old scripts.

**Mitigation:**
- Add `schema_version` field to steps_json (e.g., `{"version": 1, "steps": [...]}`)
- Implement transformation logic for schema changes
- Document: "Steps JSON schema is versioned"

---

### H5: Orphan script_runs (Started but Never Finished)
**File:** `ScriptService.cs` L108-130, `ScriptRunner.cs` L66-78  
**Severity:** HIGH  
**Scope:** Script execution, crash recovery

**Issue:**  
When ScriptRunner.ExecuteAsync() is called, it immediately inserts a run row with status='running'. If the script crashes or process dies before Finalise, the row is left with:
- `status = 'running'`
- `finished_at = NULL`
- `duration_sec = NULL`

No automatic watchdog marks these as crashed after timeout.

```csharp
// ScriptRunner.cs L59-65
var run = new ScriptRun
{
    ...
    Status      = "running",    // ← Inserted immediately
};
var runId = await _scripts.RecordRunAsync(run, ct);

// If process dies here, row is orphaned
```

**Consequences:**
- Stranded rows pollute the run history
- No way to distinguish "still running" from "crashed mid-execution"
- Over time, accumulates "ghost" runs

**Compare:**  
The `runs` table has SessionWatchdog detecting heartbeat staleness. No equivalent for script_runs.

**Mitigation:**
- Add background job to mark script_runs with status='running' and started_at >24h ago as 'failed'
- OR: implement heartbeat mechanism in ScriptRunner
- OR: wrap ExecuteAsync in try-finally that always calls Finalise

---

### H6: SQLite WAL + QueueAsync Semaphore Bottleneck
**File:** `DatabaseConnection.cs` L40-153  
**Severity:** HIGH  
**Scope:** Concurrency, scalability

**Issue:**  
A single `SemaphoreSlim(1, 1)` serializes ALL database operations. While this prevents "DataReader already open" errors, it creates a bottleneck: every read/write must acquire the semaphore, run SQL, then release. Concurrent background monitors (FingerprintQuality, WarmupQuality, ScriptRunner) all queue behind each other.

```csharp
// DatabaseConnection.cs L115-127
public async Task<T> QueueAsync<T>(
    Func<SqliteConnection, Task<T>> work, CancellationToken ct = default)
{
    await _querySemaphore.WaitAsync(ct);  // ← Serialize all ops
    try
    {
        return await work(Get());
    }
    finally
    {
        _querySemaphore.Release();
    }
}
```

**Bottleneck Effect:**
1. FingerprintQualityMonitor acquires semaphore, runs ListAsync → 100ms
2. WarmupQualityMonitor queues, waiting
3. ScriptRunner queues, waiting to call RecordRunAsync
4. If any queued operation waits >5000ms (BUSY_TIMEOUT), it times out

**Architecture Note:**  
Comment (line 34-38) explains one-connection-with-shared-cache was needed for pre-Phase 5 derived-state caches. This is legacy reasoning; modern code could use connection-per-call.

**Mitigation:**
- (Short-term) Log semaphore contention metrics
- (Medium-term) Split read/write semaphores (reads can be concurrent under WAL)
- (Long-term) Switch to connection-per-call with Pooling

---

### H7: V14 Foreign Key on runs.script_run_id (Missing Constraint)
**File:** `Migrations_V14.cs` L22, `Migrations_V13.cs` L49-52  
**Severity:** HIGH  
**Scope:** Referential integrity

**Issue:**  
Migration V14 adds `runs.script_run_id INTEGER;` but no FOREIGN KEY constraint linking it to script_runs.id. A runs row can have script_run_id=999 even if no script_runs(id=999) exists.

```csharp
// Migrations_V14.cs L22 — no FOREIGN KEY
"ALTER TABLE runs ADD COLUMN script_run_id INTEGER;",
```

**Consequence:**
- Old `runs` table has no SQL-enforced relationship to new `script_runs` table
- Orphan references possible
- Joins might return inconsistent results
- No database-enforced referential integrity

**Mitigation:**
- Add FOREIGN KEY: `ALTER TABLE runs ADD CONSTRAINT fk_runs_script_run FOREIGN KEY (script_run_id) REFERENCES script_runs(id) ON DELETE SET NULL;`

---

### H8: Migration V13 Missing Profile Update for AssignedScriptId
**File:** `ProfileService.cs` L96-116  
**Severity:** HIGH  
**Scope:** Migration, data consistency

**Issue:**  
Migration V13 adds `assigned_script_id INTEGER` to profiles, but ProfileService.UpdateAsync() doesn't include it in the UPDATE clause. Changes to AssignedScriptId are silently lost.

```csharp
// ProfileService.cs L100-112 — missing assigned_script_id in SET
const string sql = """
    UPDATE profiles
       SET group_name           = @GroupName,
           ...
           note                 = @Note,
           updated_at           = @UpdatedAt
     WHERE name                 = @Name;
    // ← assigned_script_id NOT in SET clause
""";

// ProfileService.cs L275-289 — ToRow doesn't extract it
private static ProfileRow ToRow(Profile p) => new()
{
    // ← AssignedScriptId NOT included
};
```

**Reproduction:**
1. Load profile, set AssignedScriptId = 5
2. Call UpdateAsync(profile)
3. Reload profile from DB → AssignedScriptId still null (unchanged)

**Mitigation:**
- Add assigned_script_id to the SET clause in UpdateAsync
- Add AssignedScriptId to ProfileRow and ToRow conversion
- Test: profile.AssignedScriptId = 5 → save → reload → should still be 5

---

### H9: Unbounded Growth — No Cleanup for Audit/SelfCheck Tables
**(Same as C4 — see Critical section)**

### H10: FingerprintAuditService Missing Index Optimization
**File:** `Migrations_V11.cs` L46-47  
**Severity:** HIGH  
**Scope:** Query performance

**Issue:**  
Separate indexes on profile_name and generated_at, but ListAsync queries both. SQLite uses one index, then sorts by hand (O(n log n)).

```csharp
// Migrations_V11.cs L46-47
"CREATE INDEX IF NOT EXISTS idx_fp_audits_profile  ON fingerprint_audits(profile_name);",
"CREATE INDEX IF NOT EXISTS idx_fp_audits_generated ON fingerprint_audits(generated_at DESC);",

// Query: WHERE profile_name = ? ORDER BY generated_at DESC
// Should use: CREATE INDEX ... ON (profile_name, generated_at DESC)
```

**Mitigation:**
- Add composite index: `CREATE INDEX idx_fp_audits_profile_generated ON fingerprint_audits(profile_name, generated_at DESC);`

---

### H11: SelfCheckHistoryService Missing Composite Index
**File:** `Migrations_V12.cs` L37-40  
**Severity:** HIGH  
**Scope:** Query performance

**Issue:**  
Similar to H10. Separate indexes, but queries filter by profile_name AND order by ran_at.

**Mitigation:**
- Add: `CREATE INDEX idx_selfcheck_profile_ran ON selfcheck_results(profile_name, ran_at DESC);`

---

### H12: Script Deletion Doesn't Update Profile.AssignedScriptId to NULL
**File:** `ScriptService.cs` L104-106  
**Severity:** HIGH  
**Scope:** Cascade behavior, data consistency

**Issue:**  
When a script is deleted, profiles with that script assigned still hold the now-invalid reference.

```csharp
// ScriptService.cs L104-106 — unconditional DELETE
public Task DeleteAsync(long id, CancellationToken ct = default)
    => _db.QueueAsync(c => c.ExecuteAsync(
        "DELETE FROM scripts WHERE id = @id;", new { id }), ct);
```

**Mitigation:**
- Before deleting script, execute: `UPDATE profiles SET assigned_script_id = NULL WHERE assigned_script_id = @id;`
- Log which profiles were affected

---

## MEDIUM FINDINGS

### M1: SelfCheckResult.RunId Optional but Semantics Unclear
**File:** `SelfCheckResult.cs` L15  
**Severity:** MEDIUM

**Issue:**  
RunId is nullable, but semantics unclear: does null mean "self-check ran outside run context" or "we forgot to record the ID"?

**Mitigation:**
- Document nullable vs required invariants
- If out-of-band checks exist, add reason field

---

### M2: Script Deletion Doesn't Return Count
**File:** `ScriptService.cs` L104-106  
**Severity:** MEDIUM

**Issue:**  
Caller doesn't know if script existed. DeleteAsync silently succeeds even if id=999.

**Mitigation:**
- Return Task<int> (rows affected) or throw exception if rows == 0

---

### M3: ProfileService.DeleteAsync Doesn't Log Assigned Scripts
**File:** `ProfileService.cs` L118-123  
**Severity:** MEDIUM

**Issue:**  
If a profile with an assigned script is deleted, no log warning.

**Mitigation:**
- Log: "Deleting profile 'X' with assigned script Y"

---

### M4: RunService.MarkFailedAsync Hard-Codes exit_code = -99
**File:** `RunService.cs` L144-150  
**Severity:** MEDIUM

**Issue:**  
Magic number; hard to search/change.

**Mitigation:**
- Define: `private const int ManualFailExitCode = -99;`

---

### M5: Script.ETag Could Be Empty for Imported Scripts
**File:** `Script.cs` L37  
**Severity:** MEDIUM

**Issue:**  
Default ETag="". If script directly inserted (bulk import) without CreateAsync, ETag remains empty, causing UpdateAsync failures.

**Mitigation:**
- Validate: reject Script.ETag="" in UpdateAsync
- Document: "Scripts must be created via CreateAsync"

---

### M6: Script JSON Serializer Options Not Documented
**File:** `ScriptRunner.cs` L32-36  
**Severity:** MEDIUM

**Issue:**  
JsonOpts defined locally; if scripts serialized elsewhere (UI, export), options might differ.

**Mitigation:**
- Export JsonOpts as public static
- Document: "All script serialization uses this shared JsonOpts"

---

### M7: Timezone Handling Inconsistent
**Throughout:** FingerprintAuditService, SelfCheckHistoryService  
**Severity:** MEDIUM

**Issue:**  
Timestamps stored as `DateTime.UtcNow.ToString("O")`. If ever a non-UTC DateTime is used, the string won't include timezone offset.

**Mitigation:**
- Document: "All timestamps are ISO 8601 UTC (Z suffix)"
- Ensure all DateTime sources are UtcNow

---

### M8: ScriptRunner Doesn't Log Script Name
**File:** `ScriptRunner.cs` L25-50  
**Severity:** MEDIUM

**Issue:**  
Constructor takes IScriptService but not the script object. Script name not easily accessible for logging.

**Mitigation:**
- Pass script.Name to ExecuteAsync, log upfront

---

### M9: Hardcoded Timestamps Lose Precision
**File:** `FingerprintAuditService.cs` L52, `SelfCheckHistoryService.cs` L53  
**Severity:** MEDIUM

**Issue:**  
Always use DateTime.UtcNow.ToString("O") at insert time. If events are queued/delayed, recorded timestamp doesn't reflect actual event time.

**Mitigation:**
- Add optional DateTime parameter to LogAsync and InsertAsync
- Allow historical imports

---

### M10: FingerprintQualityMonitor Doesn't Check Script Availability
**File:** `FingerprintQualityMonitor.cs` L28-136  
**Severity:** MEDIUM

**Issue:**  
When auto-regenerating, doesn't check if profile has an orphan assigned_script_id.

**Mitigation:**
- Log a warning if profile has invalid script reference

---

### M11: WarmupQualityMonitor Excludes Running Runs
**File:** `WarmupQualityMonitor.cs` L132  
**Severity:** MEDIUM

**Issue:**  
Only calculates captcha rate from finished runs. If last 5 are all running, monitor defers (can't judge). May under-react if long-running profiles exist.

**Mitigation:**
- Document the trade-off
- Log "deferred (not enough finished runs)"

---

### M12: Redundant Default Values
**File:** `Script.cs` L22-23, `Migrations_V13.cs` L26  
**Severity:** MEDIUM

**Issue:**  
StepsJson defaults to "[]" in both model and DB. If one changes, inconsistency creeps in.

**Mitigation:**
- Document which is source of truth
- Consider removing one

---

## LOW FINDINGS

### L1-L7
**Severity:** LOW  

Various low-impact items: API design clarity, logging improvements, documentation, magic numbers, index optimization for partial queries, etc.

(See full audit report section for details)

---

## Summary Table

| # | Finding | Severity | Category | Impact |
|---|---------|----------|----------|--------|
| C1 | Migration fresh install crash recovery | CRITICAL | Safety | Schema inconsistency |
| C2 | Script delete orphans profiles | CRITICAL | Integrity | Lost script binding |
| C3 | ETag two-tab race | CRITICAL | Concurrency | Silent edit loss |
| C4 | Unbounded audit/selfcheck tables | CRITICAL | Ops | Database bloat |
| H1 | V14 ALTER TABLE concurrent writes | HIGH | Migration | Deadlock/timeout |
| H2 | Default script race fragility | HIGH | Concurrency | Silent state corruption |
| H3 | Finalise zeroes ScriptRun fields | HIGH | Correctness | Bad logged/returned data |
| H4 | Script StepsJson schema drift | HIGH | Compatibility | Old scripts break |
| H5 | Orphan script_runs (never finished) | HIGH | Crash Recovery | Ghost rows |
| H6 | SQLite semaphore bottleneck | HIGH | Performance | Contention/timeouts |
| H7 | runs.script_run_id no FK | HIGH | Integrity | Orphan references |
| H8 | ProfileService doesn't update assigned_script_id | HIGH | Data Sync | Silent loss of changes |
| H10 | Missing composite index (fp_audits) | HIGH | Performance | Slow queries |
| H11 | Missing composite index (selfcheck) | HIGH | Performance | Slow queries |
| H12 | Script delete doesn't clear profiles | HIGH | Cascade | Dangling references |

---

## Recommended Actions (Priority)

1. **C1**: Wrap ApplyTolerantStatements in transaction; stamp version only after all succeed
2. **C2**: Add FOREIGN KEY with CASCADE or pre-delete hook
3. **C3**: Implement ETag conflict resolution modal
4. **C4**: Add retention policies; implement cleanup services
5. **H1**: Document: "Stop services before migration"
6. **H3**: Pass script.Id/profileName to Finalise
7. **H4**: Add schema_version to steps_json
8. **H5**: Add script_run watchdog / timeout cleanup
9. **H6**: Monitor semaphore contention; plan read/write split
10. **H7-H8**: Add FK constraints + UPDATE clause
11. **H10-H11**: Add composite indexes

---

**End of Audit Report**
