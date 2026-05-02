// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.IO;
using System.Text.Json;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Phase-12 iter-2/4/5/6 runner. Owns:
///   • Control flow: if / foreach / break / continue (recursive)
///   • Variables via RunContext.Vars
///   • Ad parsing (parse_ads → ctx.Ads), click_ad with own-domain
///     guard, foreach_ad iteration
///   • Humanised type / click / scroll
///   • 25+ action handlers across navigation, interaction, data,
///     timing, tabs, screenshot
///
/// Anything an action handler doesn't explicitly support is logged
/// as a soft failure (continues unless the step has AbortOnError).
/// </summary>
public sealed class ScriptRunner : IScriptRunner
{
    private readonly IScriptService _scripts;
    private readonly IDomainListService _domainLists;
    private readonly IAdDensityService _adDensity;
    private readonly ICompetitorService _competitors;
    private readonly ICaptchaSolver? _captcha;
    private readonly ILogger<ScriptRunner> _log;
    private readonly ConditionEvaluator _conditions = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public ScriptRunner(
        IScriptService scripts,
        IDomainListService domainLists,
        IAdDensityService adDensity,
        ICompetitorService competitors,
        ILogger<ScriptRunner> log,
        ICaptchaSolver? captcha = null)
    {
        _scripts = scripts;
        _domainLists = domainLists;
        _adDensity = adDensity;
        _competitors = competitors;
        _captcha = captcha;
        _log     = log;
    }

    public async Task<ScriptRun> ExecuteAsync(
        Script script, IBrowserSession session, string profileName,
        CancellationToken ct = default,
        IEnumerable<string>? myDomains = null,
        IEnumerable<string>? targetDomains = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? vault = null)
    {
        var startedAt = DateTime.UtcNow;
        var stepLog = new List<Dictionary<string, object?>>();
        var ctx = new RunContext();
        // Seed the domain sets so per-step filters / ad-aware
        // conditions can resolve. Empty input → empty set → filters
        // pass through (no behaviour change versus pre-Phase-19).
        if (myDomains is not null)
            foreach (var d in myDomains)
            {
                var n = NormaliseDomain(d);
                if (!string.IsNullOrEmpty(n)) ctx.MyDomains.Add(n);
            }
        if (targetDomains is not null)
            foreach (var d in targetDomains)
            {
                var n = NormaliseDomain(d);
                if (!string.IsNullOrEmpty(n)) ctx.TargetDomains.Add(n);
            }
        // Phase 34: merge global domain lists from IDomainListService.
        // Profile-specific CSV stays as overrides; these provide the
        // baseline for all profiles from the Domains page.
        try
        {
            var globalDomains = await _domainLists.ListAllAsync(ct);
            foreach (var entry in globalDomains)
            {
                var normalised = NormaliseDomain(entry.Domain);
                if (string.IsNullOrEmpty(normalised)) continue;
                switch (entry.Kind)
                {
                    case DomainListKind.My:
                        ctx.MyDomains.Add(normalised);
                        break;
                    case DomainListKind.Target:
                        ctx.TargetDomains.Add(normalised);
                        break;
                    case DomainListKind.Block:
                        ctx.BlockDomains.Add(normalised);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load global domain lists; script continues with profile-only domains");
        }
        // Phase 24 — pre-resolved vault references. The caller (the
        // profile runner) decrypts the items the script needs before
        // we start executing, so the runner never holds the master key.
        if (vault is not null)
            foreach (var kv in vault) ctx.Vault[kv.Key] = kv.Value;
        var counters = new RunCounters();
        string? lastError = null;
        var aborted = false;

        var run = new ScriptRun
        {
            ScriptId    = script.Id,
            ProfileName = profileName,
            StartedAt   = startedAt,
            Status      = "running",
        };
        var runId = await _scripts.RecordRunAsync(run, ct);

        // Phase 21: choose execution model from script.LayoutMode.
        // Default ("list", null, or any unknown value) → existing
        // sequential traversal. "graph" → parse Nodes+Edges and
        // walk the graph from the entry node.
        var layoutMode = (script.LayoutMode ?? "list").ToLowerInvariant();
        var isGraph = layoutMode == "graph";

        IReadOnlyList<ScriptStep> steps;
        GraphTraverser.ParsedGraph? graph = null;

        if (isGraph)
        {
            graph = GraphTraverser.Parse(script.NodesJson, script.EdgesJson);
            if (graph is null)
            {
                _log.LogError("Script #{Id} layout=graph but nodes_json invalid/empty", script.Id);
                return Finalise(runId, startedAt, "failed", counters,
                    "graph nodes_json missing or invalid", stepLog, ctx);
            }
            steps = Array.Empty<ScriptStep>(); // unused in graph mode
        }
        else
        {
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
        }

        try
        {
            if (isGraph)
                await ExecuteGraphAsync(graph!, session, ctx, counters, stepLog, runId, profileName, ct);
            else
                await ExecuteStepsAsync(steps, session, ctx, counters, stepLog, runId, profileName, ct);
        }
        catch (OperationCanceledException)
        {
            aborted = true;
            lastError = "cancelled";
        }
        catch (ScriptAbortException ex)
        {
            aborted = true;
            lastError = ex.Message;
        }

        var status = aborted                            ? "cancelled"
                   : counters.Failed == 0 && counters.Executed > 0 ? "ok"
                   : counters.Executed > 0              ? "partial"
                   : "failed";
        return Finalise(runId, startedAt, status, counters, lastError, stepLog, ctx);
    }

    /// <summary>
    /// Outcome flag bubbled up through the recursive step executor.
    /// Replaces the previous exception-based control flow — exceptions
    /// for break/continue could leak past their intended loop scope
    /// (audit Phase 13 #3) and were a debugging hazard.
    /// </summary>
    private enum StepFlow
    {
        Normal,
        Break,
        Continue,
    }

    /// <summary>
    /// Run a list of steps in order. Returns:
    ///   • <see cref="StepFlow.Normal"/> when the list completed
    ///   • <see cref="StepFlow.Break"/> when a break step fired
    ///   • <see cref="StepFlow.Continue"/> when a continue step fired
    /// The surrounding loop (foreach / foreach_ad) reacts to the
    /// returned flow; non-loop callers treat both flow values as
    /// "stop processing siblings".
    /// </summary>
    private async Task<StepFlow> ExecuteStepsAsync(
        IReadOnlyList<ScriptStep> steps, IBrowserSession session,
        RunContext ctx, RunCounters counters,
        List<Dictionary<string, object?>> log, long runId, string profileName, CancellationToken ct)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = steps[i];
            var entry = new Dictionary<string, object?>
            {
                ["idx"]  = i,
                ["type"] = step.Type,
                ["ok"]   = false,
            };

            var skipReason = "";
            if (!step.Enabled)
            {
                entry["skipped"] = "disabled";
                skipReason = "disabled";
                log.Add(entry);
                // Record ActionEvent for skipped click_ad steps.
                if (string.Equals(step.Type, "click_ad", StringComparison.OrdinalIgnoreCase))
                {
                    await RecordActionEventAsync(
                        step, ctx, runId, profileName, "skipped", skipReason, 0, null, ct);
                }
                continue;
            }
            if (step.Probability < 1.0 && Random.Shared.NextDouble() > step.Probability)
            {
                entry["skipped"] = "probability";
                skipReason = "probability";
                log.Add(entry);
                // Record ActionEvent for skipped click_ad steps.
                if (string.Equals(step.Type, "click_ad", StringComparison.OrdinalIgnoreCase))
                {
                    await RecordActionEventAsync(
                        step, ctx, runId, profileName, "skipped", skipReason, 0, null, ct);
                }
                continue;
            }

            // ── Per-step ad-domain filters (web-parity, Phase 17) ──
            //
            // Only meaningful when foreach_ad has set CurrentAdHref;
            // outside an ad loop the filters resolve to "no domain to
            // test", which we treat as "skip the only_on_* gates and
            // pass the skip_on_* gates" (matches web semantics).
            if (TryDomainFilterSkip(step, ctx, out skipReason))
            {
                entry["skipped"] = skipReason;
                log.Add(entry);
                // Record ActionEvent for skipped click_ad steps.
                if (string.Equals(step.Type, "click_ad", StringComparison.OrdinalIgnoreCase))
                {
                    await RecordActionEventAsync(
                        step, ctx, runId, profileName, "skipped", skipReason, 0, null, ct);
                }
                continue;
            }

            // Direct-handled control-flow words don't go through the
            // catch-all dispatcher; they short-circuit here so the
            // tuple-bubble path is obvious.
            if (string.Equals(step.Type, "break", StringComparison.OrdinalIgnoreCase))
            {
                entry["ok"] = true;
                entry["loop_ctrl"] = "break";
                log.Add(entry);
                return StepFlow.Break;
            }
            if (string.Equals(step.Type, "continue", StringComparison.OrdinalIgnoreCase))
            {
                entry["ok"] = true;
                entry["loop_ctrl"] = "continue";
                log.Add(entry);
                return StepFlow.Continue;
            }

            var t0 = DateTime.UtcNow;
            var stepOutcome = "ran";
            string? stepError = null;
            try
            {
                var flow = await DispatchAsync(step, session, ctx, counters, log, runId, profileName, ct);
                entry["ok"] = true;
                counters.Executed++;
                if (flow != StepFlow.Normal)
                {
                    // A nested if branch returned break/continue —
                    // bubble up to our parent loop.
                    entry["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                    log.Add(entry);
                    return flow;
                }
            }
            catch (OperationCanceledException)
            {
                entry["err"] = "cancelled";
                entry["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                log.Add(entry);
                throw;
            }
            catch (Exception ex)
            {
                counters.Failed++;
                entry["err"] = ex.Message;
                stepError = ex.Message;
                stepOutcome = "error";
                _log.LogWarning(ex, "Script step #{I} ({Type}) failed", i, step.Type);
                // Phase 23 hot-fix — bail immediately on a dead browser
                // session. Selenium throws "invalid session id" or
                // "session deleted" once the user closes the window or
                // the watchdog tears it down. Continuing to dispatch
                // more steps just spams identical exceptions in the log
                // and confuses the user about the actual failure point.
                if (IsDeadSession(ex))
                {
                    _log.LogWarning(
                        "Browser session is dead — aborting remaining {Left} step(s)",
                        steps.Count - i - 1);
                    entry["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                    log.Add(entry);
                    throw new ScriptAbortException("browser session closed mid-run");
                }
                if (step.AbortOnError)
                {
                    entry["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                    log.Add(entry);
                    throw new ScriptAbortException(
                        $"step #{i} ({step.Type}) threw — abort_on_error=true: {ex.Message}");
                }
            }
            entry["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            log.Add(entry);

            // Record ActionEvent for click_ad steps (Phase 34 analytics).
            if (string.Equals(step.Type, "click_ad", StringComparison.OrdinalIgnoreCase))
            {
                await RecordActionEventAsync(
                    step, ctx, runId, profileName, stepOutcome, skipReason: "",
                    (int)(DateTime.UtcNow - t0).TotalMilliseconds, stepError, ct);
            }
        }
        return StepFlow.Normal;
    }

    /// <summary>
    /// Phase 21 — graph traversal entry. Picks the entry node, then
    /// follows outgoing edges until exhaustion or the visit cap. Each
    /// node's <see cref="ScriptStep"/> goes through the same
    /// <see cref="DispatchAsync"/> pipeline as list mode, so all
    /// actions / per-step gates / interpolation behave identically.
    /// </summary>
    private async Task ExecuteGraphAsync(
        GraphTraverser.ParsedGraph graph, IBrowserSession s,
        RunContext ctx, RunCounters counters,
        List<Dictionary<string, object?>> log, long runId, string profileName, CancellationToken ct)
    {
        var entry = GraphTraverser.FindEntry(graph);
        if (entry is null)
        {
            _log.LogWarning("Graph has no entry node (every node has an inbound edge)");
            return;
        }

        var visits = 0;
        var current = entry;
        while (current is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (++visits > GraphTraverser.MaxNodeVisits)
            {
                _log.LogWarning(
                    "Graph traversal hit max node visits ({Cap}) — bailing to prevent runaway cycle",
                    GraphTraverser.MaxNodeVisits);
                break;
            }

            var step = current.Step;
            var entry2 = new Dictionary<string, object?>
            {
                ["idx"]     = visits - 1,
                ["node_id"] = current.Id,
                ["type"]    = step.Type,
                ["ok"]      = false,
            };

            // ── Per-node gates (same semantics as list mode) ──
            if (!step.Enabled)
            {
                entry2["skipped"] = "disabled";
                log.Add(entry2);
                current = StepNextNode(graph, current, branchHint: null);
                continue;
            }
            if (step.Probability < 1.0
                && Random.Shared.NextDouble() > step.Probability)
            {
                entry2["skipped"] = "probability";
                log.Add(entry2);
                current = StepNextNode(graph, current, branchHint: null);
                continue;
            }
            if (TryDomainFilterSkip(step, ctx, out var skipReason))
            {
                entry2["skipped"] = skipReason;
                log.Add(entry2);
                current = StepNextNode(graph, current, branchHint: null);
                continue;
            }

            // ── Direct-handled control words ──
            if (string.Equals(step.Type, "break",    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(step.Type, "continue", StringComparison.OrdinalIgnoreCase))
            {
                // In graph mode break/continue have no enclosing loop
                // semantics — they just terminate the traversal early.
                // (Loop nodes in graph mode are first-class — they
                //  iterate via self-edges.)
                entry2["ok"] = true;
                entry2["loop_ctrl"] = step.Type.ToLowerInvariant();
                log.Add(entry2);
                break;
            }

            string? branchHint = null;
            var t0 = DateTime.UtcNow;
            try
            {
                // Phase 21 audit fix #6 — graph-mode loop semantics.
                // foreach / foreach_ad / while_loop nodes don't have a
                // nested Body in graph mode; instead they have two
                // outgoing edges: "body" (into the loop body) and
                // "next" (after the loop). We iterate (items / ads /
                // condition) and per-iteration walk the body sub-graph
                // until it terminates or re-enters this loop node.
                var typeKey = step.Type.ToLowerInvariant();
                if (typeKey is "foreach" or "foreach_ad" or "while_loop")
                {
                    await ExecuteGraphLoopAsync(graph, current, step, s, ctx, counters, log, runId, profileName, ct);
                    entry2["ok"] = true;
                    counters.Executed++;
                    // After the loop, follow the "next" labelled edge
                    // (or any edge that isn't "body").
                    entry2["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                    log.Add(entry2);
                    current = StepNextNode(graph, current, branchHint: "next");
                    continue;
                }
                // For if-nodes, evaluate the condition here so we can
                // pick the matching outgoing edge label. The dispatch
                // path for "if" assumes nested then/else lists, which
                // graph mode replaces with edge labels.
                if (string.Equals(step.Type, "if", StringComparison.OrdinalIgnoreCase))
                {
                    var matched = await _conditions.EvaluateAsync(step.Condition, s, ctx, ct);
                    branchHint = matched ? "then" : "else";
                    entry2["ok"] = true;
                    entry2["branch"] = branchHint;
                    counters.Executed++;
                }
                else
                {
                    await DispatchAsync(step, s, ctx, counters, log, runId, profileName, ct);
                    entry2["ok"] = true;
                    counters.Executed++;
                }
            }
            catch (OperationCanceledException)
            {
                entry2["err"] = "cancelled";
                entry2["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                log.Add(entry2);
                throw;
            }
            catch (Exception ex)
            {
                counters.Failed++;
                entry2["err"] = ex.Message;
                // current is guaranteed non-null inside the while-body
                // (loop condition: `while (current is not null)`); the
                // null-forgiving suppresses CS8602 in the catch handler
                // where the compiler's flow analysis loses that fact.
                _log.LogWarning(ex, "Graph node '{Id}' ({Type}) failed", current!.Id, step.Type);
                if (IsDeadSession(ex))
                {
                    _log.LogWarning("Browser session is dead — aborting graph traversal");
                    entry2["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                    log.Add(entry2);
                    throw new ScriptAbortException("browser session closed mid-run");
                }
                if (step.AbortOnError)
                {
                    entry2["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                    log.Add(entry2);
                    throw new ScriptAbortException(
                        $"node '{current.Id}' ({step.Type}) threw — abort_on_error=true: {ex.Message}");
                }
            }
            entry2["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            log.Add(entry2);

            current = StepNextNode(graph, current, branchHint);
        }
    }

    /// <summary>Walk one outgoing edge from <paramref name="from"/>.
    /// Returns null when there's no outgoing edge (terminal node).</summary>
    private static GraphTraverser.GraphNode? StepNextNode(
        GraphTraverser.ParsedGraph g, GraphTraverser.GraphNode from, string? branchHint)
    {
        var outs = g.Outgoing[from.Id];
        var edge = GraphTraverser.PickNextEdge(outs, branchHint);
        if (edge is null) return null;
        return g.NodeIndex.TryGetValue(edge.To, out var next) ? next : null;
    }

    /// <summary>
    /// Phase 21 audit fix #6 — execute a loop node's body sub-graph
    /// once per iteration. The loop node's outgoing edges should be
    /// labelled: "body" goes into the loop body, "next" goes after
    /// the loop completes. If the user only drew one edge (no labels),
    /// we treat it as the body and stop traversal there.
    ///
    /// Per-iteration we walk from the body-target node, following
    /// edges, until either:
    ///   • the path terminates (no outgoing edge), or
    ///   • the path returns to <paramref name="loopNode"/> (back-edge).
    /// </summary>
    private async Task ExecuteGraphLoopAsync(
        GraphTraverser.ParsedGraph g,
        GraphTraverser.GraphNode loopNode,
        ScriptStep loopStep,
        IBrowserSession s, RunContext ctx, RunCounters counters,
        List<Dictionary<string, object?>> log, long runId, string profileName, CancellationToken ct)
    {
        // Pick body edge (label "body" preferred; fall back to first
        // outgoing edge that isn't "next").
        var outs = g.Outgoing[loopNode.Id];
        var bodyEdge = outs.FirstOrDefault(
            e => string.Equals(e.Label, "body", StringComparison.OrdinalIgnoreCase));
        if (bodyEdge is null)
            bodyEdge = outs.FirstOrDefault(
                e => !string.Equals(e.Label, "next", StringComparison.OrdinalIgnoreCase));
        if (bodyEdge is null) return; // empty loop body — nothing to iterate
        if (!g.NodeIndex.TryGetValue(bodyEdge.To, out var bodyStart)) return;

        var typeKey = loopStep.Type.ToLowerInvariant();
        if (typeKey == "while_loop")
        {
            // Bounded condition loop. max_iterations defaults to 1000.
            var maxIter = ParamInt(loopStep, "max_iterations", 1000);
            if (maxIter <= 0) maxIter = 1000;
            var iter = 0;
            while (iter++ < maxIter)
            {
                ct.ThrowIfCancellationRequested();
                var ok = await _conditions.EvaluateAsync(loopStep.Condition, s, ctx, ct);
                if (!ok) break;
                await ExecuteSubGraphAsync(g, bodyStart, loopNode.Id, s, ctx, counters, log, runId, profileName, ct);
            }
            if (iter >= maxIter)
                _log.LogWarning("graph while_loop hit max_iterations={Max} — bailing", maxIter);
        }
        else if (typeKey == "foreach")
        {
            var items = ResolveItemList(loopStep, ctx);
            var itemVar = ParamString(loopStep, "var") ?? "item";
            foreach (var it in items)
            {
                ct.ThrowIfCancellationRequested();
                ctx.Vars[itemVar] = it;
                await ExecuteSubGraphAsync(g, bodyStart, loopNode.Id, s, ctx, counters, log, runId, profileName, ct);
            }
        }
        else // foreach_ad
        {
            ctx.AdLoopDepth++;
            try
            {
                if (ctx.Ads.Count == 0)
                {
                    var parsed = await AdParser.ParseAsync(s, ct);
                    ctx.Ads.AddRange(parsed);
                }
                var snapshot = ctx.Ads.ToList();
                foreach (var ad in snapshot)
                {
                    ct.ThrowIfCancellationRequested();
                    ctx.Vars["ad_href"]  = ad.Href;
                    ctx.Vars["ad_title"] = ad.Title ?? "";
                    ctx.Vars["ad_id"]    = ad.StampId.ToString();
                    ctx.CurrentAdHref    = ad.Href;
                    await ExecuteSubGraphAsync(g, bodyStart, loopNode.Id, s, ctx, counters, log, runId, profileName, ct);
                    await Humanizer.IdleAsync(800, 2200, ct);
                }
                ctx.CurrentAdHref = "";
            }
            finally { ctx.AdLoopDepth--; }
        }
    }

    /// <summary>
    /// Walk the body sub-graph starting at <paramref name="start"/>,
    /// following edges until either the path terminates or returns to
    /// <paramref name="stopAtNodeId"/> (the enclosing loop node, used
    /// as a back-edge sentinel). Each node goes through the same
    /// per-step gate + dispatch pipeline as the top-level traversal,
    /// minus its own loop-iteration logic — sub-graphs may contain
    /// nested loops which recurse here naturally.
    /// </summary>
    private async Task ExecuteSubGraphAsync(
        GraphTraverser.ParsedGraph g,
        GraphTraverser.GraphNode start,
        string stopAtNodeId,
        IBrowserSession s, RunContext ctx, RunCounters counters,
        List<Dictionary<string, object?>> log, long runId, string profileName, CancellationToken ct)
    {
        var current = start;
        var visits = 0;
        while (current is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(current.Id, stopAtNodeId, StringComparison.Ordinal))
                return; // back-edge to loop node — iteration boundary
            if (++visits > GraphTraverser.MaxNodeVisits) return; // safety
            var step = current.Step;

            // Per-step gates (skipped → just step to next node).
            if (!step.Enabled
                || (step.Probability < 1.0 && Random.Shared.NextDouble() > step.Probability)
                || TryDomainFilterSkip(step, ctx, out _))
            {
                current = StepNextNode(g, current, branchHint: null);
                continue;
            }

            // Direct break/continue — terminate this body iteration.
            if (string.Equals(step.Type, "break",    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(step.Type, "continue", StringComparison.OrdinalIgnoreCase))
                return;

            string? hint = null;
            try
            {
                var typeKey = step.Type.ToLowerInvariant();
                if (typeKey == "if")
                {
                    var matched = await _conditions.EvaluateAsync(step.Condition, s, ctx, ct);
                    hint = matched ? "then" : "else";
                }
                else if (typeKey is "foreach" or "foreach_ad" or "while_loop")
                {
                    // Nested loop — recurse. After it finishes, follow
                    // its "next" edge.
                    await ExecuteGraphLoopAsync(g, current, step, s, ctx, counters, log, runId, profileName, ct);
                    current = StepNextNode(g, current, branchHint: "next");
                    continue;
                }
                else
                {
                    await DispatchAsync(step, s, ctx, counters, log, runId, profileName, ct);
                }
                counters.Executed++;
            }
            catch (Exception ex)
            {
                counters.Failed++;
                // current is guaranteed non-null inside the while-body
                // (loop condition: `while (current is not null)`); the
                // null-forgiving suppresses CS8602 in the catch handler
                // where the compiler's flow analysis loses that fact.
                if (step.AbortOnError)
                    throw new ScriptAbortException(
                        $"node '{current!.Id}' ({step.Type}) threw — abort_on_error=true: {ex.Message}");
                _log.LogWarning(ex, "Sub-graph node '{Id}' failed", current!.Id);
            }

            current = StepNextNode(g, current, hint);
        }
    }

    // ─── Dispatcher — full v1 catalog ─────────────────────────────

    private async Task<StepFlow> DispatchAsync(
        ScriptStep step, IBrowserSession s, RunContext ctx, RunCounters counters,
        List<Dictionary<string, object?>> log, long runId, string profileName, CancellationToken ct)
    {
        var type = step.Type.ToLowerInvariant();
        switch (type)
        {
            // ── Control flow ──────────────────────────────────────
            case "if":
            {
                var matched = await _conditions.EvaluateAsync(step.Condition, s, ctx, ct);
                var branch = matched ? step.Then : step.Else;
                if (branch.Count > 0)
                {
                    var flow = await ExecuteStepsAsync(branch, s, ctx, counters, log, runId, profileName, ct);
                    if (flow != StepFlow.Normal) return flow;
                }
                break;
            }
            case "foreach":
            {
                // Iterate items[]. Items can be a JSON array literal
                // OR a comma-separated string OR a var name.
                var items = ResolveItemList(step, ctx);
                var itemVar = ParamString(step, "var") ?? "item";
                foreach (var it in items)
                {
                    ct.ThrowIfCancellationRequested();
                    ctx.Vars[itemVar] = it;
                    if (step.Body.Count == 0) continue;
                    var flow = await ExecuteStepsAsync(step.Body, s, ctx, counters, log, runId, profileName, ct);
                    if (flow == StepFlow.Break) break;
                    // continue: just step to next item (no special action)
                }
                break;
            }
            case "foreach_ad":
            {
                // Re-entrance guard: parse_ads inside the body must
                // not corrupt the snapshot we're iterating. We bump
                // a depth counter so the inner parse_ads detects it
                // and short-circuits instead of mutating ctx.Ads
                // mid-iteration.
                ctx.AdLoopDepth++;
                try
                {
                    if (ctx.Ads.Count == 0)
                    {
                        var parsed = await AdParser.ParseAsync(s, ct);
                        ctx.Ads.AddRange(parsed);
                    }
                    // Snapshot — defends against external mutation
                    // even if a sibling task touches ctx.Ads.
                    var snapshot = ctx.Ads.ToList();
                    foreach (var ad in snapshot)
                    {
                        ct.ThrowIfCancellationRequested();
                        ctx.Vars["ad_href"]  = ad.Href;
                        ctx.Vars["ad_title"] = ad.Title ?? "";
                        ctx.Vars["ad_id"]    = ad.StampId.ToString();
                        // CurrentAdHref drives the per-step domain
                        // filter gate (Phase 17 web-parity). Setting
                        // it here so the filter sees a host while the
                        // body executes.
                        ctx.CurrentAdHref = ad.Href;
                        var flow = await ExecuteStepsAsync(step.Body, s, ctx, counters, log, runId, profileName, ct);
                        if (flow == StepFlow.Break) break;
                        // Inter-ad pause to look organic.
                        await Humanizer.IdleAsync(800, 2200, ct);
                    }
                    ctx.CurrentAdHref = "";
                }
                finally { ctx.AdLoopDepth--; }
                break;
            }
            // break / continue handled directly in ExecuteStepsAsync

            // ── Ads ────────────────────────────────────────────────
            case "parse_ads":
            case "catch_ads":
            {
                // Re-entrance guard (Phase 14 audit). If a foreach_ad
                // is iterating ctx.Ads and the user puts parse_ads in
                // its body, we'd corrupt the iteration. Foreach_ad
                // takes its own snapshot, so the iteration itself is
                // safe — but parse_ads still has unintuitive
                // semantics in that scope. Log + skip cleanly.
                if (ctx.AdLoopDepth > 0)
                {
                    _log.LogWarning(
                        "parse_ads inside foreach_ad body — skipped to avoid iterator corruption");
                    break;
                }
                ctx.Ads.Clear();
                ctx.Ads.AddRange(await AdParser.ParseAsync(s, ct));

                // Phase 34: record competitor observations, excluding blocked domains.
                try
                {
                    var batch = new List<CompetitorRecord>();
                    var capturedAt = DateTime.UtcNow;
                    foreach (var ad in ctx.Ads)
                    {
                        var host = ExtractHost(ad.Href);
                        if (string.IsNullOrEmpty(host)) continue;
                        if (DomainMatches(host, ctx.BlockDomains)) continue;
                        batch.Add(new CompetitorRecord
                        {
                            RunId = runId,
                            ProfileName = profileName,
                            CapturedAt = capturedAt,
                            Query = ctx.Vars.TryGetValue("current_query", out var q) ? q : "",
                            Domain = host,
                            AdTitle = ad.Title,
                            DisplayUrl = ad.DisplayUrl,
                            ClickUrl = ad.Href,
                        });
                    }
                    if (batch.Count > 0)
                        _ = _competitors.RecordBatchAsync(batch, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to record competitor batch for parse_ads");
                }
                break;
            }
            case "click_ad":
            {
                if (ctx.Ads.Count == 0)
                    throw new InvalidOperationException("no parsed ads — call parse_ads first");
                AdRecord? target;
                var stampParam = ParamInt(step, "stamp_id", -1);
                if (stampParam >= 0)
                    target = ctx.Ads.FirstOrDefault(a => a.StampId == stampParam);
                else
                    target = ctx.Ads[Random.Shared.Next(ctx.Ads.Count)];
                if (target is null)
                    throw new InvalidOperationException("no matching ad to click");
                if (await AdParser.IsSelfClickAsync(s, target.Href, ct))
                    throw new InvalidOperationException(
                        $"own-domain guard: ad href {target.Href} matches page host");
                var tier = await AdParser.ClickAsync(s, target, ct);
                _log.LogInformation("Ad #{Id} clicked via tier {Tier}", target.StampId, tier);
                ctx.AdsClicked++;
                counters.AdsClicked++;
                // Post-click dwell — let the landing render before
                // anything next would interact.
                await Humanizer.IdleAsync(2500, 6000, ct);
                break;
            }

            // ── Navigation ─────────────────────────────────────────
            case "navigate":
            case "open_url":
            case "visit":
            {
                var url = ParamString(step, "url")
                    ?? throw new ArgumentException("missing 'url'");
                await s.NavigateAsync(InterpolateVars(url, ctx), ct);
                break;
            }
            case "back":
                await s.ExecuteScriptAsync("history.back();", null, ct);
                break;
            case "forward":
                await s.ExecuteScriptAsync("history.forward();", null, ct);
                break;
            case "reload":
                await s.ExecuteScriptAsync("location.reload();", null, ct);
                break;
            case "new_tab":
            {
                var url = ParamString(step, "url") ?? "about:blank";
                await s.ExecuteScriptAsync(
                    $"window.open({JsonSerializer.Serialize(InterpolateVars(url, ctx))}, '_blank');",
                    null, ct);
                break;
            }
            case "close_tab":
                await s.ExecuteScriptAsync("window.close();", null, ct);
                break;

            // ── Waits / dwells ─────────────────────────────────────
            case "wait":
            case "dwell":
            {
                var min = ParamInt(step, "min_ms", 500);
                var max = ParamInt(step, "max_ms", min);
                await Humanizer.IdleAsync(min, max, ct);
                break;
            }
            case "random_delay":
                await Humanizer.IdleAsync(
                    ParamInt(step, "min_ms", 200),
                    ParamInt(step, "max_ms", 1500), ct);
                break;
            case "wait_for_selector":
            {
                var sel = ParamString(step, "selector")
                    ?? throw new ArgumentException("missing 'selector'");
                var timeoutMs = ParamInt(step, "timeout_ms", 15000);
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    var hit = await s.ExecuteScriptAsync(
                        $"return !!document.querySelector({JsonSerializer.Serialize(sel)});",
                        null, ct);
                    if (hit is true) return StepFlow.Normal;
                    await Task.Delay(250, ct);
                }
                throw new TimeoutException($"wait_for_selector timed out: {sel}");
            }
            case "wait_for_url":
            {
                var pattern = ParamString(step, "pattern")
                    ?? throw new ArgumentException("missing 'pattern'");
                var timeoutMs = ParamInt(step, "timeout_ms", 15000);
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                var rx = new System.Text.RegularExpressions.Regex(pattern);
                while (DateTime.UtcNow < deadline)
                {
                    var url = await s.ExecuteScriptAsync(
                        "return location.href;", null, ct) as string ?? "";
                    if (rx.IsMatch(url)) return StepFlow.Normal;
                    await Task.Delay(250, ct);
                }
                throw new TimeoutException($"wait_for_url timed out: {pattern}");
            }

            // ── Interaction ────────────────────────────────────────
            case "click_selector":
            case "click":
            {
                var sel = ParamString(step, "selector")
                    ?? throw new ArgumentException("missing 'selector'");
                await Humanizer.ClickAsync(s, sel, ct: ct);
                break;
            }
            case "double_click":
            {
                var sel = ParamString(step, "selector")
                    ?? throw new ArgumentException("missing 'selector'");
                await Humanizer.ClickAsync(s, sel, ct: ct);
                await Humanizer.IdleAsync(60, 130, ct);
                await Humanizer.ClickAsync(s, sel, ct: ct);
                break;
            }
            case "right_click":
            {
                var sel = ParamString(step, "selector")
                    ?? throw new ArgumentException("missing 'selector'");
                var js = $$"""
                    (function() {
                      var el = document.querySelector({{JsonSerializer.Serialize(sel)}});
                      if (!el) return false;
                      el.dispatchEvent(new MouseEvent('contextmenu', {bubbles: true, button: 2}));
                      return true;
                    })()
                """;
                await s.ExecuteScriptAsync(js, null, ct);
                break;
            }
            case "hover":
            {
                var sel = ParamString(step, "selector")
                    ?? throw new ArgumentException("missing 'selector'");
                var js = $$"""
                    (function() {
                      var el = document.querySelector({{JsonSerializer.Serialize(sel)}});
                      if (!el) return false;
                      el.dispatchEvent(new MouseEvent('mouseover', {bubbles: true}));
                      return true;
                    })()
                """;
                await s.ExecuteScriptAsync(js, null, ct);
                await Humanizer.IdleAsync(300, 1100, ct);
                break;
            }
            case "type":
            {
                var sel = ParamString(step, "selector")
                    ?? throw new ArgumentException("missing 'selector'");
                var text = InterpolateVars(ParamString(step, "text") ?? "", ctx);
                var minMs = ParamInt(step, "min_ms", 40);
                var maxMs = ParamInt(step, "max_ms", 180);
                await Humanizer.TypeAsync(s, sel, text, minMs, maxMs, ct);
                break;
            }
            case "press_key":
            {
                var key = ParamString(step, "key") ?? "Enter";
                var js = $$"""
                    (function() {
                      var k = {{JsonSerializer.Serialize(key)}};
                      var ev = new KeyboardEvent('keydown', {key: k, bubbles: true});
                      var t = document.activeElement || document.body;
                      t.dispatchEvent(ev);
                      return true;
                    })()
                """;
                await s.ExecuteScriptAsync(js, null, ct);
                break;
            }
            case "scroll":
            {
                var totalSec = ParamInt(step, "seconds", 6);
                await Humanizer.ScrollAsync(s, totalSec, ct);
                break;
            }
            case "scroll_to_bottom":
            {
                await s.ExecuteScriptAsync(
                    "window.scrollTo({top: document.body.scrollHeight, behavior: 'smooth'});",
                    null, ct);
                break;
            }
            case "fill_form":
            {
                // Bulk form fill — params is an object whose keys are
                // selectors and values are the strings to type.
                if (!step.Params.TryGetValue("fields", out var raw) || raw is null)
                    throw new ArgumentException("missing 'fields' object");
                if (raw is not JsonElement el || el.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("'fields' must be an object {selector: text, ...}");
                foreach (var kv in el.EnumerateObject())
                {
                    var sel = kv.Name;
                    var txt = InterpolateVars(kv.Value.GetString() ?? "", ctx);
                    await Humanizer.TypeAsync(s, sel, txt, 40, 160, ct);
                    await Humanizer.IdleAsync(150, 350, ct);
                }
                break;
            }
            case "move_random":
            {
                // No real cursor in v1 — simulate with idle pause.
                await Humanizer.IdleAsync(
                    ParamInt(step, "min_ms", 300),
                    ParamInt(step, "max_ms", 900), ct);
                break;
            }

            // ── Data / vars ────────────────────────────────────────
            case "save_var":
            {
                var name  = ParamString(step, "name")
                    ?? throw new ArgumentException("missing 'name'");
                // Phase 21 audit fix — reject reserved names so user
                // scripts can't poison ad context (ad_href / ad_title /
                // ad_id) or extension lifecycle (_ext_origin_tab) by
                // overwriting them mid-iteration.
                if (ScriptSecurityGuards.IsReservedVarName(name))
                    throw new ArgumentException(
                        $"'{name}' is a reserved variable — use a different name");
                var value = ParamString(step, "value") ?? "";
                ctx.Vars[name] = InterpolateVars(value, ctx);
                break;
            }
            case "extract_text":
            case "read":
            {
                var sel = ParamString(step, "selector") ?? "body";
                var saveAs = ParamString(step, "save_as");
                var js = $$"""
                    (function() {
                      var el = document.querySelector({{JsonSerializer.Serialize(sel)}});
                      return el ? (el.innerText || el.textContent || '') : null;
                    })()
                """;
                var text = await s.ExecuteScriptAsync(js, null, ct) as string ?? "";
                if (!string.IsNullOrEmpty(saveAs)) ctx.Vars[saveAs] = text;
                break;
            }
            case "execute_js":
            {
                var src = ParamString(step, "code")
                    ?? throw new ArgumentException("missing 'code'");
                await s.ExecuteScriptAsync(src, null, ct);
                break;
            }

            // ── Misc ───────────────────────────────────────────────
            case "screenshot":
            {
                // Phase 13B: real CDP capture, Phase 14 hardened
                // against path traversal. Every screenshot lands in
                // a sandbox dir under %LocalAppData%\GhostShell\
                // screenshots\ — user-supplied "../.." escapes get
                // rejected via a containment check after Path.GetFullPath
                // resolves the absolute target.
                var pathParam = ParamString(step, "path");
                if (string.IsNullOrEmpty(pathParam))
                {
                    var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                    pathParam = $"{ts}.png";
                }
                pathParam = InterpolateVars(pathParam, ctx);

                // Sandbox root — always under LocalAppData\GhostShell\screenshots.
                var dataDir = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                var sandboxRoot = Path.GetFullPath(
                    Path.Combine(dataDir, "GhostShell", "screenshots"));

                // If user supplied a rooted path, ignore the rooting —
                // we only respect the leaf filename. If they didn't,
                // join into sandbox. Either way, GetFullPath then
                // verify the result starts with sandboxRoot.
                var bareLeaf = Path.IsPathRooted(pathParam)
                    ? Path.GetFileName(pathParam)
                    : pathParam;
                var candidate = Path.GetFullPath(Path.Combine(sandboxRoot, bareLeaf));
                if (!candidate.StartsWith(
                        sandboxRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)
                    && !candidate.Equals(sandboxRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"screenshot path escapes sandbox: {pathParam}");
                }

                var saved = await s.CaptureScreenshotAsync(candidate, ct);
                _log.LogInformation("Screenshot written: {Path}", saved);
                if (ParamString(step, "save_as") is string varName
                    && !string.IsNullOrEmpty(varName))
                    ctx.Vars[varName] = saved;
                break;
            }
            case "log":
            {
                var msg = InterpolateVars(ParamString(step, "message") ?? "", ctx);
                _log.LogInformation("[script] {Msg}", msg);
                break;
            }

            case "solve_captcha":
            {
                if (_captcha is null)
                {
                    _log.LogWarning("solve_captcha invoked but no ICaptchaSolver registered");
                    break;
                }
                var kind = await _captcha.DetectAsync(s, ct);
                if (kind is null)
                {
                    _log.LogDebug("solve_captcha: no captcha detected");
                    break;
                }
                var timeoutSec = ParamInt(step, "timeout_sec", 180);
                var solved = await _captcha.SolveAsync(s, kind,
                    TimeSpan.FromSeconds(timeoutSec), ct);
                if (!solved)
                    throw new TimeoutException($"captcha '{kind}' not solved within {timeoutSec}s");
                break;
            }

            // ── Phase 17 — web-parity additions ────────────────────

            case "while_loop":
            {
                // Bounded condition loop. The web version doesn't have
                // a hard cap, but unbounded loops in user-authored
                // scripts are footguns; we cap at 1000 iterations and
                // log a warning if we hit it.
                var maxIterations = ParamInt(step, "max_iterations", 1000);
                if (maxIterations <= 0) maxIterations = 1000;
                var iter = 0;
                while (iter < maxIterations)
                {
                    ct.ThrowIfCancellationRequested();
                    var ok = await _conditions.EvaluateAsync(step.Condition, s, ctx, ct);
                    if (!ok) break;
                    if (step.Body.Count == 0) break;
                    var flow = await ExecuteStepsAsync(step.Body, s, ctx, counters, log, runId, profileName, ct);
                    if (flow == StepFlow.Break) break;
                    iter++;
                }
                if (iter >= maxIterations)
                    _log.LogWarning("while_loop hit max_iterations={Max} — bailing out", maxIterations);
                break;
            }

            case "switch_tab":
            {
                // Switch the focused tab by index. Selenium-style tab
                // switching needs driver-level access through
                // IBrowserSession; until that's wired up this is a
                // no-op. We log at WARN (not DEBUG) so users can see
                // their step ran but did nothing — silent debug-level
                // logging led to "why isn't my script working?".
                var idx = ParamInt(step, "index", 0);
                _log.LogWarning(
                    "switch_tab idx={Idx} requested but driver-level handler is not wired yet — step is a no-op",
                    idx);
                break;
            }

            case "pause":
            {
                // Web-parity alias for dwell with seconds (vs ms).
                // Audit Phase 18: defensively swap if user supplied
                // max < min — Random.Shared.Next throws on inverted
                // bounds, and silently swapping is safer than an
                // unhandled exception bubbling up the runner.
                var minSec = ParamInt(step, "min_sec", 3);
                var maxSec = ParamInt(step, "max_sec", 8);
                if (maxSec < minSec) (minSec, maxSec) = (maxSec, minSec);
                await Humanizer.IdleAsync(minSec * 1000, maxSec * 1000, ct);
                break;
            }

            case "refresh":
            {
                // Reload current page N times, with random delay
                // between attempts. Web version uses this to retry a
                // SERP that returned 0 ads.
                var maxAttempts = ParamInt(step, "max_attempts", 3);
                var delayMin    = ParamInt(step, "delay_min_sec", 3);
                var delayMax    = ParamInt(step, "delay_max_sec", 8);
                for (var k = 0; k < maxAttempts; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    await s.ExecuteScriptAsync("location.reload();", null, ct);
                    if (k < maxAttempts - 1)
                        await Humanizer.IdleAsync(delayMin * 1000, delayMax * 1000, ct);
                }
                break;
            }

            case "rotate_ip":
            {
                // No-op on static proxies; the runtime's proxy manager
                // is the right place to wire this up. For now log and
                // optionally pause so scripts that depend on this
                // step's existence don't break.
                var waitSec = ParamInt(step, "wait_after_sec", 4);
                _log.LogInformation("rotate_ip requested (driver-level handler not wired); waiting {S}s", waitSec);
                await Humanizer.IdleAsync(waitSec * 1000, waitSec * 1000, ct);
                break;
            }

            case "http_request":
            {
                // Webhook / external API call. Not routed through the
                // browser proxy. Hardened against SSRF: only http(s),
                // blocks loopback / RFC1918 / link-local. Response
                // capped at 1 MB.
                var url = ParamString(step, "url")
                    ?? throw new ArgumentException("missing 'url'");
                url = InterpolateVars(url, ctx);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
                    throw new ArgumentException("http_request: not an absolute URL");
                if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
                    throw new ArgumentException("http_request: only http(s) allowed");
                if (ScriptSecurityGuards.IsBlockedHost(u))
                    throw new InvalidOperationException(
                        $"http_request: host blocked by SSRF policy ({u.Host})");

                var method = (ParamString(step, "method") ?? "POST").ToUpperInvariant();
                var timeoutSec = ParamInt(step, "timeout_sec", 15);

                using var http = new System.Net.Http.HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(timeoutSec),
                };
                using var req = new System.Net.Http.HttpRequestMessage(
                    new System.Net.Http.HttpMethod(method), u);

                if (step.Params.TryGetValue("headers", out var headersRaw)
                    && headersRaw is JsonElement hEl
                    && hEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in hEl.EnumerateObject())
                    {
                        var v = kv.Value.ValueKind == JsonValueKind.String
                            ? kv.Value.GetString() ?? ""
                            : kv.Value.GetRawText();
                        try { req.Headers.TryAddWithoutValidation(kv.Name, InterpolateVars(v, ctx)); }
                        catch { /* ignore bad header — caller will see it didn't take */ }
                    }
                }

                if (method != "GET" && method != "DELETE")
                {
                    if (step.Params.TryGetValue("body", out var bodyRaw) && bodyRaw is not null)
                    {
                        string bodyStr;
                        if (bodyRaw is JsonElement bEl
                            && bEl.ValueKind == JsonValueKind.Object)
                            bodyStr = bEl.GetRawText();
                        else
                            bodyStr = InterpolateVars(bodyRaw.ToString() ?? "", ctx);
                        req.Content = new System.Net.Http.StringContent(
                            bodyStr, System.Text.Encoding.UTF8, "application/json");
                    }
                }

                using var rsp = await http.SendAsync(req, ct);
                var raw = await rsp.Content.ReadAsByteArrayAsync(ct);
                if (raw.Length > 1024 * 1024)
                    raw = raw.Take(1024 * 1024).ToArray();
                var respText = System.Text.Encoding.UTF8.GetString(raw);

                var saveAs = ParamString(step, "save_as");
                if (!string.IsNullOrEmpty(saveAs))
                    ctx.Vars[saveAs] = respText;
                _log.LogInformation("http_request {M} {U} → {S} ({N} bytes)",
                    method, u, (int)rsp.StatusCode, raw.Length);
                break;
            }

            // ── Phase 19 — extension automation ────────────────────
            //
            // The web version had 7 extension actions for driving
            // chromium extensions (popup, options page, eval inside
            // the extension's tab, etc.). Desktop equivalents drive
            // them via JavaScript injection — the extension tab is
            // tracked in <c>ctx.Vars["_ext_origin_tab"]</c> so close
            // can switch back. They're best-effort: full extension
            // automation requires CDP `Target.attachToTarget` for
            // the extension's background page, which we don't yet
            // expose through IBrowserSession. Today these actions
            // simulate the popup workflow by opening the extension
            // page in a new tab and treating it like a regular tab.

            case "open_extension_popup":
            case "open_extension_page":
            {
                var extId   = ParamString(step, "extension_id")    ?? "";
                var page    = ParamString(step, "page")             ?? "popup.html";
                var waitSel = ParamString(step, "wait_for_selector");
                var timeout = ParamInt(step, "timeout_sec", 15) * 1000;
                if (string.IsNullOrEmpty(extId))
                    throw new ArgumentException("missing 'extension_id' (32-char Chrome Web Store id)");
                // Phase 21 audit fix — extension ID + page path
                // sanitization. extId must be a Chrome 32-char a–p
                // identifier; page must be a single safe filename
                // (no ".." / "/" / "\"). See ScriptSecurityGuards for
                // the implementation; tests cover both rules there.
                if (!ScriptSecurityGuards.IsValidExtensionId(extId))
                    throw new ArgumentException(
                        "invalid 'extension_id' — expected 32-char Chrome Web Store id");
                var safePage = ScriptSecurityGuards.SanitiseExtensionPage(InterpolateVars(page, ctx));
                var url = $"chrome-extension://{extId}/{safePage}";
                // Stash the current tab id so extension_close can flip
                // back, then open the extension page in a new window.
                await s.ExecuteScriptAsync(
                    "window._gs_origin_tab = window.name || 'main';", null, ct);
                await s.ExecuteScriptAsync(
                    $"window.open({JsonSerializer.Serialize(url)}, '_blank');",
                    null, ct);
                ctx.Vars["_ext_origin_tab"] = "main";
                ctx.Vars["ext_tab"] = url;
                if (!string.IsNullOrEmpty(waitSel))
                {
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
                    while (DateTime.UtcNow < deadline)
                    {
                        var hit = await s.ExecuteScriptAsync(
                            $"return !!document.querySelector({JsonSerializer.Serialize(waitSel)});",
                            null, ct);
                        if (hit is true) break;
                        await Task.Delay(250, ct);
                    }
                }
                break;
            }

            case "extension_eval":
            {
                var code = ParamString(step, "code")
                    ?? throw new ArgumentException("missing 'code'");
                var saveAs = ParamString(step, "store_as");
                var result = await s.ExecuteScriptAsync(
                    InterpolateVars(code, ctx), null, ct);
                if (!string.IsNullOrEmpty(saveAs))
                    ctx.Vars[saveAs] = result?.ToString() ?? "";
                break;
            }

            case "extension_wait_for":
            {
                var sel = ParamString(step, "selector")
                    ?? throw new ArgumentException("missing 'selector'");
                var timeoutMs = ParamInt(step, "timeout_sec", 15) * 1000;
                var saveAs = ParamString(step, "save_as");
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    var js = $$"""
                        (function() {
                          var el = document.querySelector({{JsonSerializer.Serialize(sel)}});
                          if (!el) return null;
                          return el.textContent || '';
                        })()
                    """;
                    var hit = await s.ExecuteScriptAsync(js, null, ct);
                    if (hit is string text)
                    {
                        if (!string.IsNullOrEmpty(saveAs)) ctx.Vars[saveAs] = text;
                        return StepFlow.Normal;
                    }
                    await Task.Delay(250, ct);
                }
                throw new TimeoutException($"extension_wait_for timed out: {sel}");
            }

            case "extension_click":
            {
                var sel = ParamString(step, "selector")
                    ?? throw new ArgumentException("missing 'selector'");
                var timeoutMs = ParamInt(step, "timeout_sec", 10) * 1000;
                // Auto-wait for the selector before clicking (matches
                // legacy web semantics).
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                bool clicked = false;
                while (DateTime.UtcNow < deadline)
                {
                    var js = $$"""
                        (function() {
                          var el = document.querySelector({{JsonSerializer.Serialize(sel)}});
                          if (!el) return false;
                          el.click();
                          return true;
                        })()
                    """;
                    var r = await s.ExecuteScriptAsync(js, null, ct);
                    if (r is true) { clicked = true; break; }
                    await Task.Delay(200, ct);
                }
                if (!clicked)
                    throw new TimeoutException($"extension_click: selector not found within {timeoutMs}ms");
                break;
            }

            case "extension_fill":
            {
                var sel = ParamString(step, "selector")
                    ?? throw new ArgumentException("missing 'selector'");
                var val = InterpolateVars(ParamString(step, "value") ?? "", ctx);
                var clearFirst = ParamBool(step, "clear_first", true);
                var js = $$"""
                    (function() {
                      var el = document.querySelector({{JsonSerializer.Serialize(sel)}});
                      if (!el) return false;
                      if ({{(clearFirst ? "true" : "false")}}) el.value = '';
                      el.focus();
                      el.value = {{JsonSerializer.Serialize(val)}};
                      el.dispatchEvent(new Event('input',  { bubbles: true }));
                      el.dispatchEvent(new Event('change', { bubbles: true }));
                      return true;
                    })()
                """;
                var ok = await s.ExecuteScriptAsync(js, null, ct);
                if (ok is not true)
                    throw new InvalidOperationException($"extension_fill: selector '{sel}' not found");
                break;
            }

            case "extension_close":
            {
                // Close the current tab (assumed extension tab) and
                // hop back to the origin recorded by the open call.
                await s.ExecuteScriptAsync("window.close();", null, ct);
                ctx.Vars.Remove("ext_tab");
                ctx.Vars.Remove("_ext_origin_tab");
                break;
            }

            default:
                throw new NotSupportedException($"action '{step.Type}' is not implemented");
        }
        // Every non-control-flow case falls through here. Returning
        // Normal makes the caller loop continue to the next sibling.
        return StepFlow.Normal;
    }

    // ─── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Web-parity per-step domain gates. Returns true if the step
    /// should be skipped (and writes a short reason into
    /// <paramref name="reason"/>). When no foreach_ad is active the
    /// only_on_* gates are inert (no current ad to test) and the
    /// skip_on_* gates pass through.
    /// </summary>
    private static bool TryDomainFilterSkip(
        ScriptStep step, RunContext ctx, out string reason)
    {
        reason = "";
        // Fast path: no filters set → no work.
        if (!step.SkipOnMyDomain && !step.SkipOnTarget
            && !step.OnlyOnMyDomain && !step.OnlyOnTarget
            && !step.SkipOnBlocked && !step.OnlyOnBlocked) return false;

        var host = ExtractHost(ctx.CurrentAdHref);
        var hasAd = !string.IsNullOrEmpty(host);

        // only_on_* gates: when set, REQUIRE the ad to match. When no
        // ad is in scope, treat as "policy not satisfied" → skip.
        if (step.OnlyOnMyDomain && !(hasAd && DomainMatches(host, ctx.MyDomains)))
        { reason = "only_on_my_domain"; return true; }
        if (step.OnlyOnTarget && !(hasAd && DomainMatches(host, ctx.TargetDomains)))
        { reason = "only_on_target"; return true; }
        if (step.OnlyOnBlocked && !(hasAd && DomainMatches(host, ctx.BlockDomains)))
        { reason = "only_on_blocked"; return true; }

        // skip_on_* gates: only fire when there IS an ad and it
        // matches. With no ad, policy doesn't apply → don't skip.
        if (step.SkipOnMyDomain && hasAd && DomainMatches(host, ctx.MyDomains))
        { reason = "skip_on_my_domain"; return true; }
        if (step.SkipOnTarget && hasAd && DomainMatches(host, ctx.TargetDomains))
        { reason = "skip_on_target"; return true; }
        if (step.SkipOnBlocked && hasAd && DomainMatches(host, ctx.BlockDomains))
        { reason = "blocked"; return true; }

        return false;
    }

    /// <summary>
    /// Record one click_ad action outcome to the ad_density table for
    /// CTR / skip-reason analytics. Wraps in try-catch so analytics
    /// failures don't disrupt the script.
    /// </summary>
    private async Task RecordActionEventAsync(
        ScriptStep step, RunContext ctx, long runId, string profileName,
        string outcome, string skipReason, int durationMs, string? error,
        CancellationToken ct)
    {
        try
        {
            var host = ExtractHost(ctx.CurrentAdHref);
            string adClass;
            if (string.IsNullOrEmpty(host))
            {
                adClass = "unknown";
            }
            else if (DomainMatches(host, ctx.BlockDomains))
            {
                adClass = "blocked";
                // Skip recording if the ad is blocked — blocked sites
                // should not pollute analytics.
                return;
            }
            else if (DomainMatches(host, ctx.MyDomains))
            {
                adClass = "my_domain";
            }
            else if (DomainMatches(host, ctx.TargetDomains))
            {
                adClass = "target";
            }
            else
            {
                adClass = "competitor";
            }

            var ev = new ActionEvent
            {
                RunId = runId,
                ProfileName = profileName,
                CapturedAt = DateTime.UtcNow,
                Query = ctx.Vars.TryGetValue("current_query", out var q) ? q : null,
                AdDomain = host,
                AdClass = adClass,
                ActionType = "click_ad",
                Outcome = outcome,
                SkipReason = outcome == "skipped" ? skipReason : null,
                DurationSec = durationMs > 0 ? durationMs / 1000.0 : null,
                Error = error != null ? error[..Math.Min(error.Length, 200)] : null,
            };

            await _adDensity.RecordActionAsync(ev, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record action_event for click_ad");
        }
    }

    /// <summary>
    /// Strip whitespace, lowercase, drop a leading "www.", and reject
    /// obviously-not-a-domain inputs. Used when seeding
    /// <see cref="RunContext.MyDomains"/> / <see cref="RunContext.TargetDomains"/>
    /// from CSV strings — keeps the comparison-side normalisation
    /// consistent with <see cref="ExtractHost"/>.
    /// </summary>
    private static string NormaliseDomain(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().ToLowerInvariant();
        if (s.StartsWith("www.")) s = s[4..];
        if (s.StartsWith("http://"))  s = s[7..];
        if (s.StartsWith("https://")) s = s[8..];
        // Drop trailing path/port if pasted from a URL.
        var slash = s.IndexOf('/');
        if (slash > 0) s = s[..slash];
        var colon = s.IndexOf(':');
        if (colon > 0) s = s[..colon];
        // Sanity: must look like a host (must contain a dot).
        return s.Contains('.') ? s : "";
    }

    /// <summary>Pull the host out of an absolute URL; returns "" on
    /// failure. Strips a leading "www." for friendlier comparisons.</summary>
    private static string ExtractHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        var h = u.Host.ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }

    /// <summary>True if <paramref name="host"/> matches one of
    /// <paramref name="set"/> (exact host or any registered suffix
    /// match — ".example.com" entries cover sub-domains).</summary>
    private static bool DomainMatches(string host, HashSet<string> set)
    {
        if (set.Count == 0) return false;
        if (set.Contains(host)) return true;
        foreach (var d in set)
        {
            var trimmed = d.StartsWith("www.") ? d[4..] : d;
            if (host.EndsWith("." + trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Phase 21: SanitiseExtensionPage / IsBlockedHost / IsReservedVarName
    // moved to public <see cref="ScriptSecurityGuards"/> so the unit-
    // test suite can exercise them. ScriptRunner now calls those
    // helpers directly — see "open_extension_*" / "http_request" /
    // "save_var" cases above.

    private static IEnumerable<string> ResolveItemList(ScriptStep step, RunContext ctx)
    {
        // Two source modes:
        //   • "csv_file" — open the file, pull the named column.
        //   • "inline"   (default) — items is an explicit array, a
        //                CSV string, or a $varname reference.
        var source = (ParamString(step, "source") ?? "inline").ToLowerInvariant();
        if (source == "csv_file")
        {
            return ReadCsvColumn(step);
        }

        if (step.Params.TryGetValue("items", out var raw) && raw is not null)
        {
            switch (raw)
            {
                case string s when s.StartsWith("$"):
                    if (ctx.Vars.TryGetValue(s[1..], out var v))
                        return v.Split(',', StringSplitOptions.RemoveEmptyEntries
                                          | StringSplitOptions.TrimEntries);
                    return Array.Empty<string>();
                case string s:
                    return s.Split(',', StringSplitOptions.RemoveEmptyEntries
                                       | StringSplitOptions.TrimEntries);
                case JsonElement el when el.ValueKind == JsonValueKind.Array:
                    return el.EnumerateArray()
                             .Select(e => e.ValueKind == JsonValueKind.String
                                 ? (e.GetString() ?? "") : e.GetRawText())
                             .ToList();
                case System.Collections.IEnumerable arr:
                    return arr.Cast<object?>()
                              .Select(o => o?.ToString() ?? "")
                              .ToList();
            }
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Pull a column out of a CSV file for foreach iteration. Honours
    /// the schema's csv_path / csv_column / csv_has_header params.
    /// Column may be a header name or a 0-based index. Returns empty
    /// on any failure (logged elsewhere); never throws — a missing
    /// file shouldn't abort the run, just iterate zero items.
    /// </summary>
    /// <summary>Hard cap on rows we'll read from a CSV — prevents an
    /// OOM if the user accidentally points us at a multi-gigabyte
    /// file. 1M rows is way more than any realistic foreach wants.</summary>
    private const int MaxCsvRows = 1_000_000;

    private static IEnumerable<string> ReadCsvColumn(ScriptStep step)
    {
        var path = ParamString(step, "csv_path");
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        // Expand %USERPROFILE% / %APPDATA% / etc. so users can give us
        // portable paths instead of hard-coded C:\Users\... values.
        path = Environment.ExpandEnvironmentVariables(path);

        // Phase 21 audit fix: sandbox CSV reads to a fixed root under
        // %LocalAppData%\GhostShell\csv-data\. Without this a malicious
        // script could read arbitrary files (config\SAM, NTUSER.DAT,
        // etc.). UNC paths, symlinks pointing outside the root, and
        // ".." traversal all get rejected by the StartsWith() check
        // after Path.GetFullPath() resolves them.
        var dataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var sandboxRoot = Path.GetFullPath(
            Path.Combine(dataDir, "GhostShell", "csv-data"));
        try { Directory.CreateDirectory(sandboxRoot); } catch { /* best-effort */ }

        // If user supplies a rooted path, only its filename is honoured;
        // otherwise it's joined under the sandbox root.
        var leaf = Path.IsPathRooted(path) ? Path.GetFileName(path) : path;
        var candidate = Path.GetFullPath(Path.Combine(sandboxRoot, leaf));
        if (!candidate.StartsWith(
                sandboxRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && !candidate.Equals(sandboxRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();  // path escapes sandbox → drop silently
        }
        path = candidate;
        if (!File.Exists(path)) return Array.Empty<string>();

        var col       = ParamString(step, "csv_column") ?? "0";
        var hasHeader = ParamBool(step, "csv_has_header", true);

        // Tiny hand-rolled CSV reader: handles quoted fields with
        // embedded commas / escaped quotes. Good enough for the
        // simple list-of-values case foreach uses; users who need
        // RFC-4180-strict behaviour can pre-clean their data.
        var lines = new List<List<string>>();
        try
        {
            using var sr = new StreamReader(path);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                lines.Add(SplitCsvLine(line));
                if (lines.Count >= MaxCsvRows) break; // safety cap
            }
        }
        catch
        {
            return Array.Empty<string>();
        }
        if (lines.Count == 0) return Array.Empty<string>();

        // Resolve the column index. Try as 0-based int first, fall
        // back to header-name lookup on the first row if hasHeader.
        int idx;
        if (!int.TryParse(col, out idx))
        {
            if (!hasHeader) return Array.Empty<string>();
            idx = lines[0].FindIndex(c => string.Equals(c.Trim(), col, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return Array.Empty<string>();
        }

        var dataRows = hasHeader ? lines.Skip(1) : lines;
        var result = new List<string>();
        foreach (var row in dataRows)
        {
            if (idx < 0 || idx >= row.Count) continue;
            var v = row[idx]?.Trim() ?? "";
            if (!string.IsNullOrEmpty(v)) result.Add(v);
        }
        return result;
    }

    private static List<string> SplitCsvLine(string line)
    {
        // RFC-4180-ish: handles "field with, comma" and "" escaped
        // quotes. Bare-bones — doesn't aim for full CSV correctness.
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == ',')           { fields.Add(sb.ToString()); sb.Clear(); }
                else if (c == '"' && sb.Length == 0) inQuotes = true;
                else                    sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>
    /// Phase 23 hot-fix — Selenium throws WebDriverException with one
    /// of these messages once the Chrome window is gone (user closed
    /// it, or the watchdog tore it down). Continuing to dispatch
    /// further steps spams identical exceptions and obscures the real
    /// first-failure cause; we abort the run instead.
    /// </summary>
    private static bool IsDeadSession(Exception ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("invalid session id", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("session deleted", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("not connected to DevTools", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("chrome not reachable", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("disconnected: not connected", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("no such window", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ParamBool(ScriptStep step, string key, bool def)
    {
        if (!step.Params.TryGetValue(key, out var v) || v is null) return def;
        return v switch
        {
            bool b          => b,
            string s        => s.Equals("true", StringComparison.OrdinalIgnoreCase),
            JsonElement el  => el.ValueKind switch
            {
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                JsonValueKind.String => string.Equals(el.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                _                    => def,
            },
            _ => def,
        };
    }

    /// <summary>
    /// Substitute <c>{{var_name}}</c> in <paramref name="src"/> with
    /// the value from <paramref name="ctx"/>.Vars. Unknown names
    /// leave the placeholder verbatim — easier to debug than a silent
    /// blank substitution.
    ///
    /// Hardened against DoS: bails out if the source string is
    /// pathologically long (>64KB) and truncates each variable's
    /// substituted value to 4KB. A buggy or malicious script can no
    /// longer consume gigabytes by stuffing a giant string into
    /// <c>save_var</c> and reading it via <c>{{var}}</c> in a loop.
    /// </summary>
    private const int MaxInterpolationInput  = 64 * 1024;
    private const int MaxInterpolatedValue   = 4  * 1024;
    private static readonly System.Text.RegularExpressions.Regex VarPattern = new(
        @"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}",
        System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));
    /// <summary>
    /// Phase 24 — vault placeholder regex. Matches
    /// <c>{{vault.&lt;numeric-id&gt;.&lt;field&gt;}}</c> with optional
    /// whitespace inside the braces. Field names follow the same
    /// identifier rules as save_var.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex VaultPattern = new(
        @"\{\{\s*vault\.([0-9]+)\.([A-Za-z_][A-Za-z0-9_]*)\s*\}\}",
        System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static string InterpolateVars(string src, RunContext ctx)
    {
        if (string.IsNullOrEmpty(src)) return src;
        if (src.Length > MaxInterpolationInput) return src;
        try
        {
            // Phase 24: resolve {{vault.id.field}} first so subsequent
            // variable interpolation can chain (e.g. someone might
            // save a vault value into a regular var). Failed lookups
            // leave the placeholder verbatim — easier to debug than
            // a silent empty string.
            if (ctx.Vault.Count > 0)
            {
                src = VaultPattern.Replace(src, m =>
                {
                    var id    = m.Groups[1].Value;
                    var field = m.Groups[2].Value;
                    if (!ctx.Vault.TryGetValue(id, out var bag)) return m.Value;
                    if (!bag.TryGetValue(field, out var v))      return m.Value;
                    if (v.Length > MaxInterpolatedValue)
                        v = v[..MaxInterpolatedValue];
                    return v;
                });
            }
            if (ctx.Vars.Count == 0) return src;
            return VarPattern.Replace(src, m =>
            {
                if (!ctx.Vars.TryGetValue(m.Groups[1].Value, out var v)) return m.Value;
                if (v.Length > MaxInterpolatedValue)
                    v = v[..MaxInterpolatedValue];
                return v;
            });
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return src;
        }
    }

    /// <summary>
    /// Public surface for the profile-runner: scan a script's JSON
    /// payload (StepsJson and/or NodesJson) and pluck out every
    /// (vault-id, field) reference. The caller passes that to
    /// <c>IVaultService.ResolveAsync</c> at run start to materialise
    /// the live vault values once, off the dispatch hot path.
    /// </summary>
    public static IReadOnlyList<(long Id, string Field)> CollectVaultRefs(params string?[] jsonPayloads)
    {
        var seen = new HashSet<(long, string)>();
        foreach (var json in jsonPayloads)
        {
            if (string.IsNullOrEmpty(json)) continue;
            foreach (System.Text.RegularExpressions.Match m in VaultPattern.Matches(json))
            {
                if (long.TryParse(m.Groups[1].Value, out var id))
                    seen.Add((id, m.Groups[2].Value));
            }
        }
        return seen.Select(x => (x.Item1, x.Item2)).ToList();
    }

    private static string? ParamString(ScriptStep step, string key)
    {
        if (!step.Params.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            string s        => s,
            JsonElement el  => el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText(),
            _               => v.ToString(),
        };
    }

    private static int ParamInt(ScriptStep step, string key, int def)
    {
        if (!step.Params.TryGetValue(key, out var v) || v is null) return def;
        var raw = v switch
        {
            int i           => i,
            long l          => (int)l,
            double d        => (int)d,
            string s when int.TryParse(s, out var x) => x,
            JsonElement el when el.ValueKind == JsonValueKind.Number => el.GetInt32(),
            _ => def,
        };
        if (raw < -1) raw = def;
        if (raw > 600_000) raw = 600_000;
        return raw;
    }

    private static ScriptRun Finalise(
        long runId, DateTime startedAt, string status,
        RunCounters c, string? lastError,
        List<Dictionary<string, object?>> log, RunContext ctx)
    {
        var finishedAt = DateTime.UtcNow;
        var dur = (finishedAt - startedAt).TotalSeconds;
        var logJson = JsonSerializer.Serialize(log, JsonOpts);
        // Phase 24 audit fix — drop vault references so the cleartext
        // bag becomes eligible for GC the moment ExecuteAsync returns.
        // C# string immutability prevents true memory-zeroing, but
        // releasing the reference is the strongest mitigation that
        // doesn't require a SecureString rewrite. The bags themselves
        // hold only references; the strings live in the heap intern
        // pool until GC reclaims them.
        ctx.Vault.Clear();
        return new ScriptRun
        {
            Id            = runId,
            ScriptId      = 0,
            ProfileName   = "",
            StartedAt     = startedAt,
            FinishedAt    = finishedAt,
            Status        = status,
            StepsExecuted = c.Executed,
            StepsFailed   = c.Failed,
            AdsClicked    = c.AdsClicked,
            DurationSec   = dur,
            LastError     = lastError,
            LogJson       = logJson,
        };
    }

    private sealed class RunCounters
    {
        public int Executed { get; set; }
        public int Failed { get; set; }
        public int AdsClicked { get; set; }
    }

    /// <summary>
    /// Hard-abort signal — only used when a step has
    /// <c>abort_on_error=true</c> and threw. Walks all the way out
    /// of nested loops/conditions to the top-level executor.
    /// </summary>
    private sealed class ScriptAbortException : Exception
    {
        public ScriptAbortException(string m) : base(m) { }
    }
}
