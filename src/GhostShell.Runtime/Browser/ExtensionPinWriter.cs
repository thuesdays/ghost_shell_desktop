// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using System.Text.Json.Nodes;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Phase 28 follow-up — register every loaded extension in the per-
/// profile <c>Default/Preferences</c> file so Chrome:
///   • shows the toolbar button by default (not buried under puzzle-
///     piece overflow),
///   • doesn't flag the extension as an unverified sideload on
///     Chrome 138+ (the patched build will quietly DISABLE it
///     otherwise),
///   • skips the "you have new extensions" first-launch nagball.
///
/// Mirrors the legacy <c>browser/runtime.py</c> pin/registration
/// block. Three pin keys are written every launch because the
/// authoritative key changed across Chrome major versions:
///   • <c>extensions.toolbar</c>           — Chrome 88–90
///   • <c>extensions.pinned_extensions</c> — Chrome 91–137
///   • <c>extensions.pinned_actions</c>    — Chrome 138+
///
/// We don't know which build the user has installed on the day they
/// click Run, so we set all three. Chrome silently ignores the keys
/// that don't apply to its version.
///
/// Each extension's <c>extensions.settings[ext_id]</c> entry is also
/// upserted with:
///   • <c>location: 4</c>           (COMMAND_LINE — not a sideload)
///   • <c>state: 1</c>              (ENABLED)
///   • <c>from_webstore: false</c>
///   • <c>creation_flags: 38</c>
///   • <c>ack_external: true</c>    (silence "new extension" toast)
///   • <c>granted_permissions</c>   (auto-approve manifest perms +
///                                   host_permissions so no popup
///                                   blocks the first run)
/// </summary>
internal static class ExtensionPinWriter
{
    public static void RegisterAndPin(
        string profileName,
        IReadOnlyCollection<ExtensionItem> extensions,
        ILogger log)
    {
        if (extensions is null || extensions.Count == 0) return;

        try
        {
            var userDataDir = AppPaths.ProfileDir(profileName);
            var defaultDir  = Path.Combine(userDataDir, "Default");
            Directory.CreateDirectory(defaultDir);
            var prefsPath   = Path.Combine(defaultDir, "Preferences");

            JsonObject root = LoadOrEmpty(prefsPath, log, profileName);

            // extensions.* root.
            var extRoot = GetOrCreateObject(root, "extensions");

            // 1. extensions.settings[ext_id] = { location, state, … }
            var settings = GetOrCreateObject(extRoot, "settings");
            foreach (var ext in extensions)
            {
                if (string.IsNullOrWhiteSpace(ext.ExtId)) continue;
                var entry = settings[ext.ExtId] as JsonObject ?? new JsonObject();
                entry["location"]                  = 4;
                entry["state"]                     = 1;
                entry["from_webstore"]             = false;
                entry["was_installed_by_default"]  = false;
                entry["was_installed_by_oem"]      = false;
                entry["creation_flags"]            = 38;
                entry["ack_external"]              = true;

                // Pre-grant the manifest's declared permissions so
                // the first popup launch isn't blocked by a "needs
                // your permission" prompt. We pull these from the
                // permissions_json + host_permissions_json columns
                // we already stored when the extension was installed.
                var apiPerms  = ParseStringArray(ext.PermissionsJson);
                var hostPerms = ParseStringArray(ext.HostPermissionsJson);
                if (apiPerms.Count > 0 || hostPerms.Count > 0)
                {
                    var granted = new JsonObject
                    {
                        ["api"]             = ToJsonArray(apiPerms),
                        ["explicit_host"]   = ToJsonArray(hostPerms),
                        ["scriptable_host"] = ToJsonArray(hostPerms),
                    };
                    entry["granted_permissions"] = granted;
                    entry["active_permissions"]  = granted.DeepClone();
                }
                settings[ext.ExtId] = entry;
            }

            // 2. Pin via all three known keys (88-90, 91-137, 138+).
            //
            // Phase 31 hot-fix — REBUILD the pin array from scratch
            // each launch instead of appending. Earlier versions of
            // ExtensionManifest hashed the path as UTF-8 on Windows
            // (Chrome wants UTF-16 LE), so users who launched on the
            // old build have stale "wrong" IDs in pinned_actions.
            // Chrome ignores them, but they clutter the file and make
            // it hard to tell whether the new (correct) IDs are even
            // there. Rebuilding leaves only the IDs of currently
            // installed extensions, which is what Chrome would see
            // anyway — IDs that don't correspond to a loaded extension
            // are pure dead weight.
            var liveIds = extensions
                .Where(e => !string.IsNullOrWhiteSpace(e.ExtId))
                .Select(e => e.ExtId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            int newlyPinned = 0;
            foreach (var key in new[] { "pinned_actions", "pinned_extensions", "toolbar" })
            {
                // Note any IDs that were ALREADY in the array — those
                // ones we don't count as "newly pinned" in the log.
                var prior = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (extRoot[key] is JsonArray oldArr)
                {
                    foreach (var n in oldArr)
                        if (n is JsonValue v && v.TryGetValue<string>(out var s))
                            prior.Add(s);
                }
                var rebuilt = new JsonArray();
                foreach (var id in liveIds)
                {
                    rebuilt.Add(JsonValue.Create(id));
                    if (key == "pinned_actions" && !prior.Contains(id))
                        newlyPinned++;
                }
                extRoot[key] = rebuilt;
            }

            // 3. Suppress the first-launch "you have new extensions" toast.
            var alerts = GetOrCreateObject(extRoot, "alerts");
            alerts["initialized"] = true;

            // Write back atomically.
            var json = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = false,
            });
            var tmp = prefsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, prefsPath, overwrite: true);
            log.LogInformation(
                "Registered {Total} extension(s) in Preferences for '{Profile}' ({New} newly pinned)",
                extensions.Count, profileName, newlyPinned);
            // Phase 31 — log the exact IDs we wrote so the user can
            // cross-check chrome://extensions/ when pinning misbehaves.
            // If the on-disk ID doesn't match Chrome's, the path-hash
            // algorithm is still wrong; if it matches, the pin keys
            // are simply being ignored by this Chrome version (look
            // for a new key in pin layout).
            foreach (var ext in extensions)
            {
                log.LogDebug(
                    "  ↳ '{Name}' v{Ver} → id={Id} path='{Path}'",
                    ext.Name, ext.Version, ext.ExtId, ext.LocalPath);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Couldn't update Preferences for '{Profile}' — extensions may show as unverified or stay hidden in puzzle-piece menu",
                profileName);
        }
    }

    // ─── helpers ──────────────────────────────────────────────────────

    private static JsonObject LoadOrEmpty(string path, ILogger log, string profileName)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            var raw = File.ReadAllText(path);
            return JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Preferences for '{Profile}' is malformed — rewriting from scratch",
                profileName);
            return new JsonObject();
        }
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var fresh = new JsonObject();
        parent[key] = fresh;
        return fresh;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing) return existing;
        var fresh = new JsonArray();
        parent[key] = fresh;
        return fresh;
    }

    private static List<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
            var list = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    list.Add(item.GetString() ?? "");
            return list;
        }
        catch { return new List<string>(); }
    }

    private static JsonArray ToJsonArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var s in items) arr.Add(JsonValue.Create(s));
        return arr;
    }
}
