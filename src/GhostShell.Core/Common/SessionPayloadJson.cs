// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using System.Text.Json.Serialization;
using GhostShell.Core.Models;

namespace GhostShell.Core.Common;

/// <summary>
/// Single source-of-truth for snapshot / pack JSON serialization.
/// We pin <c>JsonSerializerOptions</c> here so the disk format is
/// stable across writers (the runtime, the import dialog, the export
/// button) — drift between them used to manifest as "exported pack
/// won't import" bugs in the legacy tree.
///
/// camelCase property names match the legacy Python format exactly
/// — packs exported from the web project import cleanly into the
/// desktop, and vice versa. Enums-as-strings is the same call.
/// </summary>
public static class SessionPayloadJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        WriteIndented          = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SerializePayload(SessionPayload p) =>
        JsonSerializer.Serialize(p, Options);

    public static SessionPayload? DeserializePayload(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<SessionPayload>(json, Options);

    public static string SerializeCookies(IReadOnlyList<CookieEntry> cookies) =>
        JsonSerializer.Serialize(cookies, Options);

    public static IReadOnlyList<CookieEntry> DeserializeCookies(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? Array.Empty<CookieEntry>()
            : JsonSerializer.Deserialize<List<CookieEntry>>(json, Options)
              ?? new List<CookieEntry>();

    public static string SerializeStorage(IReadOnlyList<StorageEntry> storage) =>
        JsonSerializer.Serialize(storage, Options);

    public static IReadOnlyList<StorageEntry> DeserializeStorage(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? Array.Empty<StorageEntry>()
            : JsonSerializer.Deserialize<List<StorageEntry>>(json, Options)
              ?? new List<StorageEntry>();
}
