// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.App.Dialogs;

/// <summary>
/// Per-action parameter schema. The typed-form dialog
/// (<see cref="ScriptStepParamsTypedDialog"/>) renders an input row
/// per <see cref="ParamField"/>; actions without a registered schema
/// fall back to the raw-JSON editor.
///
/// Kept simple intentionally — the editor's job is to make the
/// common cases ergonomic, not to fully model every action's
/// option space (which would mean reinventing the action handler
/// in C# data form).
/// </summary>
public static class ScriptActionSchema
{
    private static readonly Dictionary<string, IReadOnlyList<ParamField>> _schemas =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // ─── Navigation ──────────────────────────────────────────
        ["navigate"] = new[]
        {
            new ParamField("url", "URL", ParamFieldKind.Url, "https://example.com/", Required: true),
        },
        ["open_url"]  = new[] { new ParamField("url", "URL", ParamFieldKind.Url, "https://example.com/", Required: true) },
        ["visit"]     = new[] { new ParamField("url", "URL", ParamFieldKind.Url, "https://example.com/", Required: true) },
        ["new_tab"]   = new[] { new ParamField("url", "URL (or about:blank)", ParamFieldKind.Url, "about:blank") },

        // ─── Waits ───────────────────────────────────────────────
        ["dwell"] = new[]
        {
            new ParamField("min_ms", "Min wait (ms)", ParamFieldKind.Int, "2000"),
            new ParamField("max_ms", "Max wait (ms)", ParamFieldKind.Int, "5000"),
        },
        ["wait"] = new[]
        {
            new ParamField("min_ms", "Min wait (ms)", ParamFieldKind.Int, "1000"),
            new ParamField("max_ms", "Max wait (ms)", ParamFieldKind.Int, "2000"),
        },
        ["random_delay"] = new[]
        {
            new ParamField("min_ms", "Min delay (ms)", ParamFieldKind.Int, "200"),
            new ParamField("max_ms", "Max delay (ms)", ParamFieldKind.Int, "1500"),
        },
        ["wait_for_selector"] = new[]
        {
            new ParamField("selector",   "CSS selector",     ParamFieldKind.Selector, "", Required: true),
            new ParamField("timeout_ms", "Timeout (ms)",     ParamFieldKind.Int, "15000"),
        },
        ["wait_for_url"] = new[]
        {
            new ParamField("pattern",    "URL regex",        ParamFieldKind.String, "", Required: true),
            new ParamField("timeout_ms", "Timeout (ms)",     ParamFieldKind.Int, "15000"),
        },

        // ─── Interaction ─────────────────────────────────────────
        ["click_selector"] = new[] { new ParamField("selector", "CSS selector", ParamFieldKind.Selector, "", Required: true) },
        ["click"]          = new[] { new ParamField("selector", "CSS selector", ParamFieldKind.Selector, "", Required: true) },
        ["double_click"]   = new[] { new ParamField("selector", "CSS selector", ParamFieldKind.Selector, "", Required: true) },
        ["right_click"]    = new[] { new ParamField("selector", "CSS selector", ParamFieldKind.Selector, "", Required: true) },
        ["hover"]          = new[] { new ParamField("selector", "CSS selector", ParamFieldKind.Selector, "", Required: true) },
        ["type"] = new[]
        {
            new ParamField("selector", "CSS selector", ParamFieldKind.Selector, "", Required: true),
            new ParamField("text",     "Text",         ParamFieldKind.String,   "", Required: true),
            new ParamField("min_ms",   "Min keystroke gap (ms)", ParamFieldKind.Int, "40"),
            new ParamField("max_ms",   "Max keystroke gap (ms)", ParamFieldKind.Int, "180"),
        },
        ["press_key"] = new[]
        {
            new ParamField("key", "Key", ParamFieldKind.Select, "Enter",
                Options: new[] { "Enter", "Tab", "Escape", "ArrowDown", "ArrowUp", "ArrowLeft", "ArrowRight", "PageDown", "PageUp", " ", "Backspace" }),
        },
        ["scroll"]            = new[] { new ParamField("seconds", "Total seconds", ParamFieldKind.Int, "6") },
        ["scroll_to_bottom"]  = Array.Empty<ParamField>(),
        ["move_random"] = new[]
        {
            new ParamField("min_ms", "Min ms", ParamFieldKind.Int, "300"),
            new ParamField("max_ms", "Max ms", ParamFieldKind.Int, "900"),
        },

        // ─── Data ────────────────────────────────────────────────
        ["save_var"] = new[]
        {
            new ParamField("name",  "Var name",   ParamFieldKind.String, "",    Required: true),
            new ParamField("value", "Value",      ParamFieldKind.String, ""),
        },
        ["extract_text"] = new[]
        {
            new ParamField("selector", "CSS selector", ParamFieldKind.Selector, "body"),
            new ParamField("save_as",  "Save as var",  ParamFieldKind.String,   "text"),
        },
        ["read"] = new[]
        {
            new ParamField("selector", "CSS selector", ParamFieldKind.Selector, "body"),
            new ParamField("save_as",  "Save as var",  ParamFieldKind.String,   "text"),
        },
        ["execute_js"] = new[]
        {
            new ParamField("code", "JavaScript", ParamFieldKind.Multiline,
                "// Runs in the page context. The return value is\n// available to the runner but not yet captured to a var.\nreturn document.title;"),
        },

        // ─── Ads / captcha / misc ────────────────────────────────
        ["parse_ads"]   = Array.Empty<ParamField>(),
        ["catch_ads"]   = Array.Empty<ParamField>(),
        ["click_ad"]    = new[]
        {
            // Phase 38 — full web-parity schema. The runner currently
            // implements the click + own-domain guard + post-click dwell
            // (2.5–6 s hardcoded). The remaining fields are accepted as
            // params for forward-compat with the legacy ghost_shell_browser
            // and read by the click-ad analytics so existing scripts
            // don't lose their settings on round-trip.
            new ParamField("stamp_id",           "Stamp id (-1 = random)",        ParamFieldKind.Int,  "-1"),
            new ParamField("dwell_min",          "Min landing dwell (sec)",       ParamFieldKind.Int,  "6"),
            new ParamField("dwell_max",          "Max landing dwell (sec)",       ParamFieldKind.Int,  "18"),
            new ParamField("scroll_after_click", "Scroll the landing page",       ParamFieldKind.Bool, "true"),
            new ParamField("close_after",        "Close tab after dwell",         ParamFieldKind.Bool, "true"),
            new ParamField("deep_dive",          "Click a 2nd link on landing",   ParamFieldKind.Bool, "false"),
        },
        ["solve_captcha"] = new[]
        {
            new ParamField("timeout_sec", "Timeout (seconds)", ParamFieldKind.Int, "180"),
        },
        ["screenshot"] = new[]
        {
            new ParamField("path",    "Path (supports {{vars}})", ParamFieldKind.String, "screenshots/{{ts}}.png"),
            new ParamField("save_as", "Save path to var",         ParamFieldKind.String, ""),
        },
        ["log"] = new[] { new ParamField("message", "Message", ParamFieldKind.String, "") },

        // No-arg actions
        ["back"]      = Array.Empty<ParamField>(),
        ["forward"]   = Array.Empty<ParamField>(),
        ["reload"]    = Array.Empty<ParamField>(),
        ["close_tab"] = Array.Empty<ParamField>(),
        ["break"]     = Array.Empty<ParamField>(),
        ["continue"]  = Array.Empty<ParamField>(),

        // ─── Control flow (typed forms) ───────────────────────────
        // The condition tree on `if` / `while_loop` is still authored
        // through the JSON view (we don't have a graphical condition
        // builder yet). Body / then / else lists are nested step
        // arrays edited via the visual editor itself — they live on
        // the step record outside the params bag.
        ["foreach"] = new[]
        {
            // Two source modes:
            //   • "inline" (default) — read items directly from the
            //     CSV/varname string in `items`.
            //   • "csv_file" — read a CSV file, pull values from
            //     a named column (or column index "0", "1", …).
            new ParamField("source", "Source", ParamFieldKind.Select, "inline",
                Options: new[] { "inline", "csv_file" }),
            new ParamField("items",      "Items (inline CSV or $varname)", ParamFieldKind.String, "a,b,c"),
            new ParamField("csv_path",   "CSV file path (csv_file mode)",  ParamFieldKind.String, ""),
            new ParamField("csv_column", "CSV column (name or index)",      ParamFieldKind.String, "0"),
            new ParamField("csv_has_header", "CSV has header row",          ParamFieldKind.Bool,   "true"),
            new ParamField("var",     "Iterator var name", ParamFieldKind.String, "item"),
            new ParamField("shuffle", "Randomise order",   ParamFieldKind.Bool,   "false"),
            new ParamField("limit",   "Limit (0 = no cap)", ParamFieldKind.Int,   "0"),
        },
        ["foreach_ad"] = new[]
        {
            new ParamField("shuffle",          "Randomise ad order",   ParamFieldKind.Bool, "false"),
            new ParamField("limit",            "Limit (0 = no cap)",   ParamFieldKind.Int,  "0"),
            new ParamField("scan_between_ads", "Pause between ads",    ParamFieldKind.Bool, "true"),
            new ParamField("scan_dwell_min",   "Min between-ad (sec)", ParamFieldKind.Int,  "3"),
            new ParamField("scan_dwell_max",   "Max between-ad (sec)", ParamFieldKind.Int,  "8"),
        },
        // The `if` step has no `params.*` keys of its own — the
        // condition tree lives in step.condition and is edited via
        // the dedicated Condition panel that the typed-form dialog
        // renders for "if" / "while_loop". HasSchema("if") still
        // returns true (the key exists), so the dialog stays in
        // form mode and the condition panel is shown; the empty
        // params list just means no extra rows appear below it.
        // (Use Array.Empty<T>() — `new[] { }` can't infer the
        // element type, same reason `extension_close` uses it.)
        ["if"] = Array.Empty<ParamField>(),
        ["while_loop"] = new[]
        {
            // Loop condition lives in step.condition (edited via the
            // Condition panel — same as `if`). max_iterations gives
            // the loop a hard cap so it can't spin forever.
            new ParamField("max_iterations", "Max iterations (cap)", ParamFieldKind.Int, "1000"),
        },
        ["switch_tab"] = new[]
        {
            new ParamField("index", "Tab index (0-based)", ParamFieldKind.Int, "0"),
        },
        ["pause"] = new[]
        {
            new ParamField("min_sec", "Min seconds", ParamFieldKind.Int, "3"),
            new ParamField("max_sec", "Max seconds", ParamFieldKind.Int, "8"),
        },

        // ─── Search compound actions (Phase 38 — web parity) ──────
        // search_query and commercial_inflate are compound actions
        // ported from the legacy ghost_shell_browser. They wrap
        // navigate + wait + parse_ads + (optional) retry-on-empty
        // into a single step so user-authored scripts stay short.
        ["search_query"] = new[]
        {
            new ParamField("query",         "Search query (supports {{vars}})", ParamFieldKind.String, "{{item}}", Required: true),
            new ParamField("locale",        "Google locale (hl=...)",           ParamFieldKind.String, "uk"),
            new ParamField("max_attempts",  "Retry on empty SERP (count)",      ParamFieldKind.Int,    "4"),
            new ParamField("retry_min_sec", "Retry pause min (sec)",            ParamFieldKind.Int,    "13"),
            new ParamField("retry_max_sec", "Retry pause max (sec)",            ParamFieldKind.Int,    "15"),
            new ParamField("fail_on_empty", "Throw if no ads after retries",    ParamFieldKind.Bool,   "false"),
            new ParamField("timeout_ms",    "wait_for_selector timeout (ms)",   ParamFieldKind.Int,    "12000"),
        },
        ["commercial_inflate"] = new[]
        {
            new ParamField("brand",         "Brand seed (supports {{vars}})",   ParamFieldKind.String, "{{item}}", Required: true),
            new ParamField("n",             "Pre-warm queries to fire",         ParamFieldKind.Int,    "2"),
            new ParamField("locale",        "Google locale (hl=...)",           ParamFieldKind.String, "uk"),
            new ParamField("dwell_min",     "Per-query dwell min (sec)",        ParamFieldKind.Int,    "4"),
            new ParamField("dwell_max",     "Per-query dwell max (sec)",        ParamFieldKind.Int,    "10"),
            new ParamField("click_organic", "Click first organic result",       ParamFieldKind.Bool,   "false"),
        },
        ["refresh"] = new[]
        {
            new ParamField("max_attempts",  "Max reloads",            ParamFieldKind.Int, "3"),
            new ParamField("delay_min_sec", "Min delay between (sec)", ParamFieldKind.Int, "3"),
            new ParamField("delay_max_sec", "Max delay between (sec)", ParamFieldKind.Int, "8"),
        },
        ["rotate_ip"] = new[]
        {
            new ParamField("wait_after_sec", "Wait after (sec)", ParamFieldKind.Int, "4"),
        },
        ["http_request"] = new[]
        {
            new ParamField("method", "Method", ParamFieldKind.Select, "POST",
                Options: new[] { "GET", "POST", "PUT", "DELETE" }),
            new ParamField("url", "URL", ParamFieldKind.Url, "https://", Required: true),
            new ParamField("body", "Body (JSON or text)", ParamFieldKind.Multiline, "{}"),
            new ParamField("save_as",     "Save response to var", ParamFieldKind.String, ""),
            new ParamField("timeout_sec", "Timeout (sec)",        ParamFieldKind.Int,    "15"),
        },

        // ─── Phase 19 — extension automation ──────────────────────
        ["open_extension_popup"] = new[]
        {
            new ParamField("extension_id",        "Extension ID (32-char)",         ParamFieldKind.String, "", Required: true),
            new ParamField("page",                "Page (popup.html / options.html)", ParamFieldKind.String, "popup.html"),
            new ParamField("wait_for_selector",   "Wait for selector (optional)",   ParamFieldKind.Selector, ""),
            new ParamField("timeout_sec",         "Timeout (sec)",                  ParamFieldKind.Int,    "15"),
        },
        ["open_extension_page"] = new[]
        {
            new ParamField("extension_id",        "Extension ID (32-char)",         ParamFieldKind.String, "", Required: true),
            new ParamField("page",                "Page name",                      ParamFieldKind.String, "popup.html"),
            new ParamField("wait_for_selector",   "Wait for selector (optional)",   ParamFieldKind.Selector, ""),
            new ParamField("timeout_sec",         "Timeout (sec)",                  ParamFieldKind.Int,    "15"),
        },
        ["extension_eval"] = new[]
        {
            new ParamField("code",      "JavaScript",          ParamFieldKind.Multiline, "// runs inside the extension tab\nreturn document.title;", Required: true),
            new ParamField("store_as",  "Save return as var",  ParamFieldKind.String,    ""),
        },
        ["extension_wait_for"] = new[]
        {
            new ParamField("selector",      "CSS selector",     ParamFieldKind.Selector, "", Required: true),
            new ParamField("timeout_sec",   "Timeout (sec)",    ParamFieldKind.Int,      "15"),
            new ParamField("save_as",       "Save text as var", ParamFieldKind.String,   ""),
        },
        ["extension_click"] = new[]
        {
            new ParamField("selector",     "CSS selector",  ParamFieldKind.Selector, "", Required: true),
            new ParamField("timeout_sec",  "Timeout (sec)", ParamFieldKind.Int,      "10"),
        },
        ["extension_fill"] = new[]
        {
            new ParamField("selector",     "CSS selector",     ParamFieldKind.Selector, "", Required: true),
            new ParamField("value",        "Value (supports {{vars}})", ParamFieldKind.String, "", Required: true),
            new ParamField("clear_first",  "Clear before typing", ParamFieldKind.Bool,    "true"),
        },
        ["extension_close"] = Array.Empty<ParamField>(),
    };

    /// <summary>True if a typed schema exists for the action.</summary>
    public static bool HasSchema(string actionType)
        => _schemas.ContainsKey(actionType);

    /// <summary>Schema for the action (empty array if none / unknown).</summary>
    public static IReadOnlyList<ParamField> Get(string actionType)
        => _schemas.TryGetValue(actionType, out var fs) ? fs : Array.Empty<ParamField>();
}

public enum ParamFieldKind
{
    String,
    Multiline,
    Int,
    Selector,
    Url,
    Bool,
    Select,
}

/// <summary>One row in a typed param form.</summary>
public sealed record ParamField(
    string Name,
    string Label,
    ParamFieldKind Kind,
    string DefaultValue = "",
    bool Required = false,
    IReadOnlyList<string>? Options = null);
