// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Core.Vault;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>
/// Phase 24 — credential vault implementation. Manages master-key
/// lifecycle (initialize / unlock / lock), encrypts secrets with
/// AES-GCM under a key derived from the user's passphrase, and
/// exposes CRUD over <c>vault_items</c>.
///
/// Thread-safety: the in-memory key + lock state are guarded by a
/// <see cref="SemaphoreSlim"/>. DB access goes through the existing
/// <see cref="DatabaseConnection.QueueAsync"/> serialiser, so vault
/// reads/writes don't fight other tables for the SQLite connection.
///
/// LOCK-ORDER INVARIANT (Phase 26 audit codification):
///   _gate (this class)  →  DatabaseConnection.QueueAsync semaphore
/// All vault paths that hold both acquire them in this order.
/// DatabaseConnection's queue MUST NOT call back into any vault method
/// that requires _gate (it currently doesn't), or a reverse-order
/// deadlock becomes possible.
/// </summary>
internal sealed class VaultService : IVaultService, IDisposable
{
    private const string CfgSalt        = "vault.salt";
    private const string CfgVerifier    = "vault.verifier";
    private const string CfgInitAt      = "vault.initialized_at";
    // Phase 26 — auto-lock idle timeout (minutes). 0 = disabled.
    // Stored as ASCII bytes in vault_config so it survives restarts.
    private const string CfgAutoLockMin = "vault.auto_lock_min";
    private const int    DefaultAutoLockMinutes = 15;

    private readonly DatabaseConnection _db;
    private readonly ILogger<VaultService> _log;

    private readonly SemaphoreSlim _gate = new(1, 1);
    // Phase 24 audit fix #1, #9 — volatile so reads on other threads
    // see the most-recent assignment without stale CPU-register
    // caching. The byte[] CONTENTS are mutated by ZeroMemory; volatile
    // applies to the REFERENCE assignment which is what IsUnlocked
    // reads. Field-level reads are inherently atomic for references.
    private volatile byte[]? _key;     // null = locked
    private bool _initialized;

    public bool IsInitialized => _initialized;
    public bool IsUnlocked    => _key is not null;

    public event EventHandler? LockStateChanged;

    // Phase 26 — idle-tracking. Volatile because the watcher reads it
    // from a DispatcherTimer thread while mouse/key handlers (UI thread)
    // and vault CRUD (worker thread) bump it concurrently.
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    public DateTime LastActivityUtc => new DateTime(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc);
    public void NotifyActivity()
        => Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    public VaultService(DatabaseConnection db, ILogger<VaultService> log)
    {
        _db  = db;
        _log = log;
    }

    public async Task<bool> RefreshStateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var salt = await ReadConfigBytesAsync(CfgSalt, ct);
            var verifier = await ReadConfigBytesAsync(CfgVerifier, ct);
            _initialized = salt is not null && verifier is not null;
            return _initialized;
        }
        finally { _gate.Release(); }
    }

    public async Task InitializeAsync(string masterPassphrase, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(masterPassphrase))
            throw new ArgumentException("master passphrase is required", nameof(masterPassphrase));
        await _gate.WaitAsync(ct);
        try
        {
            var existingSalt = await ReadConfigBytesAsync(CfgSalt, ct);
            if (existingSalt is not null)
                throw new InvalidOperationException(
                    "vault already initialized — use ResetAsync to wipe + re-init");

            var salt     = VaultCrypto.NewSalt();
            var key      = VaultCrypto.DeriveKey(masterPassphrase, salt);
            var verifier = VaultCrypto.EncryptString(key, VaultCrypto.VerifierPlain);

            await WriteConfigBytesAsync(CfgSalt,     salt,                                    ct);
            await WriteConfigBytesAsync(CfgVerifier, verifier,                                ct);
            await WriteConfigBytesAsync(CfgInitAt,
                Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O")),                        ct);

            _key = key;
            _initialized = true;
            _log.LogInformation("Vault initialized");
        }
        finally { _gate.Release(); }
        OnLockStateChanged();
    }

    public async Task<bool> UnlockAsync(string masterPassphrase, CancellationToken ct = default)
    {
        // Phase 24 audit fix — explicit null/empty rejection so the
        // caller can't accidentally derive an empty-string key against
        // a real salt (matches InitializeAsync's contract).
        if (string.IsNullOrEmpty(masterPassphrase)) return false;
        bool unlocked = false;
        await _gate.WaitAsync(ct);
        try
        {
            var salt     = await ReadConfigBytesAsync(CfgSalt,     ct);
            var verifier = await ReadConfigBytesAsync(CfgVerifier, ct);
            if (salt is null || verifier is null)
            {
                _initialized = false;
                // Fall through — unlocked stays false.
            }
            else
            {
                _initialized = true;
                var key = VaultCrypto.DeriveKey(masterPassphrase ?? "", salt);
                try
                {
                    var plain = VaultCrypto.DecryptString(key, verifier);
                    if (plain == VaultCrypto.VerifierPlain)
                    {
                        _key = key;
                        unlocked = true;
                        NotifyActivity();
                        _log.LogInformation("Vault unlocked");
                    }
                    // else: wrong passphrase, leave unlocked = false
                }
                catch (CryptographicException)
                {
                    // Wrong passphrase — auth tag mismatch.
                }
            }
        }
        finally { _gate.Release(); }
        // Phase 24 hot-fix: fire LockStateChanged AFTER releasing the
        // gate so subscribers don't deadlock if they re-enter the
        // service. We only fire when the unlock actually succeeded
        // (caller checks return value already, but this matches the
        // "state changed" contract precisely). Phase 26 build fix:
        // refactored to a single fall-through return so this line is
        // actually reachable (CS0162 was warning "unreachable code").
        if (unlocked) OnLockStateChanged();
        return unlocked;
    }

    public void Lock()
    {
        bool changed;
        _gate.Wait();
        try
        {
            changed = _key is not null;
            if (_key is not null)
            {
                CryptographicOperations.ZeroMemory(_key);
                _key = null;
            }
        }
        finally { _gate.Release(); }
        if (changed)
        {
            _log.LogInformation("Vault locked");
            OnLockStateChanged();
        }
    }

    public async Task ResetAsync(string currentPassphrase, CancellationToken ct = default)
    {
        // Require the current password (if initialized) so a stray
        // click on Reset doesn't nuke everything.
        if (await RefreshStateAsync(ct))
        {
            var ok = await UnlockAsync(currentPassphrase, ct);
            if (!ok)
                throw new UnauthorizedAccessException("current passphrase doesn't match");
        }

        await _db.QueueAsync(async c =>
        {
            using var tx = c.BeginTransaction();
            await c.ExecuteAsync("DELETE FROM vault_items;",  transaction: tx);
            await c.ExecuteAsync("DELETE FROM vault_config;", transaction: tx);
            tx.Commit();
            return 0;
        }, ct);
        Lock();
        _initialized = false;
        _log.LogWarning("Vault reset (all entries + config wiped)");
        OnLockStateChanged();
    }

    // ─── CRUD ─────────────────────────────────────────────────────

    private const string SelectColumns = """
        id, name, kind, service, identifier,
        secrets_enc       AS SecretsEnc,
        profile_name      AS ProfileName,
        status,
        tags_json         AS TagsJson,
        notes,
        email,
        field_meta_json   AS FieldMetaJson,
        extras_json       AS ExtrasJson,
        last_used_at      AS LastUsedAt,
        last_login_at     AS LastLoginAt,
        last_login_status AS LastLoginStatus,
        created_at        AS CreatedAt,
        updated_at        AS UpdatedAt
    """;

    public async Task<IReadOnlyList<VaultItem>> ListAsync(
        string? kind = null, string? service = null, string? status = null,
        string? profileName = null, string? search = null,
        CancellationToken ct = default)
    {
        var where = new List<string>();
        var args  = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(kind))         { where.Add("kind = @kind");                args.Add("kind", kind); }
        if (!string.IsNullOrWhiteSpace(service))      { where.Add("service = @svc");              args.Add("svc", service); }
        if (!string.IsNullOrWhiteSpace(status))       { where.Add("status = @st");                args.Add("st", status); }
        if (!string.IsNullOrWhiteSpace(profileName))  { where.Add("profile_name = @pn");          args.Add("pn", profileName); }
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Cheap LIKE across the metadata-only fields. Notes/tags
            // are also included so users can find stuff by free-form
            // labels without unlocking the vault.
            // Phase 71 — also search by email + extras_json so the
            // unified item's plaintext custom fields show up in
            // free-text filtering. SecretsEnc + encrypted custom fields
            // remain unsearchable (they're ciphertext at rest).
            where.Add("(name LIKE @q OR identifier LIKE @q OR email LIKE @q OR " +
                      "notes LIKE @q OR tags_json LIKE @q OR extras_json LIKE @q)");
            args.Add("q", $"%{search}%");
        }
        // Phase 24 audit fix — bounded result set so a vault with
        // thousands of items can't OOM the UI. Default 500; users
        // who need more get pagination via filters (kind / search).
        // The constant lives here rather than as a parameter because
        // every UI surface today wants the same cap; if we ever ship
        // a "browse-all" panel we can plumb the limit through then.
        const int HardLimit = 500;
        var sql = $"SELECT {SelectColumns} FROM vault_items"
                  + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                  + " ORDER BY updated_at DESC LIMIT @limit;";
        args.Add("limit", HardLimit);
        var rows = await _db.QueueAsync(c => c.QueryAsync<VaultItem>(sql, args), ct);
        return rows.ToList();
    }

    public async Task<VaultItem?> GetAsync(long id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM vault_items WHERE id = @id;";
        return await _db.QueueAsync(
            c => c.QuerySingleOrDefaultAsync<VaultItem>(sql, new { id }), ct);
    }

    public async Task<(VaultItem item, IReadOnlyDictionary<string, string> clear)?>
        GetClearAsync(long id, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct);
        if (item is null) return null;
        // Phase 24 audit fix #3 — gate-protect the decrypt so a Lock()
        // racing with this read can't yank _key between the check
        // and the use.
        await _gate.WaitAsync(ct);
        try
        {
            EnsureUnlockedInternal();
            var clear = DecryptSecrets(item.SecretsEnc);
            // Phase 71 — merge plaintext extras into the cleartext bag
            // so callers see a single uniform view of the item's fields,
            // regardless of which storage column each value lives in.
            // Encrypted keys take precedence over extras on collision —
            // a key marked encrypted should never read from plaintext.
            var merged = MergeWithExtras(clear, item.ExtrasJson);
            return (item, merged);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Phase 71 — merge plaintext extras_json values onto the
    /// decrypted secrets bag. Encrypted-bag entries take precedence
    /// on key collision so a secret never reads from a plaintext
    /// shadow. Returns a NEW dictionary (callers may mutate without
    /// affecting the cached ciphertext).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> MergeWithExtras(
        IReadOnlyDictionary<string, string> encryptedClear, string? extrasJson)
    {
        var merged = new Dictionary<string, string>(encryptedClear, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(extrasJson)) return merged;
        try
        {
            using var doc = JsonDocument.Parse(extrasJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return merged;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (merged.ContainsKey(prop.Name)) continue;   // encrypted wins
                var v = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True   => "true",
                    JsonValueKind.False  => "false",
                    JsonValueKind.Null   => "",
                    _                    => prop.Value.GetRawText(),
                };
                merged[prop.Name] = v;
            }
        }
        catch (JsonException)
        {
            // Corrupt extras_json — ignore, return what we have.
        }
        return merged;
    }

    public async Task<VaultItem> CreateAsync(
        VaultItem item,
        IReadOnlyDictionary<string, string> clearSecrets,
        CancellationToken ct = default)
    {
        // Phase 24 audit fix — kind + status whitelist guards. Without
        // these the DB happily stores rows nothing can render.
        if (!VaultKinds.IsValidKind(item.Kind))
            throw new ArgumentException(
                $"Unknown vault kind '{item.Kind}' — must be one of: "
                    + string.Join(", ", VaultKinds.All), nameof(item));
        if (!VaultKinds.IsValidStatus(item.Status))
            throw new ArgumentException(
                $"Unknown vault status '{item.Status}' — must be one of: "
                    + string.Join(", ", VaultKinds.Statuses), nameof(item));
        if (string.IsNullOrWhiteSpace(item.Name))
            throw new ArgumentException("vault item name is required", nameof(item));

        // Phase 24 audit fix #2 — encrypt under the gate so a Lock()
        // racing with CreateAsync can't yank _key between the check
        // and the use. EncryptSecrets reads _key directly.
        byte[]? encrypted;
        await _gate.WaitAsync(ct);
        try
        {
            EnsureUnlockedInternal();
            encrypted = EncryptSecrets(clearSecrets);
        }
        finally { _gate.Release(); }
        var now = DateTime.UtcNow;
        // Phase 24 audit fix #4 — every timestamp goes to TEXT columns
        // as ISO 8601 ("O" format) so reads / sorts / range queries
        // work consistently across locales. Bare DateTime.ToString()
        // uses CurrentCulture which breaks on non-en-US machines.
        var nowIso = now.ToString("O");
        const string sql = """
            INSERT INTO vault_items
              (name, kind, service, identifier, secrets_enc,
               profile_name, status, tags_json, notes,
               email, field_meta_json, extras_json,
               created_at, updated_at)
            VALUES
              (@Name, @Kind, @Service, @Identifier, @SecretsEnc,
               @ProfileName, @Status, @TagsJson, @Notes,
               @Email, @FieldMetaJson, @ExtrasJson,
               @CreatedAt, @UpdatedAt)
            RETURNING id;
        """;
        var id = await _db.QueueAsync(c => c.ExecuteScalarAsync<long>(sql, new
        {
            item.Name, item.Kind, item.Service, item.Identifier,
            SecretsEnc  = encrypted,
            item.ProfileName, item.Status, item.TagsJson, item.Notes,
            // Phase 71 — universal-vault columns. All nullable; null on
            // legacy items and on universal items that haven't added
            // custom fields yet.
            item.Email, item.FieldMetaJson, item.ExtrasJson,
            CreatedAt = nowIso, UpdatedAt = nowIso,
        }), ct);
        _log.LogInformation("Vault item #{Id} '{Name}' created (kind={Kind})",
            id, item.Name, item.Kind);
        return item with { Id = id, SecretsEnc = encrypted, CreatedAt = now, UpdatedAt = now };
    }

    public async Task UpdateAsync(
        VaultItem item,
        IReadOnlyDictionary<string, string>? clearSecrets,
        CancellationToken ct = default)
    {
        // Phase 24 audit fix — same kind/status whitelist as create.
        if (!VaultKinds.IsValidKind(item.Kind))
            throw new ArgumentException(
                $"Unknown vault kind '{item.Kind}'", nameof(item));
        if (!VaultKinds.IsValidStatus(item.Status))
            throw new ArgumentException(
                $"Unknown vault status '{item.Status}'", nameof(item));

        // Phase 24 audit fix #2 — same gate-protected encrypt as
        // CreateAsync. The clearSecrets=null path skips encryption
        // entirely and preserves existing ciphertext.
        byte[]? encrypted;
        if (clearSecrets is null)
        {
            EnsureUnlocked();
            encrypted = item.SecretsEnc;
        }
        else
        {
            await _gate.WaitAsync(ct);
            try
            {
                EnsureUnlockedInternal();
                encrypted = EncryptSecrets(clearSecrets);
            }
            finally { _gate.Release(); }
        }
        const string sql = """
            UPDATE vault_items SET
              name            = @Name,
              kind            = @Kind,
              service         = @Service,
              identifier      = @Identifier,
              secrets_enc     = @SecretsEnc,
              profile_name    = @ProfileName,
              status          = @Status,
              tags_json       = @TagsJson,
              notes           = @Notes,
              email           = @Email,
              field_meta_json = @FieldMetaJson,
              extras_json     = @ExtrasJson,
              updated_at      = @UpdatedAt
            WHERE id = @Id;
        """;
        await _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            item.Id, item.Name, item.Kind, item.Service, item.Identifier,
            SecretsEnc  = encrypted,
            item.ProfileName, item.Status, item.TagsJson, item.Notes,
            // Phase 71 — universal-vault columns.
            item.Email, item.FieldMetaJson, item.ExtrasJson,
            // Phase 24 audit fix #4 — ISO 8601 string for TEXT column.
            UpdatedAt = DateTime.UtcNow.ToString("O"),
        }), ct);
        _log.LogInformation("Vault item #{Id} updated", item.Id);
    }

    public Task DeleteAsync(long id, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM vault_items WHERE id = @id;", new { id }), ct);

    public Task TouchUsedAsync(long id, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "UPDATE vault_items SET last_used_at = @now WHERE id = @id;",
            new { now = DateTime.UtcNow.ToString("O"), id }), ct);

    // ─── Script integration ──────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>
        ResolveAsync(IEnumerable<(long Id, string Field)> refs, CancellationToken ct = default)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (!IsUnlocked) return result;

        var byId = refs.GroupBy(r => r.Id).ToList();
        foreach (var group in byId)
        {
            // Phase 24 audit fix #3 — vault may lock mid-iteration
            // (user clicks Lock or auto-lock fires). Skip the rest of
            // this group instead of bubbling up — partial resolution
            // is better than failing the whole script.
            (VaultItem item, IReadOnlyDictionary<string, string> clear)? pair;
            try { pair = await GetClearAsync(group.Key, ct); }
            catch (InvalidOperationException) { break; }   // vault locked
            catch (CryptographicException)    { continue; } // bad ciphertext
            if (pair is null) continue;
            // For TOTP fields, compute a live code instead of returning
            // the raw secret. Mirrors the legacy web semantics.
            var bag = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in pair.Value.clear) bag[kv.Key] = kv.Value;
            foreach (var (_, field) in group)
            {
                if (string.Equals(field, "totp_code", StringComparison.OrdinalIgnoreCase)
                    && pair.Value.clear.TryGetValue("totp_secret", out var seed))
                {
                    var (code, _) = Totp.Compute(seed);
                    bag["totp_code"] = code;
                }
            }
            result[group.Key.ToString()] = bag;
            try { await TouchUsedAsync(group.Key, ct); } catch { /* non-fatal */ }
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveAliasesAsync(
        string profileName, IEnumerable<string> aliases, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!IsUnlocked || string.IsNullOrEmpty(profileName)) return result;

        var distinct = aliases
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return result;

        // Group aliases by their target kind so we issue at most ONE
        // ListAsync per kind. The "" kind (TOTP — any kind that has a
        // totp_secret field) gets a separate pass that walks every
        // profile-bound item.
        //
        // Phase 70b — split aliases into two buckets:
        //   • known: in VaultAliases catalog → bucket by spec.Kind.
        //   • unknown: free-form identifier → lookup as raw secret
        //     key on ANY profile-bound item. This is the path that
        //     makes user-defined custom fields work transparently.
        var byKind = new Dictionary<string, List<VaultAliases.AliasSpec>>(
            StringComparer.OrdinalIgnoreCase);
        var unknownAliases = new List<string>();
        foreach (var a in distinct)
        {
            var spec = VaultAliases.Get(a);
            if (spec is null) { unknownAliases.Add(a); continue; }
            if (!byKind.TryGetValue(spec.Kind, out var bucket))
                byKind[spec.Kind] = bucket = new List<VaultAliases.AliasSpec>();
            bucket.Add(spec);
        }

        foreach (var (kind, specs) in byKind)
        {
            // kind="" means "any kind" — search every profile-bound item.
            var items = await ListAsync(
                kind: string.IsNullOrEmpty(kind) ? null : kind,
                profileName: profileName,
                ct: ct);
            if (items.Count == 0) continue;

            // Pick the most recently-updated item if multiple match
            // (e.g. user re-imported the same wallet — newer wins).
            var item = items.OrderByDescending(i => i.UpdatedAt).First();

            (VaultItem item, IReadOnlyDictionary<string, string> clear)? pair;
            try { pair = await GetClearAsync(item.Id, ct); }
            catch (InvalidOperationException) { return result; }   // locked
            catch (CryptographicException)    { continue; }
            if (pair is null) continue;

            foreach (var spec in specs)
            {
                if (pair.Value.clear.TryGetValue(spec.Field, out var value)
                    && !string.IsNullOrEmpty(value))
                {
                    result[spec.Alias] = value;
                }
                else if (string.Equals(spec.Field, "totp_secret", StringComparison.OrdinalIgnoreCase)
                         && pair.Value.clear.TryGetValue("totp_secret", out var seed))
                {
                    // Compute live TOTP code for ${TOTP} convenience.
                    var (code, _) = Totp.Compute(seed);
                    result[spec.Alias] = code;
                }
            }
            try { await TouchUsedAsync(item.Id, ct); } catch { /* non-fatal */ }
        }

        // Phase 70b — unknown-alias fallback. For aliases not in the
        // VaultAliases catalog (e.g. user-defined custom keys like
        // "discord_token", "wallet_email_pin"), walk every profile-
        // bound item once, decrypt, and pick the first non-empty
        // value matching the alias as a secret key. Case-insensitive
        // match because users type aliases inconsistently in scripts.
        // Newest item wins on collision (UpdatedAt desc).
        //
        // Phase 71 — also consult VaultItem.FieldMetaJson: if the
        // matched field has IsTotp=true the stored value is a Base32
        // seed and we return a freshly computed 6-digit code instead
        // of the raw seed. This is the dynamic equivalent of the
        // catalog's hardcoded "TOTP" alias.
        if (unknownAliases.Count > 0)
        {
            var allItems = await ListAsync(kind: null, profileName: profileName, ct: ct);
            if (allItems.Count > 0)
            {
                foreach (var it in allItems.OrderByDescending(i => i.UpdatedAt))
                {
                    (VaultItem item, IReadOnlyDictionary<string, string> clear)? pair;
                    try { pair = await GetClearAsync(it.Id, ct); }
                    catch (InvalidOperationException) { return result; } // locked mid-flight
                    catch (CryptographicException)    { continue; }
                    if (pair is null) continue;

                    // Parse the item's per-field meta once per item so
                    // we don't deserialise per alias.
                    Dictionary<string, VaultFieldMeta>? meta = null;
                    if (!string.IsNullOrWhiteSpace(it.FieldMetaJson))
                    {
                        try
                        {
                            meta = JsonSerializer.Deserialize<
                                Dictionary<string, VaultFieldMeta>>(it.FieldMetaJson!);
                        }
                        catch (JsonException) { /* ignore corrupt meta */ }
                    }

                    foreach (var alias in unknownAliases)
                    {
                        if (result.ContainsKey(alias)) continue;

                        // Resolve the actual storage key (case-insensitive
                        // match against the cleartext bag).
                        string? matchedKey = null;
                        string? value = null;
                        if (pair.Value.clear.TryGetValue(alias, out var v0) && !string.IsNullOrEmpty(v0))
                        { matchedKey = alias; value = v0; }
                        else
                        {
                            foreach (var kv in pair.Value.clear)
                            {
                                if (string.Equals(kv.Key, alias, StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrEmpty(kv.Value))
                                {
                                    matchedKey = kv.Key;
                                    value = kv.Value;
                                    break;
                                }
                            }
                        }
                        if (matchedKey is null || value is null) continue;

                        // Phase 71 — TOTP seed → live code conversion.
                        if (meta is not null
                            && meta.TryGetValue(matchedKey, out var fm)
                            && fm.IsTotp)
                        {
                            try
                            {
                                var (code, _) = Totp.Compute(value);
                                value = code;
                            }
                            catch
                            {
                                // Bad seed — fall back to raw value so
                                // the user at least sees the placeholder
                                // resolved (and can fix it).
                            }
                        }

                        result[alias] = value;
                    }
                    if (unknownAliases.All(a => result.ContainsKey(a))) break;
                }
            }
        }

        return result;
    }

    // ─── Helpers ─────────────────────────────────────────────────

    /// <summary>Public-API check (no gate held). Subject to TOCTOU
    /// races — callers that go on to USE _key after this check must
    /// re-check inside the gate via <see cref="EnsureUnlockedInternal"/>.</summary>
    private void EnsureUnlocked()
    {
        if (_key is null) throw new InvalidOperationException("vault is locked");
    }

    /// <summary>Internal check called WHILE holding <see cref="_gate"/>.
    /// Use this when about to dereference <see cref="_key"/> directly
    /// — it guarantees no Lock() can interpose between the check and
    /// the use.</summary>
    private void EnsureUnlockedInternal()
    {
        if (_key is null) throw new InvalidOperationException("vault is locked");
    }

    private byte[]? EncryptSecrets(IReadOnlyDictionary<string, string> clear)
    {
        if (clear is null || clear.Count == 0) return null;
        var json = JsonSerializer.Serialize(clear);
        return VaultCrypto.EncryptString(_key!, json);
    }

    private IReadOnlyDictionary<string, string> DecryptSecrets(byte[]? blob)
    {
        if (blob is null || blob.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        var json = VaultCrypto.DecryptString(_key!, blob);
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return dict;
    }

    private async Task<byte[]?> ReadConfigBytesAsync(string key, CancellationToken ct)
    {
        var sql = "SELECT value FROM vault_config WHERE key = @key;";
        return await _db.QueueAsync(
            c => c.QuerySingleOrDefaultAsync<byte[]?>(sql, new { key }), ct);
    }

    private Task WriteConfigBytesAsync(string key, byte[] value, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO vault_config (key, value, updated_at)
            VALUES (@key, @value, @now)
            ON CONFLICT(key) DO UPDATE SET value = @value, updated_at = @now;
        """;
        return _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            key,
            value,
            now = DateTime.UtcNow.ToString("O"),
        }), ct);
    }

    private void OnLockStateChanged() => LockStateChanged?.Invoke(this, EventArgs.Empty);

    // ─── Phase 26 — auto-lock config ─────────────────────────────────

    public async Task<int> GetAutoLockMinutesAsync(CancellationToken ct = default)
    {
        var raw = await ReadConfigBytesAsync(CfgAutoLockMin, ct);
        if (raw is null || raw.Length == 0) return DefaultAutoLockMinutes;
        try
        {
            var s = Encoding.UTF8.GetString(raw);
            if (int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
                return Math.Clamp(n, 0, 24 * 60);
        }
        catch { /* fall through to default */ }
        return DefaultAutoLockMinutes;
    }

    public async Task SetAutoLockMinutesAsync(int minutes, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(minutes, 0, 24 * 60);
        var bytes = Encoding.UTF8.GetBytes(
            clamped.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await WriteConfigBytesAsync(CfgAutoLockMin, bytes, ct);
        _log.LogInformation("Vault auto-lock set to {Min} minute(s)", clamped);
    }

    // ─── Phase 26 — master password rotation ─────────────────────────

    public async Task ChangeMasterPasswordAsync(
        string oldPassphrase, string newPassphrase, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(oldPassphrase))
            throw new ArgumentException("current passphrase is required", nameof(oldPassphrase));
        if (string.IsNullOrEmpty(newPassphrase))
            throw new ArgumentException("new passphrase is required", nameof(newPassphrase));
        if (newPassphrase.Length < 8)
            throw new ArgumentException("new passphrase must be at least 8 characters", nameof(newPassphrase));

        // Hold the gate for the entire rotation so a concurrent Lock() /
        // CRUD can't see a half-rotated state. Encrypt/decrypt happens
        // INSIDE the gate just like CreateAsync.
        await _gate.WaitAsync(ct);
        byte[]? oldKey = null;
        byte[]? newKey = null;
        try
        {
            // 1. Verify the OLD passphrase.
            var salt = await ReadConfigBytesAsync(CfgSalt, ct);
            var verifier = await ReadConfigBytesAsync(CfgVerifier, ct);
            if (salt is null || verifier is null)
                throw new InvalidOperationException("vault is not initialized");

            oldKey = VaultCrypto.DeriveKey(oldPassphrase, salt);
            try
            {
                var plain = VaultCrypto.DecryptString(oldKey, verifier);
                if (plain != VaultCrypto.VerifierPlain)
                    throw new UnauthorizedAccessException("current passphrase doesn't match");
            }
            catch (CryptographicException)
            {
                throw new UnauthorizedAccessException("current passphrase doesn't match");
            }

            // 2. Derive the NEW key + verifier under a brand-new salt.
            // Rotating salt forces every rainbow-table-against-old-salt
            // assumption to start over.
            var newSalt = VaultCrypto.NewSalt();
            newKey = VaultCrypto.DeriveKey(newPassphrase, newSalt);
            var newVerifier = VaultCrypto.EncryptString(newKey, VaultCrypto.VerifierPlain);

            // 3. Read every secrets_enc row, decrypt under old, re-encrypt
            // under new. Done in a single DB transaction so we never see
            // a partial-rotation state on disk.
            await _db.QueueAsync(async c =>
            {
                using var tx = c.BeginTransaction();
                var rows = (await c.QueryAsync<(long Id, byte[]? Blob)>(
                    "SELECT id AS Id, secrets_enc AS Blob FROM vault_items;",
                    transaction: tx)).ToList();
                foreach (var row in rows)
                {
                    if (row.Blob is null || row.Blob.Length == 0) continue;
                    string json;
                    try { json = VaultCrypto.DecryptString(oldKey, row.Blob); }
                    catch (CryptographicException ex)
                    {
                        // Phase 26 audit fix — partial rotation is WORSE
                        // than no rotation: a row that won't decrypt under
                        // the old key would, after rotation, be unreachable
                        // because the old key is gone. Abort the whole
                        // rotation; the tx auto-rolls back on throw.
                        throw new InvalidOperationException(
                            $"Vault item #{row.Id} could not be decrypted under the current passphrase " +
                            $"(possibly corrupt or rotated mid-flight). Rotation aborted; the vault is unchanged. " +
                            $"Repair or delete the offending row, then retry.", ex);
                    }
                    var rewrapped = VaultCrypto.EncryptString(newKey, json);
                    await c.ExecuteAsync(
                        "UPDATE vault_items SET secrets_enc = @blob, updated_at = @now WHERE id = @id;",
                        new { blob = rewrapped, now = DateTime.UtcNow.ToString("O"), id = row.Id },
                        transaction: tx);
                }
                // 4. Replace salt + verifier inside the same tx.
                const string upsert = """
                    INSERT INTO vault_config (key, value, updated_at)
                    VALUES (@key, @value, @now)
                    ON CONFLICT(key) DO UPDATE SET value = @value, updated_at = @now;
                """;
                var nowIso = DateTime.UtcNow.ToString("O");
                await c.ExecuteAsync(upsert, new { key = CfgSalt,     value = newSalt,     now = nowIso }, transaction: tx);
                await c.ExecuteAsync(upsert, new { key = CfgVerifier, value = newVerifier, now = nowIso }, transaction: tx);
                tx.Commit();
                return rows.Count;
            }, ct);

            // 5. Swap in-memory key.
            if (_key is not null) CryptographicOperations.ZeroMemory(_key);
            _key = newKey;
            newKey = null; // ownership transferred to _key — don't wipe in finally
            _initialized = true;
            NotifyActivity();
            _log.LogInformation("Vault master passphrase rotated");
        }
        finally
        {
            // Wipe ephemeral key material we own.
            if (oldKey is not null) CryptographicOperations.ZeroMemory(oldKey);
            if (newKey is not null) CryptographicOperations.ZeroMemory(newKey);
            _gate.Release();
        }
        // Phase 26 audit fix — fire the event in a try block so a
        // misbehaving subscriber can't bubble out and make the rotation
        // LOOK like it failed when it actually succeeded on disk.
        try { OnLockStateChanged(); }
        catch (Exception ex) { _log.LogWarning(ex, "LockStateChanged subscriber threw after rotation"); }
    }

    public void Dispose()
    {
        // Phase 24 audit fix #6 — bounded wait so app shutdown can't
        // hang if a vault op held the gate when teardown started. We
        // sweep the in-memory key whether or not the gate could be
        // acquired — by the time we reach Dispose, the host is going
        // away and any in-flight call is doomed regardless.
        try
        {
            if (_gate.Wait(TimeSpan.FromSeconds(2)))
            {
                if (_key is not null)
                {
                    CryptographicOperations.ZeroMemory(_key);
                    _key = null;
                }
                _gate.Release();
            }
            else
            {
                _log.LogWarning("VaultService.Dispose: gate held > 2s; key may not be wiped before shutdown");
                if (_key is not null)
                {
                    // Best-effort wipe without the gate; on app exit
                    // races don't matter because the process is going
                    // down.
                    CryptographicOperations.ZeroMemory(_key);
                    _key = null;
                }
            }
        }
        catch { /* ignore — we're shutting down */ }
        finally
        {
            try { _gate.Dispose(); } catch { /* ignore */ }
        }
    }
}
