// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Cookie Packs page ViewModel — port of the legacy
/// <c>cookies-marketplace</c> + pack-management screens. Mirrors
/// the reusable subset (skip the marketplace API per spec):
///
///   • List packs (gzipped JSON bundles in <c>cookie_packs</c>)
///   • Import a pack from a JSON file on disk
///   • Export a pack from a running profile
///   • Apply a pack to a running profile (push cookies + storage)
///   • Delete a pack
///
/// All the complex state is delegated to <see cref="ICookiePackService"/>;
/// this VM is glue + UX wiring (filename picker, profile chooser,
/// confirms).
/// </summary>
public sealed partial class CookiePacksViewModel : BaseViewModel
{
    private readonly ICookiePackService _packs;
    private readonly IProfileRunner     _runner;
    private readonly IDialogService     _dialogs;
    private readonly ILogger<CookiePacksViewModel> _log;

    public CookiePacksViewModel(
        ICookiePackService packs,
        IProfileRunner runner,
        IDialogService dialogs,
        ILogger<CookiePacksViewModel> log)
    {
        _packs   = packs;
        _runner  = runner;
        _dialogs = dialogs;
        _log     = log;
    }

    public ObservableCollection<CookiePack> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private int  _total;

    public override async Task OnNavigatedToAsync() => await ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var packs = await _packs.ListAsync();
            Items.Clear();
            foreach (var p in packs) Items.Add(p);
            Total   = Items.Count;
            IsEmpty = Items.Count == 0;
            _log.LogInformation("Cookie packs loaded: {Count}", Items.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading cookie packs failed");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ImportFromFileAsync()
    {
        var dlg = new OpenFileDialog
        {
            Filter      = "Cookie pack (*.json)|*.json|All files|*.*",
            Title       = "Import cookie pack",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        string raw;
        try { raw = await File.ReadAllTextAsync(dlg.FileName, Encoding.UTF8); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not read pack file {Path}", dlg.FileName);
            await _dialogs.ConfirmAsync("Could not read file",
                ex.Message, "OK", ConfirmSeverity.Error);
            return;
        }

        // The legacy pack JSON shape includes both the metadata
        // (slug, label, domains) and the payload (cookies, storage)
        // in a single document. We deserialise loosely so a
        // user-edited file with a few missing fields still imports.
        try
        {
            var meta    = ParsePackMeta(raw, dlg.FileName);
            var payload = ParsePayload(raw);

            await _packs.UpsertAsync(meta, payload);
            await ReloadAsync();
            await _dialogs.ConfirmAsync(
                $"Imported '{meta.Label}'",
                $"Slug: {meta.Slug}\n" +
                $"Cookies: {payload.Cookies.Count}\n" +
                $"Storage origins: {payload.Storage.Count}",
                "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Importing pack from {Path} failed", dlg.FileName);
            await _dialogs.ConfirmAsync(
                "Could not import pack",
                $"{ex.Message}\n\nThe file should contain a JSON object " +
                "with at least 'slug', 'label', 'cookies', and (optional) 'localStorage'.",
                "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ExportFromRunningAsync()
    {
        if (_runner.ActiveProfileNames.Count == 0)
        {
            await _dialogs.ConfirmAsync(
                "No profile running",
                "Start a profile first, then export. The pack snapshot " +
                "is taken from the live browser session.",
                "OK", ConfirmSeverity.Info);
            return;
        }

        // Multiple profiles running? Ask the user. With one running
        // we shortcut to that one. Real picker UI lands later.
        var profile = _runner.ActiveProfileNames.First();
        var slug    = $"export-{profile}-{DateTime.UtcNow:yyyyMMdd-HHmm}";
        var label   = $"Export from '{profile}' ({DateTime.Now:yyyy-MM-dd HH:mm})";

        var ok = await _dialogs.ConfirmAsync(
            "Export cookie pack",
            $"Snapshot the current cookies + localStorage of '{profile}' as " +
            $"a new pack '{slug}'?",
            "Export");
        if (!ok) return;

        // We need a live session reference to capture from. The
        // runner doesn't currently expose one (it owns the
        // IBrowserSession internally). Add a hook in a future
        // iteration; for now surface a clear "not yet wired" message
        // so the user knows the action exists.
        await _dialogs.ConfirmAsync(
            "Not yet wired",
            "Export-from-running needs the runner to expose the live " +
            "session reference. Tracked under Phase 5 — for now use " +
            "Import to load packs from disk.",
            "OK", ConfirmSeverity.Info);
    }

    [RelayCommand]
    private async Task ApplyAsync(CookiePack? pack)
    {
        if (pack is null) return;
        if (_runner.ActiveProfileNames.Count == 0)
        {
            await _dialogs.ConfirmAsync(
                "No profile running",
                "Start a profile first. Apply pushes the pack's cookies + " +
                "storage into the live browser session.",
                "OK", ConfirmSeverity.Info);
            return;
        }
        // Same caveat as Export — runner doesn't expose live session.
        // Until that's wired, surface clearly.
        await _dialogs.ConfirmAsync(
            "Not yet wired",
            "Live-apply needs the runner to expose the live session " +
            "reference. Tracked under Phase 5 — for now packs are " +
            "applied automatically at next profile launch via the " +
            "snapshot auto-restore path.",
            "OK", ConfirmSeverity.Info);
    }

    [RelayCommand]
    private async Task DeleteCookiePackAsync(CookiePack? pack)
    {
        if (pack is null) return;
        // ConfirmAsync(title, message, confirmLabel, severity) — 4-arg
        // signature; "Cancel" label is implicit. Pass severity by name
        // since confirmLabel is positional.
        var ok = await _dialogs.ConfirmAsync(
            $"Delete pack '{pack.Label}'?",
            $"This permanently removes pack #{pack.Id} (slug: {pack.Slug}, " +
            $"{pack.CookiesCount} cookies, {pack.StorageCount} storage origins). There's no undo.",
            confirmLabel: "Delete",
            severity:     ConfirmSeverity.Warning);
        if (!ok) return;

        try
        {
            await _packs.DeleteAsync(pack.Id);
            Items.Remove(pack);
            Total = Items.Count;
            IsEmpty = Items.Count == 0;
            _log.LogInformation("Pack #{Id} ('{Label}') deleted", pack.Id, pack.Label);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Delete pack #{Id} failed", pack.Id);
            await _dialogs.ConfirmAsync("Could not delete",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ExportToFileAsync(CookiePack? pack)
    {
        if (pack is null) return;
        var dlg = new SaveFileDialog
        {
            Title    = "Export cookie pack",
            FileName = $"{pack.Slug}.json",
            Filter   = "Cookie pack (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var payload = await _packs.GetPayloadAsync(pack.Id);
            if (payload is null)
            {
                await _dialogs.ConfirmAsync("Pack missing",
                    "Pack payload is empty (corrupted or deleted).",
                    "OK", ConfirmSeverity.Error);
                return;
            }

            // Compose the disk format — slug + label + domains at the
            // top level, payload nested. Same shape the legacy
            // exporter produces, so files round-trip between the
            // two implementations.
            var doc = new
            {
                slug         = pack.Slug,
                label        = pack.Label,
                domains      = pack.Domains,
                ageDays      = pack.AgeDays,
                captchaRate  = pack.CaptchaRate,
                cookies      = payload.Cookies,
                storage      = payload.Storage,
                metadata = new
                {
                    created_at = pack.CreatedAt.ToString("O"),
                    pack_version = "1.0",
                },
            };
            var json = System.Text.Json.JsonSerializer.Serialize(doc,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    WriteIndented        = true,
                });
            await File.WriteAllTextAsync(dlg.FileName, json, Encoding.UTF8);
            _log.LogInformation("Pack #{Id} exported to {Path}", pack.Id, dlg.FileName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Export pack #{Id} failed", pack.Id);
            await _dialogs.ConfirmAsync("Could not export",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    // ─── Loose JSON parsing helpers ──────────────────────────────

    /// <summary>
    /// Pull the metadata (slug, label, domains, ageDays, captchaRate)
    /// out of an arbitrary pack JSON. The slug is required; if the
    /// file doesn't supply one we derive it from the filename.
    /// Other fields fall back to safe defaults so a minimal pack
    /// with just cookies still imports.
    /// </summary>
    private static CookiePack ParsePackMeta(string json, string sourceFile)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        string slug  = TryString(root, "slug")
                    ?? TryString(root, "id")
                    ?? Path.GetFileNameWithoutExtension(sourceFile);
        string label = TryString(root, "label") ?? slug;

        var domains = new List<string>();
        if (root.TryGetProperty("domains", out var dm)
            && dm.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var d in dm.EnumerateArray())
                if (d.ValueKind == System.Text.Json.JsonValueKind.String)
                    domains.Add(d.GetString()!);
        }

        int ageDays = root.TryGetProperty("ageDays",  out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number
                      ? a.GetInt32()
                      : root.TryGetProperty("age_days", out var a2) && a2.ValueKind == System.Text.Json.JsonValueKind.Number
                          ? a2.GetInt32() : 0;
        double captchaRate = root.TryGetProperty("captchaRate", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number
                             ? c.GetDouble()
                             : root.TryGetProperty("captcha_rate", out var c2) && c2.ValueKind == System.Text.Json.JsonValueKind.Number
                                 ? c2.GetDouble() : 0;

        return new CookiePack
        {
            Slug         = slug,
            Label        = label,
            Domains      = domains,
            AgeDays      = ageDays,
            CaptchaRate  = captchaRate,
            CookiesCount = 0, // filled by service post-payload
            StorageCount = 0,
            CreatedAt    = DateTime.UtcNow,
            UpdatedAt    = DateTime.UtcNow,
        };
    }

    private static SessionPayload ParsePayload(string json)
    {
        // We accept two shapes:
        //   1. { "cookies": [...], "storage": [{origin, localStorage, ...}] }   (our format)
        //   2. { "cookies": [...], "local_storage": [{origin, items: [{key,value}]}] }  (legacy)
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        var cookies = new List<CookieEntry>();
        if (root.TryGetProperty("cookies", out var ca)
            && ca.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var el in ca.EnumerateArray())
            {
                var name   = TryString(el, "name");
                var domain = TryString(el, "domain");
                if (name is null || domain is null) continue;
                cookies.Add(new CookieEntry
                {
                    Name           = name,
                    Value          = TryString(el, "value") ?? "",
                    Domain         = domain,
                    Path           = TryString(el, "path") ?? "/",
                    Secure         = TryBool(el, "secure"),
                    HttpOnly       = TryBool(el, "httpOnly")
                                    || TryBool(el, "http_only"),
                    SameSite       = TryString(el, "sameSite")
                                    ?? TryString(el, "same_site"),
                    ExpiresUnixSec = TryLong(el, "expiresUnixSec")
                                    ?? TryLong(el, "expires")
                                    ?? TryLong(el, "expiry"),
                });
            }
        }

        var storage = new List<StorageEntry>();
        if (root.TryGetProperty("storage", out var sa)
            && sa.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var el in sa.EnumerateArray())
            {
                var origin = TryString(el, "origin");
                if (origin is null) continue;
                var local   = ReadDict(el, "localStorage");
                var session = ReadDict(el, "sessionStorage");
                storage.Add(new StorageEntry
                {
                    Origin = origin,
                    LocalStorage = local,
                    SessionStorage = session,
                });
            }
        }
        else if (root.TryGetProperty("local_storage", out var ls)
                 && ls.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            // Legacy [{origin, items:[{key,value}]}] shape.
            foreach (var el in ls.EnumerateArray())
            {
                var origin = TryString(el, "origin");
                if (origin is null) continue;
                var local = new Dictionary<string, string>();
                if (el.TryGetProperty("items", out var it)
                    && it.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var kv in it.EnumerateArray())
                    {
                        var k = TryString(kv, "key");
                        var v = TryString(kv, "value");
                        if (k is not null) local[k] = v ?? "";
                    }
                }
                storage.Add(new StorageEntry
                {
                    Origin = origin,
                    LocalStorage = local,
                });
            }
        }

        return new SessionPayload { Cookies = cookies, Storage = storage };
    }

    private static IReadOnlyDictionary<string, string> ReadDict(
        System.Text.Json.JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var dict)) return new Dictionary<string, string>();
        var result = new Dictionary<string, string>();
        if (dict.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var prop in dict.EnumerateObject())
                result[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();
        }
        return result;
    }

    private static string? TryString(System.Text.Json.JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() : null;

    private static bool TryBool(System.Text.Json.JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True  => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => false,
        };
    }

    private static long? TryLong(System.Text.Json.JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetDouble(out var d)) return (long)d;
        return null;
    }
}
