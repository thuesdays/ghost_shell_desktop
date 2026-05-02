// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// One programmable browsing-automation script. Mirrors the legacy
/// Python project's <c>scripts</c> table 1:1 so future migrations
/// can move data either way.
///
/// <see cref="StepsJson"/> is the canonical representation —
/// <see cref="ScriptStep"/> instances are deserialised on read
/// only when actually needed (editor / runner). Saving from the
/// editor writes the full array back atomically.
/// </summary>
public sealed record Script
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>JSON-array of <see cref="ScriptStep"/>. Default "[]".</summary>
    public string StepsJson { get; init; } = "[]";

    // ─── Phase 21: Graph-mode editor ─────────────────────────────
    //
    // Two execution models live side-by-side:
    //   • list   — StepsJson interpreted top-to-bottom (legacy).
    //   • graph  — NodesJson + EdgesJson; runtime traverses outgoing
    //              edges from the entry node (one with no inbound).
    //
    // Switching modes is destructive — the editor warns the user
    // before flipping, and the unselected payload is preserved on
    // the row so the user can flip back without losing work.

    /// <summary>Layout / execution mode. <c>"list"</c> (default) or
    /// <c>"graph"</c>. The runner picks the correct traversal based
    /// on this value.</summary>
    public string LayoutMode { get; init; } = "list";

    /// <summary>
    /// JSON array of graph nodes when <see cref="LayoutMode"/>=graph.
    /// Each node:
    /// <code>
    ///   { "id": "n1", "x": 100, "y": 200,
    ///     "type": "navigate", "params": {...}, "enabled": true }
    /// </code>
    /// Null when layout=list.
    /// </summary>
    public string? NodesJson { get; init; }

    /// <summary>
    /// JSON array of graph edges when <see cref="LayoutMode"/>=graph.
    /// Each edge: <code>{ "from": "n1", "to": "n2", "label": "then" }</code>.
    /// The optional <c>label</c> distinguishes branch outputs from an
    /// <c>if</c> node ("then" / "else"). Null when layout=list.
    /// </summary>
    public string? EdgesJson { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When true, this script auto-applies to new profiles created
    /// after now. At most one default exists at a time —
    /// <see cref="IScriptService.SetDefaultAsync"/> clears the flag
    /// on the previous default.
    /// </summary>
    public bool IsDefault { get; init; } = false;

    /// <summary>Optimistic-concurrency token. Editor refuses to save
    /// when the local etag drifted from the DB value (two-tab race).</summary>
    public string ETag { get; init; } = "";

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// One step inside a script. Mirrors the legacy step shape:
/// <c>{"type": "navigate", "params": {"url": "..."}}</c>.
///
/// Control-flow steps carry sub-step arrays:
///   • <c>if</c>      — <see cref="Then"/> + <see cref="Else"/>
///   • <c>foreach</c> — <see cref="Body"/>
///   • <c>foreach_ad</c> — <see cref="Body"/>
///
/// Conditions are typed (<see cref="Condition"/>) instead of stuffed
/// into Params so the editor can validate them without parsing the
/// untyped bag.
/// </summary>
public sealed record ScriptStep
{
    /// <summary>Action id from the catalog (e.g. "navigate", "click").</summary>
    public required string Type { get; init; }

    /// <summary>Free-form parameter map; parsed by the action handler.</summary>
    public IReadOnlyDictionary<string, object?> Params { get; init; }
        = new Dictionary<string, object?>();

    /// <summary>If false, the runner skips this step at execution time
    /// (the editor uses this to "comment out" a step without deleting).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>0–1 probabilistic gate. Defaults to 1.0 (always run).</summary>
    public double Probability { get; init; } = 1.0;

    /// <summary>If true, a thrown handler aborts the whole script run.</summary>
    public bool AbortOnError { get; init; } = false;

    // ─── Per-step ad-domain filters (Phase 17 parity with web) ────
    //
    // These four flags only matter inside a <c>foreach_ad</c> body —
    // they early-skip the step when the current ad's domain doesn't
    // match the policy. Outside an ad loop they're inert (the runner
    // has no current ad to test against, so the gate evaluates true).
    //
    // The two pairs are mutually-exclusive in spirit but not enforced;
    // setting both <c>OnlyOnTarget=true</c> and <c>SkipOnTarget=true</c>
    // means "never run", which is a valid (if odd) policy.

    /// <summary>Skip when the current ad's domain matches one of the
    /// profile's <c>my_domains</c> (configured per-profile).</summary>
    public bool SkipOnMyDomain { get; init; } = false;

    /// <summary>Skip when the current ad's domain matches one of the
    /// profile's <c>target_domains</c>.</summary>
    public bool SkipOnTarget { get; init; } = false;

    /// <summary>Run ONLY when the current ad is on a target domain
    /// (skip otherwise).</summary>
    public bool OnlyOnTarget { get; init; } = false;

    /// <summary>Run ONLY when the current ad is on one of the
    /// profile's own domains (skip otherwise).</summary>
    public bool OnlyOnMyDomain { get; init; } = false;

    /// <summary>Skip when the current ad's domain is in the block list
    /// (domains to ignore entirely).</summary>
    public bool SkipOnBlocked { get; init; } = false;

    /// <summary>Run ONLY when the current ad is on the block list
    /// (debug-only inverse; rare usage).</summary>
    public bool OnlyOnBlocked { get; init; } = false;

    /// <summary>Human-readable label shown in the editor; optional.</summary>
    public string? Label { get; init; }

    // ─── Control-flow extensions ──────────────────────────────────

    /// <summary>For <c>if</c>: the predicate evaluated before branching.</summary>
    public ScriptCondition? Condition { get; init; }

    /// <summary>For <c>if</c>: steps executed when <see cref="Condition"/> matches.</summary>
    public IReadOnlyList<ScriptStep> Then { get; init; } = Array.Empty<ScriptStep>();

    /// <summary>For <c>if</c>: steps executed when <see cref="Condition"/> doesn't match.</summary>
    public IReadOnlyList<ScriptStep> Else { get; init; } = Array.Empty<ScriptStep>();

    /// <summary>For <c>foreach</c> / <c>foreach_ad</c>: the per-iteration body.</summary>
    public IReadOnlyList<ScriptStep> Body { get; init; } = Array.Empty<ScriptStep>();
}

/// <summary>
/// One condition node. Compound forms (and / or / not) carry
/// <see cref="Children"/>; leaf forms read <see cref="Params"/>
/// (e.g. <c>kind="var_equals"</c> with Params={"name":"x","value":"5"}).
/// </summary>
public sealed record ScriptCondition
{
    /// <summary>Condition kind (see ConditionEvaluator catalog).</summary>
    public required string Kind { get; init; }

    public IReadOnlyDictionary<string, object?> Params { get; init; }
        = new Dictionary<string, object?>();

    /// <summary>For <c>and</c> / <c>or</c> / <c>not</c>.</summary>
    public IReadOnlyList<ScriptCondition> Children { get; init; }
        = Array.Empty<ScriptCondition>();
}

/// <summary>One execution of a script against one profile.</summary>
public sealed record ScriptRun
{
    public long Id { get; init; }
    public required long ScriptId { get; init; }
    public required string ProfileName { get; init; }

    public required DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }

    /// <summary>One of: <c>running | ok | partial | failed | cancelled</c>.</summary>
    public required string Status { get; init; }

    public int StepsExecuted { get; init; }
    public int StepsFailed   { get; init; }
    public int AdsClicked    { get; init; }

    public double? DurationSec { get; init; }
    public string? LastError   { get; init; }

    /// <summary>Per-step log: JSON array of {idx, type, ok, err, dur_ms}.</summary>
    public string? LogJson { get; init; }
}
