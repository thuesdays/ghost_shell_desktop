// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GhostShell.Core.Extensions;

/// <summary>
/// Phase 27 — parsed Chrome <c>manifest.json</c>. We don't aim to
/// validate every detail of the spec; we just pull the fields the
/// Extensions page surfaces (name, version, description, icon path,
/// declared permissions) and synthesize a stable extension ID when
/// the manifest doesn't ship a public key.
///
/// Supports both manifest v2 (<c>permissions</c> only) and v3
/// (<c>permissions</c> + <c>host_permissions</c>). The host-permissions
/// list is surfaced separately because it's the one users care about
/// most ("what sites can this extension see?").
/// </summary>
public sealed record ExtensionManifest
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? Homepage { get; init; }
    public int ManifestVersion { get; init; } = 2;

    /// <summary>Path to the icon file relative to the manifest's folder.
    /// We pick the largest declared size for crispest rendering in the
    /// Extensions list.</summary>
    public string? IconPath { get; init; }

    /// <summary>Synthesized 32-char lowercase ID. Computed from the
    /// manifest's <c>key</c> field when present (matches Chrome's own
    /// derivation), falling back to a SHA-256 of the unpacked path so
    /// the same on-disk extension always gets the same ID.</summary>
    public string ExtId { get; init; } = "";

    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> HostPermissions { get; init; } = Array.Empty<string>();

    /// <summary>Raw bytes of the manifest, kept verbatim for storage.</summary>
    public string RawJson { get; init; } = "";

    /// <summary>
    /// Parse a manifest.json file. <paramref name="unpackedDir"/> is the
    /// folder containing the manifest — used for resolving the icon
    /// relative path and for fingerprinting when no <c>key</c> field
    /// is declared.
    /// </summary>
    /// <summary>Phase 27 audit fix — refuse a manifest > 4 MB. Real
    /// extensions ship manifests in single-digit kilobytes; anything
    /// bigger is either accidental (bundled JS pasted into the
    /// manifest) or hostile (OOM via giant payload). 4 MB leaves
    /// plenty of room for legit edge cases.</summary>
    public const long MaxManifestSizeBytes = 4L * 1024L * 1024L;

    public static ExtensionManifest ParseFromFile(string manifestPath, string unpackedDir)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("manifest.json not found", manifestPath);
        var info = new FileInfo(manifestPath);
        if (info.Length > MaxManifestSizeBytes)
            throw new InvalidDataException(
                $"manifest.json is {info.Length / 1024} KB — refusing to parse. " +
                $"Cap is {MaxManifestSizeBytes / (1024 * 1024)} MB.");
        var raw = File.ReadAllText(manifestPath);
        return Parse(raw, unpackedDir);
    }

    public static ExtensionManifest Parse(string rawJson, string unpackedDir)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new ArgumentException("manifest is empty", nameof(rawJson));

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("manifest.json must be a JSON object");

        var name        = TryString(root, "name") ?? "Unnamed extension";
        var version     = TryString(root, "version") ?? "0.0.0";
        var description = TryString(root, "description");
        var homepage    = TryString(root, "homepage_url");
        var manifestVer = TryInt(root, "manifest_version") ?? 2;

        // Author — manifest v3 has "author" as a string, v2 sometimes
        // nests it under "author.email". We accept either.
        string? author = null;
        if (root.TryGetProperty("author", out var authorEl))
        {
            if (authorEl.ValueKind == JsonValueKind.String) author = authorEl.GetString();
            else if (authorEl.ValueKind == JsonValueKind.Object && authorEl.TryGetProperty("email", out var email))
                author = email.GetString();
        }

        // Phase 28 fix — resolve Chrome i18n placeholders. Manifests
        // commonly use "__MSG_some_key__" for name/description so the
        // real label can switch by locale. Without resolution, the UI
        // shows the literal "__MSG_xxx__" string (visible on OKX Wallet
        // and most internationalised extensions). Look up the key in
        // _locales/<default_locale>/messages.json — falling back to
        // _locales/en/, then _locales/en_US/.
        var localeMessages = LoadLocaleMessages(root, unpackedDir);
        name        = ResolveI18nPlaceholder(name,        localeMessages) ?? name;
        description = ResolveI18nPlaceholder(description, localeMessages);

        var iconPath = ResolveIconPath(root, unpackedDir);
        var perms    = ReadStringArray(root, "permissions");
        var hostPerms = ReadStringArray(root, "host_permissions");

        var extId = SynthesizeExtId(root, unpackedDir);

        return new ExtensionManifest
        {
            Name = name,
            Version = version,
            Description = description,
            Author = author,
            Homepage = homepage,
            ManifestVersion = manifestVer,
            IconPath = iconPath,
            ExtId = extId,
            Permissions = perms,
            HostPermissions = hostPerms,
            RawJson = rawJson,
        };
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static string? TryString(JsonElement obj, string field)
        => obj.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int? TryInt(JsonElement obj, string field)
        => obj.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)
            ? n
            : (int?)null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string field)
    {
        if (!obj.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
        return list;
    }

    // ─── i18n placeholder resolution ─────────────────────────────────

    /// <summary>If <paramref name="value"/> is a Chrome i18n
    /// placeholder of the form <c>__MSG_keyName__</c>, return the
    /// corresponding message from <paramref name="messages"/>.
    /// Returns <paramref name="value"/> unchanged when it isn't a
    /// placeholder; returns null when the input was null.</summary>
    public static string? ResolveI18nPlaceholder(
        string? value, IReadOnlyDictionary<string, string>? messages)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (messages is null || messages.Count == 0) return value;
        // Pattern: __MSG_<key>__   (case-insensitive prefix per Chrome).
        if (value.Length < 8) return value;
        if (!value.StartsWith("__MSG_", StringComparison.OrdinalIgnoreCase)) return value;
        if (!value.EndsWith("__", StringComparison.Ordinal)) return value;
        var key = value.Substring(6, value.Length - 8);
        // Chrome treats keys case-insensitively; messages.json is
        // typically lowercase.
        if (messages.TryGetValue(key, out var hit)) return hit;
        if (messages.TryGetValue(key.ToLowerInvariant(), out hit)) return hit;
        return value;
    }

    /// <summary>Read <c>_locales/{default_locale}/messages.json</c>
    /// and flatten it to <c>{ key → message }</c>. Falls back to
    /// <c>en</c>, then <c>en_US</c>, then any single available locale.
    /// Returns null when no messages file is found.</summary>
    private static IReadOnlyDictionary<string, string>? LoadLocaleMessages(
        JsonElement root, string unpackedDir)
    {
        var localesDir = Path.Combine(unpackedDir, "_locales");
        if (!Directory.Exists(localesDir)) return null;
        var preferred = TryString(root, "default_locale");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferred)) candidates.Add(preferred!);
        candidates.Add("en");
        candidates.Add("en_US");
        // As a last resort, take whichever locale actually exists.
        try
        {
            foreach (var d in Directory.EnumerateDirectories(localesDir))
                candidates.Add(Path.GetFileName(d));
        }
        catch { /* ignore */ }
        foreach (var locale in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(localesDir, locale, "messages.json");
            if (!File.Exists(path)) continue;
            try
            {
                var raw = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in doc.RootElement.EnumerateObject())
                {
                    // Each entry is { "message": "...", "description": "...", "placeholders": {...} }.
                    if (kv.Value.ValueKind != JsonValueKind.Object) continue;
                    if (kv.Value.TryGetProperty("message", out var m) &&
                        m.ValueKind == JsonValueKind.String)
                    {
                        dict[kv.Name] = m.GetString() ?? "";
                    }
                }
                if (dict.Count > 0) return dict;
            }
            catch { /* try next locale */ }
        }
        return null;
    }

    /// <summary>Pick the LARGEST declared icon size and return a path
    /// relative to the unpacked dir. Manifest format:
    /// <c>"icons": { "16": "...", "48": "...", "128": "..." }</c>.
    /// Phase 27 audit fix — reject paths that escape the unpacked dir
    /// (e.g. <c>"../../etc/passwd"</c>) so the UI can't be tricked into
    /// reading arbitrary files when it tries to render the icon.</summary>
    private static string? ResolveIconPath(JsonElement root, string unpackedDir)
    {
        if (!root.TryGetProperty("icons", out var icons) || icons.ValueKind != JsonValueKind.Object)
            return null;
        int bestSize = -1;
        string? bestPath = null;
        foreach (var kv in icons.EnumerateObject())
        {
            if (!int.TryParse(kv.Name, out var size)) continue;
            if (kv.Value.ValueKind != JsonValueKind.String) continue;
            if (size > bestSize)
            {
                bestSize = size;
                bestPath = kv.Value.GetString();
            }
        }
        if (bestPath is null) return null;
        // Normalize: manifests use forward slashes; on-disk we want
        // platform-native.
        var rel = bestPath.Replace('/', Path.DirectorySeparatorChar);
        // Reject absolute paths + traversal — the caller resolves
        // relative to unpackedDir, so an absolute path here would
        // happily read outside.
        if (Path.IsPathRooted(rel)) return null;
        try
        {
            var combined = Path.GetFullPath(Path.Combine(unpackedDir, rel));
            var rooted   = Path.GetFullPath(unpackedDir);
            if (!combined.StartsWith(rooted + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return null;
        }
        catch { return null; }
        return rel;
    }

    /// <summary>
    /// Compute a stable 32-char lowercase Chrome-extension ID.
    ///
    /// Chrome's algorithm (from extensions/common/extension.cc
    /// <c>Extension::GenerateIdForPath</c>):
    ///   • If the manifest has a <c>key</c> field — SHA-256 of the
    ///     base64-decoded bytes.
    ///   • Otherwise — SHA-256 of the unpacked path's NATIVE bytes:
    ///       Windows  → UTF-16 LE (wchar_t)
    ///       POSIX    → UTF-8
    ///     PLUS path separators normalised (backslashes on Windows).
    ///
    /// Take the first 16 bytes, remap each nibble (0-15) to 'a'-'p'.
    /// Phase 27 hot-fix — earlier versions hashed UTF-8 of the path on
    /// Windows too. The result didn't match the ID Chrome assigned at
    /// runtime, so the toolbar-pin keys (pinned_actions /
    /// pinned_extensions / toolbar) all referenced a phantom ID and
    /// Chrome silently dropped them.
    /// </summary>
    public static string SynthesizeExtId(JsonElement root, string unpackedDir)
    {
        byte[] hashInput;
        if (root.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
        {
            try { hashInput = Convert.FromBase64String(keyEl.GetString() ?? ""); }
            catch { hashInput = NativePathBytes(unpackedDir); }
        }
        else
        {
            hashInput = NativePathBytes(unpackedDir);
        }
        var sha = SHA256.HashData(hashInput);
        var sb = new StringBuilder(32);
        for (int i = 0; i < 16; i++)
        {
            // Each byte → 2 chars in [a-p]: high nibble first, then low.
            sb.Append((char)('a' + (sha[i] >> 4)));
            sb.Append((char)('a' + (sha[i] & 0x0F)));
        }
        return sb.ToString();
    }

    /// <summary>Encode a filesystem path the way Chrome does internally
    /// (<c>base::FilePath::CharType</c>): UTF-16 LE on Windows, UTF-8
    /// elsewhere. Path separators are normalised — Windows uses
    /// backslash, POSIX forward slash. Chrome calls this on the
    /// canonicalised long path before hashing.</summary>
    private static byte[] NativePathBytes(string path)
    {
        // Canonicalise: strip relative segments + trailing separators,
        // resolve to an absolute path so the hash is stable across
        // working-directory changes. Chrome runs on the resolved path
        // it actually loaded the extension from.
        try { path = Path.GetFullPath(path); } catch { /* leave as-is */ }
        if (OperatingSystem.IsWindows())
        {
            // Normalise separators to backslash (Chrome does
            // FilePath::NormalizePathSeparators before hashing).
            path = path.Replace('/', '\\');
            // Trim a single trailing separator IF the path isn't a
            // drive root ("C:\"). Drive roots keep their separator.
            if (path.Length > 3 && path.EndsWith("\\")) path = path.TrimEnd('\\');
            return Encoding.Unicode.GetBytes(path); // UTF-16 LE
        }
        // POSIX path: forward slashes, UTF-8.
        path = path.Replace('\\', '/');
        if (path.Length > 1 && path.EndsWith("/")) path = path.TrimEnd('/');
        return Encoding.UTF8.GetBytes(path);
    }
}
