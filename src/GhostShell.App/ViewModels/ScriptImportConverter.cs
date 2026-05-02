// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 35 — translate the legacy <c>ghost_shell_browser</c> JSON
/// flow format into the desktop runner's step shape so users can
/// import scripts written for the web build without manual editing.
///
/// Why this exists
/// ---------------
/// The two projects evolved their step JSON in parallel and ended up
/// with almost-identical-but-not-quite shapes:
///   • Root key:     web=<c>flow</c>     desktop=<c>steps</c>
///   • Loop step:    web=<c>loop</c>     desktop=<c>foreach</c>
///   • Loop fields:  web has <c>item_var</c>/<c>items</c>/<c>steps</c>
///                   side-by-side with the type;
///                   desktop puts <c>var</c>/<c>items</c>/<c>shuffle</c>
///                   under <c>params</c> and renames <c>steps</c>→<c>body</c>.
///   • If branches:  web=<c>then_steps</c>/<c>else_steps</c>
///                   desktop=<c>then</c>/<c>else</c>
///   • foreach_ad:   both projects use the same name, but web puts
///                   the iterated body under <c>steps</c> and desktop
///                   under <c>body</c>.
///   • Leaf params:  web inlines them next to <c>type</c> (e.g.
///                   <c>"dwell_min": 6</c>); desktop wants them
///                   nested under <c>params</c>. The exception is
///                   the per-step ad-domain flags (<c>skip_on_my_domain</c>,
///                   <c>only_on_target</c>, etc.) which the desktop
///                   ALSO keeps at the top level.
///   • Variables:    web uses <c>{name}</c> single braces; desktop's
///                   InterpolateVars regex requires <c>{{name}}</c>.
///
/// This file's <see cref="ConvertWebToDesktop"/> walks the imported
/// JSON tree, rewrites the structural keys, and bumps every
/// single-brace <c>{ident}</c> in string values up to <c>{{ident}}</c>.
/// Output is a JSON array string ready to drop into
/// <c>Script.StepsJson</c>. Idempotent: feeding desktop-shape JSON
/// through the converter is a near no-op (the recogniser checks
/// for the web markers before transforming).
/// </summary>
internal static class ScriptImportConverter
{
    /// <summary>Top-level fields the desktop runner reads at the
    /// step root. Anything else gets moved into <c>params</c>.</summary>
    private static readonly HashSet<string> TopLevelStepFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "enabled", "probability", "abort_on_error", "label", "_comment",
        "condition", "then", "else", "body", "params",
        "skip_on_my_domain", "skip_on_target",
        "only_on_my_domain", "only_on_target",
        "skip_on_blocked", "only_on_blocked",
    };

    /// <summary>Returns the steps JSON-array string normalised to
    /// the desktop schema. Always returns a valid JSON array;
    /// throws on root-level shape we can't make sense of.</summary>
    public static (string StepsJson, string Name, string Description) Normalise(string raw)
    {
        var node = JsonNode.Parse(raw)
            ?? throw new InvalidOperationException("expected JSON, got empty input");

        // Bare-array form — used when a user exports just the steps.
        if (node is JsonArray arr)
        {
            var normalised = NormaliseStepArray(arr);
            return (normalised.ToJsonString(), "", "");
        }

        // Object form — the common export shape from either project.
        if (node is JsonObject obj)
        {
            // Pick up name / description if present.
            var name = (obj["name"] as JsonValue)?.GetValue<string>() ?? "";
            var desc = (obj["description"] as JsonValue)?.GetValue<string>() ?? "";

            // Web uses "flow", desktop uses "steps". Either is fine.
            JsonArray? stepsArr =
                  obj["steps"] as JsonArray
               ?? obj["flow"]  as JsonArray;
            if (stepsArr is null)
                throw new InvalidOperationException(
                    "missing 'steps' or 'flow' array at the JSON root");

            var normalised = NormaliseStepArray(stepsArr);
            return (normalised.ToJsonString(), name, desc);
        }

        throw new InvalidOperationException(
            "expected a JSON array of steps or an object with a 'steps' / 'flow' property");
    }

    // ─── recursive normaliser ───────────────────────────────────────

    private static JsonArray NormaliseStepArray(JsonArray src)
    {
        var dst = new JsonArray();
        foreach (var n in src)
        {
            if (n is JsonObject step)
                dst.Add(NormaliseStep(step));
            // Skip non-object entries silently — they wouldn't
            // deserialise as steps anyway.
        }
        return dst;
    }

    private static JsonObject NormaliseStep(JsonObject src)
    {
        var dst = new JsonObject();

        // 1. type — with web→desktop rename for the loop step.
        var rawType = (src["type"] as JsonValue)?.GetValue<string>()?.ToLowerInvariant() ?? "";
        var type = rawType switch
        {
            "loop" => "foreach",
            _      => rawType,
        };
        dst["type"] = type;

        // 2. Walk the source's other keys. Top-level fields (enabled,
        // probability, condition, _comment, ad flags) get copied
        // verbatim. Web-specific structural keys get rewritten.
        // Anything else is treated as a leaf "param" and bucketed
        // under params.
        JsonObject paramsObj = (src["params"] is JsonObject p) ? CloneObject(p) : new JsonObject();
        var keysHandled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "type", "params" };

        foreach (var kv in src)
        {
            if (keysHandled.Contains(kv.Key)) continue;
            if (string.Equals(kv.Key, "type", StringComparison.OrdinalIgnoreCase)) continue;

            // Web → desktop structural rewrites.
            switch (kv.Key.ToLowerInvariant())
            {
                case "steps":  // web's foreach/loop/foreach_ad body
                    if (kv.Value is JsonArray bodyArr)
                        dst["body"] = NormaliseStepArray(bodyArr);
                    keysHandled.Add(kv.Key);
                    continue;
                case "then_steps":
                    if (kv.Value is JsonArray thenArr)
                        dst["then"] = NormaliseStepArray(thenArr);
                    keysHandled.Add(kv.Key);
                    continue;
                case "else_steps":
                    if (kv.Value is JsonArray elseArr)
                        dst["else"] = NormaliseStepArray(elseArr);
                    keysHandled.Add(kv.Key);
                    continue;
                case "item_var":  // web's loop var name
                    paramsObj["var"] = CloneNode(kv.Value);
                    keysHandled.Add(kv.Key);
                    continue;
                case "then":  // already-desktop input — recurse
                    if (kv.Value is JsonArray thenArr2)
                        dst["then"] = NormaliseStepArray(thenArr2);
                    keysHandled.Add(kv.Key);
                    continue;
                case "else":
                    if (kv.Value is JsonArray elseArr2)
                        dst["else"] = NormaliseStepArray(elseArr2);
                    keysHandled.Add(kv.Key);
                    continue;
                case "body":
                    if (kv.Value is JsonArray bodyArr2)
                        dst["body"] = NormaliseStepArray(bodyArr2);
                    keysHandled.Add(kv.Key);
                    continue;
            }

            // Top-level desktop fields stay at the root (enabled,
            // probability, condition, _comment, all ad flags). The
            // condition node also has its own normaliser.
            if (TopLevelStepFields.Contains(kv.Key))
            {
                dst[kv.Key] = kv.Key.Equals("condition", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonObject condObj
                    ? NormaliseCondition(condObj)
                    : InterpolateInPlace(CloneNode(kv.Value));
                keysHandled.Add(kv.Key);
                continue;
            }

            // Anything else is a leaf parameter — move it under
            // `params`, with single-brace → double-brace rewrite for
            // string values.
            paramsObj[kv.Key] = InterpolateInPlace(CloneNode(kv.Value));
            keysHandled.Add(kv.Key);
        }

        // Only emit `params` if non-empty — keeps round-tripped JSON
        // tidy for steps that have no parameters at all (e.g. parse_ads).
        if (paramsObj.Count > 0)
            dst["params"] = paramsObj;

        return dst;
    }

    private static JsonObject NormaliseCondition(JsonObject src)
    {
        // Conditions are simpler: { kind, params, children } and the
        // kinds match between projects. Only the children array might
        // need recursion.
        var dst = CloneObject(src);
        if (dst["children"] is JsonArray kids)
        {
            var rebuilt = new JsonArray();
            foreach (var k in kids)
            {
                if (k is JsonObject ko) rebuilt.Add(NormaliseCondition(ko));
            }
            dst["children"] = rebuilt;
        }
        return dst;
    }

    // ─── { var } → {{ var }} rewrite ─────────────────────────────────

    /// <summary>Rewrites <c>{ident}</c> tokens into <c>{{ident}}</c>
    /// inside string values — and recurses into objects/arrays. Skips
    /// any token already wrapped in double braces. Tokens must look
    /// like a C identifier so we don't accidentally damage URLs that
    /// contain literal <c>{anything}</c> braces.</summary>
    private static readonly Regex SingleBracePattern = new(
        @"(?<!\{)\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}(?!\})",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private static JsonNode? InterpolateInPlace(JsonNode? n)
    {
        switch (n)
        {
            case null: return null;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(SingleBracePattern.Replace(s, "{{$1}}"));
            case JsonObject o:
            {
                var clone = new JsonObject();
                foreach (var kv in o) clone[kv.Key] = InterpolateInPlace(CloneNode(kv.Value));
                return clone;
            }
            case JsonArray a:
            {
                var clone = new JsonArray();
                foreach (var item in a) clone.Add(InterpolateInPlace(CloneNode(item)));
                return clone;
            }
            default:
                return CloneNode(n);
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────

    /// <summary>JsonNode is parented to its tree, so to copy a value
    /// into a new container we have to deep-clone via re-parse.</summary>
    private static JsonNode? CloneNode(JsonNode? n) =>
        n is null ? null : JsonNode.Parse(n.ToJsonString());

    private static JsonObject CloneObject(JsonObject n) =>
        (JsonObject)(JsonNode.Parse(n.ToJsonString()) ?? new JsonObject());
}
