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
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? vault = null,
        IReadOnlyDictionary<string, string>? vaultAliases = null)
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
        // Phase 69 — pre-resolved profile-scoped aliases.
        if (vaultAliases is not null)
            foreach (var kv in vaultAliases) ctx.VaultAliases[kv.Key] = kv.Value;
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
        var result = Finalise(runId, startedAt, status, counters, lastError, stepLog, ctx);

        // Phase 61c — surface aborts to the caller. Previously a
        // ScriptAbortException (browser session died, NoSuchWindow,
        // proxy timeout) was caught above and ExecuteAsync returned
        // normally, leading RealProfileRunner to log "Script completed"
        // for runs that actually crashed. Re-throw so the caller's
        // catch (Exception) branch fires and the run row + log line
        // both reflect the crash. We don't re-throw on
        // OperationCanceledException — that's a clean user-initiated
        // stop, RealProfileRunner already handles it specially.
        if (aborted && lastError is not "cancelled")
        {
            throw new ScriptAbortException(
                $"script aborted: {lastError ?? "unknown"} (status={status}, executed={counters.Executed}, failed={counters.Failed})");
        }
        return result;
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

            // Phase 70 — universal "probability" param. Any step can carry
            // a `probability` value (0..100). The default is 100 (always
            // run) so existing scripts behave exactly the same. Set to
            // e.g. 70 → run on roughly 7 of 10 invocations; 0 → never run.
            // Lets the user dial in stochastic flow for ANY action -- a
            // single click_ad that fires 60% of the time, a foreach_ad
            // body that varies per iteration, a save_var that snapshots
            // every other run -- without writing if-conditions.
            if (TryProbabilitySkip(step, out var probSkip))
            {
                entry["skipped"] = probSkip;
                log.Add(entry);
                if (string.Equals(step.Type, "click_ad", StringComparison.OrdinalIgnoreCase))
                {
                    await RecordActionEventAsync(
                        step, ctx, runId, profileName, "skipped", probSkip, 0, null, ct);
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
                // Phase 70 — explicit ScriptAbortException MUST propagate.
                // Previously the catch handler treated it as a generic
                // step failure: when an INNER ExecuteStepsAsync (e.g. a
                // foreach body) threw ScriptAbortException due to a dead
                // session, this OUTER handler caught it, marked the
                // foreach step as failed, and continued — letting
                // ExecuteAsync return "partial" status without re-throw.
                // RealProfileRunner then logged "finished cleanly" for
                // a run that actually crashed. Strict re-throw keeps the
                // abort travelling up to the run-coordinator.
                if (ex is ScriptAbortException)
                {
                    entry["err"] = ex.Message;
                    entry["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                    log.Add(entry);
                    throw;
                }
                counters.Failed++;
                entry["err"] = ex.Message;
                stepError = ex.Message;
                stepOutcome = "error";
                // Phase 70 — quiet logging for the "dead session" path.
                // The full stack trace was confusing users into thinking
                // it's a real bug; the only useful fact is which step
                // ran when the window died. Genuine step failures still
                // dump the trace so they're debuggable.
                if (IsDeadSession(ex))
                {
                    _log.LogInformation(
                        "Script step #{I} ({Type}) couldn't complete — browser session closed ({Msg})",
                        i, step.Type, ex.Message?.Split('\n').FirstOrDefault());
                    _log.LogWarning(
                        "Browser session is dead — aborting remaining {Left} step(s)",
                        steps.Count - i - 1);
                    entry["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                    log.Add(entry);
                    throw new ScriptAbortException("browser session closed mid-run");
                }
                _log.LogWarning(ex, "Script step #{I} ({Type}) failed", i, step.Type);
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
            // Phase 70 — universal probability gate (graph mode mirror
            // of the list-mode TryProbabilitySkip path).
            if (TryProbabilitySkip(step, out var probSkipG))
            {
                entry2["skipped"] = probSkipG;
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
                // Phase 70 — same fix as the list-mode handler: strict
                // propagate any ScriptAbortException so a session-died
                // signal from a deeper level reaches the run coordinator
                // instead of being absorbed as a step failure.
                if (ex is ScriptAbortException)
                {
                    entry2["err"] = ex.Message;
                    entry2["dur_ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                    log.Add(entry2);
                    throw;
                }
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
                    // Same unwrap + display-URL plumbing as the linear
                    // path above — graph-mode foreach_ad MUST share the
                    // same gate-input shape, otherwise scripts behave
                    // differently in the two layouts.
                    ctx.CurrentAdHref       = AdParser.UnwrapAdRedirect(ad.Href);
                    ctx.CurrentAdDisplayUrl = ad.DisplayUrl ?? "";
                    await ExecuteSubGraphAsync(g, bodyStart, loopNode.Id, s, ctx, counters, log, runId, profileName, ct);
                    await Humanizer.IdleAsync(800, 2200, ct);
                }
                ctx.CurrentAdHref = "";
                ctx.CurrentAdDisplayUrl = "";
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

        // ── Phase 60 — Universal step ENTRY log ───────────────────────
        // One LogInformation line per step gives the user a top-level
        // trail of EVERY action the script took: navigates, clicks,
        // searches, dwells, conditionals, loops. Each line names the
        // step type plus the most useful 1-2 params for that type.
        // This is the line the user grep's when they see suspicious
        // activity ("why did we open my domain?") — it tells them
        // exactly which step initiated each browser action.
        try
        {
            var paramSnap = StepParamSnapshot(step, ctx);
            _log.LogInformation(
                "▶ STEP {Type}{Params}  (run #{Run}, profile '{Profile}')",
                type, paramSnap, runId, profileName);
        }
        catch { /* logging shouldn't ever throw */ }

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
                // ─── Architectural redesign (Phase 41) ────────────────
                //
                // The OLD implementation took ONE snapshot of ctx.Ads at
                // loop entry, then iterated. After the first click_ad
                // the browser was on the AD'S landing page, not the
                // SERP — but the next iteration kept trying to click
                // ads from the original snapshot whose DOM stamps no
                // longer existed. Symptom from the user's logs:
                // "Ad click failed (all 4 tiers): https://kim-medical..."
                // because tier-1 querySelector('[data-gs-ad-id="N"]')
                // returned null on the kim-medical landing page.
                //
                // NEW model — per-iteration RESYNC:
                //   1. Capture the SERP URL at loop entry ("home base").
                //   2. Each iteration: check current URL; if not on
                //      SERP, navigate back (the ad click took us to a
                //      landing page that we now want to leave).
                //   3. Re-parse_ads on the (possibly re-rendered) SERP
                //      so DOM stamps (data-gs-ad-id) are FRESH.
                //   4. Pick the next ad whose host we haven't clicked
                //      yet — host-keyed instead of stamp-keyed because
                //      stamps reset on every parse, but advertiser
                //      hosts are stable across rerenders.
                //   5. Replace ctx.Ads with [target] so click_ad's
                //      Random.Next picks THIS specific ad (the body's
                //      click_ad takes ctx.Ads[Random.Next] when no
                //      stamp_id is set).
                //   6. Execute body, restore ctx.Ads, continue.
                //
                // Cap iterations at 50 — bounded to prevent runaway
                // loops if the SERP keeps showing new ads or the
                // resync fails.
                ctx.AdLoopDepth++;
                try
                {
                    var serpUrl = await GetCurrentUrlAsync(s, ct);
                    if (string.IsNullOrEmpty(serpUrl))
                    {
                        _log.LogWarning("foreach_ad: couldn't read SERP URL at entry — using best-effort snapshot mode");
                    }
                    var clickedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    const int maxIterations = 50;
                    for (var iter = 0; iter < maxIterations; iter++)
                    {
                        ct.ThrowIfCancellationRequested();

                        // ── RESYNC: ensure we're on the SERP ──────────
                        // After the first click_ad navigates to an ad's
                        // landing page, the browser is no longer on the
                        // SERP. Navigating back here ensures the next
                        // parse_ads sees the right DOM. We compare host
                        // + path (ignoring query/hash) because Google
                        // sometimes appends &sxsrf=... params per request.
                        if (!string.IsNullOrEmpty(serpUrl))
                        {
                            var curUrl = await GetCurrentUrlAsync(s, ct);
                            if (!IsSameSerpPage(curUrl, serpUrl))
                            {
                                _log.LogInformation(
                                    "foreach_ad iter {N}: not on SERP (cur='{Cur}'), navigating back to '{Serp}'",
                                    iter, curUrl, serpUrl);
                                try
                                {
                                    await s.NavigateAsync(serpUrl, ct);
                                    // Settle — Google's SERP renders
                                    // ads after main content, give it
                                    // 1.5-3s before we re-parse.
                                    await Humanizer.IdleAsync(1500, 3000, ct);
                                }
                                catch (Exception navEx)
                                {
                                    _log.LogWarning(navEx,
                                        "foreach_ad iter {N}: navigation back to SERP failed — aborting loop",
                                        iter);
                                    break;
                                }
                            }
                        }

                        // ── RE-PARSE: fresh DOM stamps every iteration
                        // The previous iteration's stamps (data-gs-ad-id)
                        // are dead after ANY DOM mutation — Google's
                        // SERP re-renders aggressively. Always start
                        // with a fresh parse.
                        List<AdRecord> freshAds;
                        try
                        {
                            freshAds = await AdParser.ParseAsync(s, ct);
                        }
                        catch (Exception parseEx)
                        {
                            _log.LogWarning(parseEx,
                                "foreach_ad iter {N}: parse_ads failed — aborting loop", iter);
                            break;
                        }
                        // Phase 45 — record EVERY observed ad to the
                        // competitor table. Without this, the
                        // Competitors page stayed empty even when the
                        // script clicked through dozens of advertiser
                        // domains (Phase 41 redesign bypasses the
                        // `parse_ads` action which was the only place
                        // recording happened previously).
                        if (freshAds.Count > 0)
                        {
                            await RecordCompetitorsAsync(freshAds, ctx, runId, profileName, ct);
                        }
                        if (freshAds.Count == 0)
                        {
                            _log.LogInformation(
                                "foreach_ad iter {N}: no ads on SERP, ending loop", iter);
                            break;
                        }

                        // ── PICK: next unclicked advertiser host ──────
                        // Track by host (not exact href) because Google
                        // shows the same advertiser with slightly
                        // different tracker URLs per impression. Without
                        // host-keyed dedup the loop would click the
                        // same advertiser N times in a row.
                        AdRecord? target = null;
                        foreach (var ad in freshAds)
                        {
                            // Prefer host of UNWRAPPED click URL — that's
                            // the actual destination, not Google's aclk.
                            var unwrapped = AdParser.UnwrapAdRedirect(ad.Href);
                            var host = ExtractHost(unwrapped);
                            if (string.IsNullOrEmpty(host)) continue;
                            if (clickedHosts.Contains(host)) continue;
                            target = ad;
                            break;
                        }
                        if (target is null)
                        {
                            _log.LogInformation(
                                "foreach_ad iter {N}: all {Total} ads' hosts already visited, ending loop",
                                iter, freshAds.Count);
                            break;
                        }

                        // ── BIND CONTEXT for the body ─────────────────
                        ctx.Vars["ad_href"]  = target.Href;
                        ctx.Vars["ad_title"] = target.Title ?? "";
                        ctx.Vars["ad_id"]    = target.StampId.ToString();
                        ctx.CurrentAdHref       = AdParser.UnwrapAdRedirect(target.Href);
                        ctx.CurrentAdDisplayUrl = target.DisplayUrl ?? "";

                        // Mark host as clicked NOW (before body runs)
                        // so even if the body throws / skips / bypasses
                        // we don't infinite-loop on the same advertiser.
                        var targetHost = ExtractHost(ctx.CurrentAdHref);
                        if (!string.IsNullOrEmpty(targetHost))
                            clickedHosts.Add(targetHost);

                        // Diagnostic: dump what the gate will see.
                        _log.LogInformation(
                            "foreach_ad iter {N}: target host='{Host}', display='{Disp}', stamp={Stamp}, my=[{My}], target=[{Tg}]",
                            iter, targetHost,
                            ExtractHost(ctx.CurrentAdDisplayUrl),
                            target.StampId,
                            string.Join(",", ctx.MyDomains),
                            string.Join(",", ctx.TargetDomains));

                        // Replace ctx.Ads with [target] so the body's
                        // click_ad picks THIS ad (it does ctx.Ads[
                        // Random.Next(Count)] when no stamp_id is set).
                        // We also set ctx.Ads to the fresh full list
                        // first if the user wants the body to reason
                        // about counts/density, but the most common
                        // pattern is "click one ad" — single-element
                        // list is the safer default.
                        var savedAds = ctx.Ads.ToList();
                        ctx.Ads.Clear();
                        ctx.Ads.Add(target);
                        StepFlow flow;
                        try
                        {
                            flow = await ExecuteStepsAsync(step.Body, s, ctx, counters, log, runId, profileName, ct);
                        }
                        finally
                        {
                            // Restore the full ad list for any siblings
                            // outside foreach_ad that read ctx.Ads.
                            ctx.Ads.Clear();
                            ctx.Ads.AddRange(freshAds);
                        }
                        if (flow == StepFlow.Break) break;

                        // Inter-ad pause to look organic.
                        await Humanizer.IdleAsync(800, 2200, ct);
                    }
                    ctx.CurrentAdHref = "";
                    ctx.CurrentAdDisplayUrl = "";
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
                await RecordCompetitorsAsync(ctx.Ads, ctx, runId, profileName, ct);
                break;
            }
            // ── Web-parity compound actions (Phase 38) ─────────────
            // Native handlers for the legacy ghost_shell_browser
            // `search_query` and `commercial_inflate` step types.
            // Without these, imported web scripts that use them
            // silently throw NotSupportedException at the default
            // case — the run finishes with no visible work because
            // the catch path logs at Warning and steps continue.
            case "search_query":
            {
                var query = InterpolateVars(ParamString(step, "query") ?? "", ctx);
                if (string.IsNullOrWhiteSpace(query))
                {
                    _log.LogWarning("search_query: empty query, skipping");
                    break;
                }
                var locale = ParamString(step, "locale") ?? "uk";
                var maxAttempts = Math.Max(1, ParamInt(step, "max_attempts", 4));
                var retryMinSec = Math.Max(1, ParamInt(step, "retry_min_sec", 13));
                var retryMaxSec = Math.Max(retryMinSec, ParamInt(step, "retry_max_sec", 15));
                var timeoutMs = Math.Max(1000, ParamInt(step, "timeout_ms", 12000));
                var failOnEmpty = ParamBool(step, "fail_on_empty", false);
                var url = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}&hl={Uri.EscapeDataString(locale)}";

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    _log.LogInformation(
                        "search_query: '{Q}' attempt {A}/{Max} → {Url}",
                        query, attempt, maxAttempts, url);
                    try { await s.NavigateAsync(url, ct); }
                    catch (Exception ex) { _log.LogDebug(ex, "search_query navigate threw — continuing"); }

                    // Best-effort wait for the SERP to render. Don't
                    // throw if #search isn't there in time — Google
                    // sometimes redirects to a consent page; fall
                    // through to parse_ads which will return zero.
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    while (DateTime.UtcNow < deadline)
                    {
                        var hit = await s.ExecuteScriptAsync(
                            "return !!document.querySelector('#search');",
                            null, ct);
                        if (hit is true) break;
                        await Task.Delay(250, ct);
                    }

                    // Parse ads now so foreach_ad inside the same
                    // script body gets a populated list.
                    ctx.Ads.Clear();
                    ctx.Ads.AddRange(await AdParser.ParseAsync(s, ct));
                    if (ctx.Ads.Count > 0)
                    {
                        _log.LogInformation("search_query: '{Q}' returned {N} ad(s)", query, ctx.Ads.Count);
                        // Phase 70 fix — record competitor observations.
                        // The previous comment claimed "parse_ads handler
                        // does the competitor-record write" but that was
                        // false: search_query parses ads INLINE (via
                        // AdParser.ParseAsync) and never re-enters the
                        // parse_ads case-block, so the competitor table
                        // stayed empty for every search_query run. Stash
                        // the query in ctx.Vars so the recording path
                        // can populate the "query" column on each row.
                        ctx.Vars["current_query"] = query;
                        await RecordCompetitorsAsync(ctx.Ads, ctx, runId, profileName, ct);
                        break;
                    }
                    if (attempt < maxAttempts)
                    {
                        var pauseMs = Random.Shared.Next(retryMinSec * 1000, retryMaxSec * 1000 + 1);
                        _log.LogInformation(
                            "search_query: '{Q}' returned 0 ads, refreshing in {Ms} ms",
                            query, pauseMs);
                        await Task.Delay(pauseMs, ct);
                    }
                }

                if (ctx.Ads.Count == 0 && failOnEmpty)
                    throw new InvalidOperationException(
                        $"search_query '{query}' returned no ads after {maxAttempts} attempts");
                break;
            }
            case "commercial_inflate":
            {
                var brand = InterpolateVars(ParamString(step, "brand") ?? "", ctx);
                if (string.IsNullOrWhiteSpace(brand))
                {
                    _log.LogWarning("commercial_inflate: empty brand seed, skipping");
                    break;
                }
                var n = Math.Clamp(ParamInt(step, "n", 2), 1, 10);
                var locale = ParamString(step, "locale") ?? "uk";
                var dwellMin = Math.Max(1, ParamInt(step, "dwell_min", 4));
                var dwellMax = Math.Max(dwellMin, ParamInt(step, "dwell_max", 10));
                var clickOrganic = ParamBool(step, "click_organic", false);

                // Generic commercial-intent query templates. The aim
                // is to seed Google's per-session signal with a few
                // searches that LOOK like commercial intent for the
                // brand, so the subsequent search_query lands in a
                // richer ad context.
                var templates = new[]
                {
                    brand + " отзывы",
                    brand + " цена",
                    "купить " + brand,
                    "instagram " + brand,
                    brand + " магазин",
                    brand + " доставка",
                };
                for (int i = 0; i < n; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var q = templates[Random.Shared.Next(templates.Length)];
                    var url = $"https://www.google.com/search?q={Uri.EscapeDataString(q)}&hl={Uri.EscapeDataString(locale)}";
                    _log.LogInformation(
                        "commercial_inflate: pre-warm {I}/{N} '{Q}'", i + 1, n, q);
                    try
                    {
                        await s.NavigateAsync(url, ct);
                        var deadline = DateTime.UtcNow.AddSeconds(8);
                        while (DateTime.UtcNow < deadline)
                        {
                            var hit = await s.ExecuteScriptAsync(
                                "return !!document.querySelector('#search');", null, ct);
                            if (hit is true) break;
                            await Task.Delay(250, ct);
                        }

                        // Optional click-first-organic for stronger
                        // signal. Best-effort; if no organic anchor
                        // is visible (consent page, captcha) we just
                        // dwell on the SERP.
                        //
                        // Phase 58b — CRITICAL FIX: hard-block clicking the
                        // first organic result if its href host matches
                        // ctx.MyDomains. Previously the JS just did
                        // `a.closest('a').click()` with NO own-domain check,
                        // which is how 'goodmedika доставка' search → click
                        // → goodmedika.com.ua → billed-as-fraud-click
                        // happened. This is the SAME class of bug as the
                        // click_ad ctx-stale issue, just a different code
                        // path. Resolve the anchor URL FIRST (DOM read), let
                        // the C# layer match against MyDomains, only then
                        // execute the click. Title-substring also blocked
                        // (e.g. "Купить лекарства | GoodMedika.com.ua").
                        if (clickOrganic)
                        {
                            try
                            {
                                // 1) Read the first-organic anchor's href + title
                                //    without clicking it.
                                var organicJson = await s.ExecuteScriptAsync(
                                    "var a=document.querySelector('#search a h3');" +
                                    "if(!a) return null;" +
                                    "var anchor=a.closest('a');" +
                                    "if(!anchor||!anchor.href) return null;" +
                                    "return JSON.stringify({href:anchor.href,title:(a.innerText||a.textContent||'').slice(0,200)});",
                                    null, ct) as string;

                                bool blocked = false;
                                string? href = null, title = null;
                                if (!string.IsNullOrEmpty(organicJson))
                                {
                                    try
                                    {
                                        using var d = System.Text.Json.JsonDocument.Parse(organicJson);
                                        href  = d.RootElement.TryGetProperty("href",  out var h) ? h.GetString() : null;
                                        title = d.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
                                    }
                                    catch { /* fall through, blocked stays false */ }
                                }

                                if (!string.IsNullOrEmpty(href))
                                {
                                    var organicHost = ExtractHost(href);
                                    if (!string.IsNullOrEmpty(organicHost) &&
                                        DomainMatches(organicHost, ctx.MyDomains))
                                    {
                                        blocked = true;
                                        _log.LogWarning(
                                            "commercial_inflate SKIP click_organic (own-domain): " +
                                            "first-organic href={Href} host={Host} matched MyDomains=[{My}]",
                                            href, organicHost, string.Join(",", ctx.MyDomains));
                                    }
                                    // Title substring guard — affiliate links sometimes
                                    // route through a tracker so the host doesn't match
                                    // but the visible title contains the brand root.
                                    if (!blocked && !string.IsNullOrEmpty(title))
                                    {
                                        foreach (var d in ctx.MyDomains)
                                        {
                                            var root = DomainRoot(d);
                                            if (root.Length >= 4 &&
                                                title!.Contains(root, StringComparison.OrdinalIgnoreCase))
                                            {
                                                blocked = true;
                                                _log.LogWarning(
                                                    "commercial_inflate SKIP click_organic (own-brand-in-title): " +
                                                    "first-organic title='{Title}' contains brand-root '{Root}'",
                                                    title, root);
                                                break;
                                            }
                                        }
                                    }
                                }

                                if (!blocked)
                                {
                                    _log.LogInformation(
                                        "commercial_inflate: clicking first-organic href={Href}",
                                        href ?? "(unknown)");
                                    await s.ExecuteScriptAsync(
                                        "var a=document.querySelector('#search a h3');" +
                                        "if(a){a.closest('a').click();}",
                                        null, ct);
                                }
                            }
                            catch (Exception cex)
                            {
                                _log.LogDebug(cex, "commercial_inflate: click_organic skipped");
                            }
                        }

                        await Humanizer.IdleAsync(dwellMin * 1000, dwellMax * 1000, ct);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex,
                            "commercial_inflate: iteration {I} failed (continuing)", i + 1);
                    }
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

                // ── Phase 58 — CRITICAL FIX: sync ctx.CurrentAd* to the picked
                // target BEFORE any guard runs. Previously this assignment
                // happened ONLY inside foreach_ad's iteration body (line 885).
                // Standalone `click_ad` steps that ran outside foreach_ad
                // picked a random target but kept ctx.CurrentAdHref pointing
                // at whatever ad parse_ads / a previous loop iteration last
                // touched — which meant the AnyHostMatches pre-click guard
                // below was checking the WRONG ad. That's how own-domain
                // ads were slipping through and getting clicked + billed.
                // Set them now from this iteration's target.
                ctx.CurrentAdHref       = AdParser.UnwrapAdRedirect(target.Href);
                ctx.CurrentAdDisplayUrl = target.DisplayUrl ?? "";
                if (!string.IsNullOrEmpty(target.Title))
                    ctx.Vars["ad_title"] = target.Title;

                // ── Diagnostic chain: log the FULL provenance of this click
                // attempt so the user can audit every domain-guard decision.
                // raw_href is the href as parsed from the SERP DOM; unwrapped
                // is what we get after stripping Google's aclk/url redirector;
                // unwrapped_host is what AnyHostMatches will see; my_domains
                // is the configured skip set. With this in the log a single
                // glance tells you whether the guard SHOULD have caught it.
                var rawHostDiag       = ExtractHost(target.Href);
                var unwrappedHostDiag = ExtractHost(ctx.CurrentAdHref);
                var dispHostDiag      = ExtractHost(ctx.CurrentAdDisplayUrl);
                _log.LogInformation(
                    "click_ad PRECHECK: ad #{Id} raw_host={Raw} → unwrapped_host={Unwrapped} " +
                    "display_host={Disp} title={Title} my_domains=[{My}] target_domains=[{T}] block_domains=[{B}]",
                    target.StampId,
                    rawHostDiag,
                    unwrappedHostDiag,
                    dispHostDiag,
                    target.Title?.Length > 60 ? target.Title[..60] + "…" : target.Title,
                    string.Join(",", ctx.MyDomains),
                    string.Join(",", ctx.TargetDomains),
                    string.Join(",", ctx.BlockDomains));

                if (await AdParser.IsSelfClickAsync(s, target.Href, ct))
                {
                    _log.LogWarning(
                        "click_ad SKIP (self-click): ad #{Id} href {Href} matches current page host",
                        target.StampId, target.Href);
                    await RecordActionEventAsync(
                        step, ctx, runId, profileName,
                        "skipped", "self_click_page_host", 0, null, ct);
                    break;
                }

                // ── Pre-click belt-and-braces check ────────────────
                // Even though the if-condition / per-step filters
                // already gated this call, we run AnyHostMatches one
                // more time against MyDomains as a hard last line of
                // defence. Edge-case: a power user runs a click_ad
                // step with NO if-gate and NO skip_on_my_domain flag
                // (the schema doesn't require either). Without this
                // check, MyDomains config silently has no effect on
                // such scripts → fraud-click. We make MyDomains a
                // GLOBAL skip rule that no script can bypass.
                if (AnyHostMatches(ctx, ctx.MyDomains))
                {
                    _log.LogWarning(
                        "click_ad SKIP (own-domain hard guard): ad #{Id} click_host={ClickHost} " +
                        "display_host={DispHost} matched MyDomains=[{My}]",
                        target.StampId,
                        ExtractHost(ctx.CurrentAdHref),
                        ExtractHost(ctx.CurrentAdDisplayUrl),
                        string.Join(",", ctx.MyDomains));
                    await RecordActionEventAsync(
                        step, ctx, runId, profileName,
                        "skipped", "own_domain_hard_guard", 0, null, ct);
                    break;
                }
                if (AnyHostMatches(ctx, ctx.BlockDomains))
                {
                    _log.LogWarning(
                        "click_ad SKIP (block-list): ad #{Id} click_host={ClickHost} " +
                        "matched BlockDomains=[{B}]",
                        target.StampId,
                        ExtractHost(ctx.CurrentAdHref),
                        string.Join(",", ctx.BlockDomains));
                    await RecordActionEventAsync(
                        step, ctx, runId, profileName,
                        "skipped", "block_list", 0, null, ct);
                    break;
                }

                var tier = await AdParser.ClickAsync(s, target, ct);
                _log.LogInformation("Ad #{Id} clicked via tier {Tier}", target.StampId, tier);
                ctx.AdsClicked++;
                counters.AdsClicked++;

                // ── Post-click safety net ──────────────────────────
                // The pre-click checks rely on inputs that may lie
                // (Google's redirector hosts, ad networks that
                // anonymise the destination). The ground-truth comes
                // AFTER the navigation: location.href on the landing
                // page. If we ended up on a profile-owned domain, we
                // just self-clicked our own ad — back out immediately
                // (close the tab) and log a warning so the user can
                // grep for the pattern and add the offending tracker
                // domain to MyDomains. This catches any redirect
                // chain we couldn't anticipate.
                try
                {
                    // Brief settle so the SPA / redirect chain has
                    // time to commit a final URL before we sample.
                    await Task.Delay(800, ct);
                    var landing = await s.ExecuteScriptAsync(
                        "return location.href;", null, ct) as string;
                    if (!string.IsNullOrEmpty(landing)
                        && Uri.TryCreate(landing, UriKind.Absolute, out var lu))
                    {
                        var landingHost = lu.Host.ToLowerInvariant();
                        if (landingHost.StartsWith("www.")) landingHost = landingHost[4..];
                        if (DomainMatches(landingHost, ctx.MyDomains))
                        {
                            _log.LogWarning(
                                "POST-CLICK: ad #{Id} landed on own domain '{Host}' — closing tab. " +
                                "Add the source tracker (click_host={ClickHost}, display={DispHost}) " +
                                "to MyDomains so the next iteration's pre-check filters it out.",
                                target.StampId, landingHost,
                                ExtractHost(ctx.CurrentAdHref),
                                ExtractHost(ctx.CurrentAdDisplayUrl));
                            // Close the offending tab so we're back
                            // on the SERP. ClickAsync may have opened
                            // a new tab (tier 2/3); window.close() in
                            // that tab returns to the opener.
                            try
                            {
                                await s.ExecuteScriptAsync(
                                    "window.close(); history.back();", null, ct);
                            }
                            catch { /* best-effort tear-down */ }
                            // Don't count this as a successful click —
                            // it shouldn't have happened.
                            ctx.AdsClicked--;
                            counters.AdsClicked--;
                            // Surface to the action-events log so the
                            // user sees the skip in the analytics view.
                            await RecordActionEventAsync(
                                step, ctx, runId, profileName,
                                "skipped", "post_click_own_domain", 0, null, ct);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception postEx)
                {
                    // Non-fatal — the safety net itself failing
                    // shouldn't crash the whole script. Just log.
                    _log.LogDebug(postEx, "click_ad post-click safety net errored");
                }
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
    /// <summary>
    /// Phase 70 — universal stochastic gate. Reads the existing
    /// <see cref="ScriptStep.Probability"/> field (double 0..1).
    /// Returns true (skip) when the field is &lt; 1.0 AND a fresh roll
    /// lands above it. Default 1.0 → never skips, existing scripts
    /// behave exactly as before.
    ///
    /// Examples (translate the visual editor's 0-100 percent display to
    /// the internal 0-1 representation):
    ///   1.00 (100%) → always run (default)
    ///   0.70 (70%)  → run on ~7 of 10 invocations
    ///   0.00 (0%)   → never run
    ///
    /// Lives at the dispatch top — gate fires BEFORE the step's setup,
    /// so a probability=0 click_ad never even parses its target. Same
    /// gate is applied in graph mode at the per-node entry (line ~720)
    /// using the same field.
    /// </summary>
    private bool TryProbabilitySkip(ScriptStep step, out string reason)
    {
        reason = "";
        if (step.Probability >= 1.0) return false;
        if (step.Probability <= 0.0)
        {
            reason = "probability=0";
            // Phase 70 diagnostics — log every gate verdict so the
            // user can see exactly why a step ran or skipped. Useful
            // when a slider position seems to "not work" — usually
            // either a serialisation bug or a misunderstanding about
            // how often the step is supposed to fire over a small
            // sample size.
            _log.LogInformation(
                "Probability gate: SKIP step '{Type}' — probability=0 (always skip)",
                step.Type);
            return true;
        }
        var roll = Random.Shared.NextDouble();
        if (roll > step.Probability)
        {
            reason = $"probability={step.Probability:F2} (rolled {roll:F2})";
            _log.LogInformation(
                "Probability gate: SKIP step '{Type}' — rolled {Roll:F2} > threshold {Prob:F2} ({ProbPct}%)",
                step.Type, roll, step.Probability, (int)Math.Round(step.Probability * 100));
            return true;
        }
        // Pass — record the verdict at info level so the user can
        // verify the gate is actually consulting the value they set
        // (vs. defaulting to 1.0 = always run because of a load-path
        // bug, e.g. nested-probability not surviving the round-trip).
        _log.LogInformation(
            "Probability gate: RUN step '{Type}' — rolled {Roll:F2} <= threshold {Prob:F2} ({ProbPct}%)",
            step.Type, roll, step.Probability, (int)Math.Round(step.Probability * 100));
        return false;
    }

    private static bool TryDomainFilterSkip(
        ScriptStep step, RunContext ctx, out string reason)
    {
        reason = "";
        // Fast path: no filters set → no work.
        if (!step.SkipOnMyDomain && !step.SkipOnTarget
            && !step.OnlyOnMyDomain && !step.OnlyOnTarget
            && !step.SkipOnBlocked && !step.OnlyOnBlocked) return false;

        // ── Three-source ad-host matching ──────────────────────────
        // (1) click host — what the user navigates to (often a
        //     redirector / partner). Pre-unwrapped in foreach_ad.
        // (2) display host — the green-text URL Google shows under
        //     the ad title. Most reliable signal of advertiser ID
        //     when the click goes through an affiliate tracker.
        // (3) ad title — last-ditch keyword match. If the ad title
        //     contains a my-domain root word (e.g. "goodmedika"),
        //     count it as the user's own ad even when the click /
        //     display hosts hide that fact.
        // The gate fires if ANY of these three signals matches the
        // relevant set — "be over-eager about skip_on_my_domain to
        // never fraud-click yourself" trumps "be precise" because
        // the cost of a false-skip (one missed competitor click) is
        // much smaller than the cost of a self-click.
        var clickHost = ExtractHost(ctx.CurrentAdHref);
        var dispHost  = ExtractHost(ctx.CurrentAdDisplayUrl);
        var hasAd = !string.IsNullOrEmpty(clickHost) || !string.IsNullOrEmpty(dispHost);

        // only_on_* gates: when set, REQUIRE the ad to match. When no
        // ad is in scope, treat as "policy not satisfied" → skip.
        if (step.OnlyOnMyDomain && !(hasAd && AnyHostMatches(ctx, ctx.MyDomains)))
        { reason = "only_on_my_domain"; return true; }
        if (step.OnlyOnTarget && !(hasAd && AnyHostMatches(ctx, ctx.TargetDomains)))
        { reason = "only_on_target"; return true; }
        if (step.OnlyOnBlocked && !(hasAd && AnyHostMatches(ctx, ctx.BlockDomains)))
        { reason = "only_on_blocked"; return true; }

        // skip_on_* gates: only fire when there IS an ad and it
        // matches. With no ad, policy doesn't apply → don't skip.
        if (step.SkipOnMyDomain && hasAd && AnyHostMatches(ctx, ctx.MyDomains))
        { reason = "skip_on_my_domain"; return true; }
        if (step.SkipOnTarget && hasAd && AnyHostMatches(ctx, ctx.TargetDomains))
        { reason = "skip_on_target"; return true; }
        if (step.SkipOnBlocked && hasAd && AnyHostMatches(ctx, ctx.BlockDomains))
        { reason = "blocked"; return true; }

        return false;
    }

    /// <summary>
    /// Triple-source ad-host match: click URL, display URL, ad title
    /// keyword. Returns true if ANY of the three signals matches a
    /// domain in <paramref name="set"/>. This is the same logic
    /// ConditionEvaluator.AdHostMatches uses, plus a third pass
    /// scanning the ad title for the unqualified domain root (e.g.
    /// "goodmedika" anywhere in the title matches a "goodmedika.com.ua"
    /// entry in the set). Keeping the implementation here mirrors the
    /// per-step filters' code locality with the rest of ScriptRunner.
    /// </summary>
    private static bool AnyHostMatches(RunContext ctx, HashSet<string> set)
    {
        if (set.Count == 0) return false;
        var clickHost = ExtractHost(ctx.CurrentAdHref);
        if (!string.IsNullOrEmpty(clickHost) && DomainMatches(clickHost, set))
            return true;
        var dispHost = ExtractHost(ctx.CurrentAdDisplayUrl);
        if (!string.IsNullOrEmpty(dispHost) && DomainMatches(dispHost, set))
            return true;
        // Title keyword scan — strip the TLD/SLD off each entry to get
        // the brand root (e.g. "goodmedika.com.ua" → "goodmedika"),
        // then do a case-insensitive substring search in the ad title
        // and (if available) ad_title var. Only meaningful when the
        // brand has a distinctive root — generic brands like "shop"
        // or "store" would over-match, so we require ≥4 chars.
        if (ctx.Vars.TryGetValue("ad_title", out var title) && !string.IsNullOrEmpty(title))
        {
            foreach (var d in set)
            {
                var root = DomainRoot(d);
                if (root.Length < 4) continue;
                if (title.Contains(root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// "goodmedika.com.ua" → "goodmedika"; "shop.example.com" → "example";
    /// "example" → "example". Strips the public-suffix-ish tail by
    /// taking the leftmost label that's at least 4 chars long. Good
    /// enough for affiliate-tracker matching; not a full PSL parse.
    /// </summary>
    private static string DomainRoot(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return "";
        var d = domain.Trim().ToLowerInvariant();
        if (d.StartsWith("www.")) d = d[4..];
        var parts = d.Split('.', StringSplitOptions.RemoveEmptyEntries);
        // Take the second-to-last label if there's a multi-part domain
        // (best heuristic for "shop.example.com" → "example"); fall
        // back to the first-and-only label for bare names.
        return parts.Length >= 2 ? parts[^2] : parts.FirstOrDefault() ?? "";
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

    /// <summary>
    /// Phase 60 — render the most useful 1-2 params for a step type
    /// inline with the dispatcher's entry log. Returns either an empty
    /// string (no useful params) or a leading-space-prefixed bracket
    /// expression like "  url=…" or "  query='goodmedika' n=2".
    /// We render a SHORT snapshot — the goal is to make every step
    /// recognisable at a glance, not to dump every field.
    /// </summary>
    private static string StepParamSnapshot(ScriptStep step, RunContext ctx)
    {
        string Trim(string? v, int max = 80)
        {
            if (string.IsNullOrEmpty(v)) return "";
            return v.Length <= max ? v : v[..(max - 1)] + "…";
        }

        var t = step.Type.ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        sb.Append("  ");
        switch (t)
        {
            case "navigate":
            case "open_url":
                sb.Append("url=").Append(Trim(InterpolateVars(ParamString(step, "url") ?? "", ctx), 100));
                break;
            case "search_query":
            case "google_search":
                sb.Append("q='").Append(Trim(InterpolateVars(ParamString(step, "query") ?? "", ctx), 60)).Append("'");
                var locale = ParamString(step, "locale");
                if (!string.IsNullOrEmpty(locale)) sb.Append(" hl=").Append(locale);
                break;
            case "commercial_inflate":
                sb.Append("brand='").Append(Trim(InterpolateVars(ParamString(step, "brand") ?? "", ctx), 40)).Append("'")
                  .Append(" n=").Append(ParamInt(step, "n", 2))
                  .Append(" click_organic=").Append(ParamBool(step, "click_organic", false));
                break;
            case "click":
            case "click_selector":
                sb.Append("sel='").Append(Trim(ParamString(step, "selector"), 60)).Append("'");
                break;
            case "click_ad":
                var stamp = ParamInt(step, "stamp_id", -1);
                sb.Append(stamp >= 0 ? $"stamp_id={stamp}" : "random pick");
                sb.Append(", parsed_ads=").Append(ctx.Ads.Count);
                break;
            case "type":
            case "fill":
            case "type_text":
                sb.Append("sel='").Append(Trim(ParamString(step, "selector"), 50)).Append("' value='")
                  .Append(Trim(InterpolateVars(ParamString(step, "value") ?? "", ctx), 40)).Append("'");
                break;
            case "parse_ads":
                sb.Append("(scan SERP)");
                break;
            case "foreach":
                sb.Append("var=").Append(ParamString(step, "var") ?? "item")
                  .Append(", body_steps=").Append(step.Body.Count);
                break;
            case "foreach_ad":
                sb.Append("max_iters=").Append(ParamInt(step, "max", 5))
                  .Append(", body_steps=").Append(step.Body.Count);
                break;
            case "if":
                sb.Append("cond=").Append(Trim(step.Condition?.ToString(), 50))
                  .Append(", then=").Append(step.Then.Count)
                  .Append(", else=").Append(step.Else.Count);
                break;
            case "while":
                sb.Append("cond=").Append(Trim(step.Condition?.ToString(), 50))
                  .Append(", body=").Append(step.Body.Count);
                break;
            case "dwell":
            case "sleep":
            case "wait":
                sb.Append("min=").Append(ParamInt(step, "min", 1000)).Append("ms")
                  .Append(" max=").Append(ParamInt(step, "max", 3000)).Append("ms");
                break;
            case "set_var":
            case "set":
                sb.Append("name=").Append(ParamString(step, "name"))
                  .Append(" value='").Append(Trim(InterpolateVars(ParamString(step, "value") ?? "", ctx), 40)).Append("'");
                break;
            case "scroll":
                sb.Append("dir=").Append(ParamString(step, "direction") ?? "down")
                  .Append(", px=").Append(ParamInt(step, "amount", 600));
                break;
            case "screenshot":
                sb.Append("name=").Append(ParamString(step, "name") ?? "(auto)");
                break;
            case "extension_open":
                sb.Append("ext=").Append(ParamString(step, "extension_id"))
                  .Append(", path=").Append(ParamString(step, "path"));
                break;
            case "extension_click":
            case "extension_fill":
                sb.Append("sel='").Append(Trim(ParamString(step, "selector"), 60)).Append("'");
                break;
            case "captcha_solve":
                sb.Append("kind=").Append(ParamString(step, "kind") ?? "auto");
                break;
            case "break":
            case "continue":
                sb.Append("(loop control)");
                break;
            default:
                // Unknown step type — show first param key/value to help
                // identify it in a custom-script log.
                if (step.Params.Count > 0)
                {
                    var first = step.Params.First();
                    sb.Append(first.Key).Append("=").Append(Trim(first.Value?.ToString(), 40));
                }
                else sb.Length = 0; // no useful params, drop the leading space
                break;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Record observed ads to the competitor table. Extracted from the
    /// `parse_ads` case so foreach_ad's per-iteration re-parse can ALSO
    /// record competitors — without this, runs that use the modern
    /// foreach_ad architecture (re-parse every lap, no explicit
    /// `parse_ads` step) silently never populated the Competitors page,
    /// even when the script clicked dozens of competitor ads.
    ///
    /// Phase 70 — proper competitor classification. We check BOTH the
    /// click host and the display host against MyDomains + TargetDomains
    /// + BlockDomains and only record ads where NEITHER matches anything
    /// in My/Target/Block. The previous filter was BlockDomains-only,
    /// which polluted the Competitors page with the user's OWN ads
    /// (matching MyDomains) and their paid TARGET ads (matching
    /// TargetDomains). Those aren't competitors — they're own/paid traffic.
    /// Ad host check uses the existing AnyHostMatches (click OR display)
    /// to handle affiliate-tracker URLs the same way as ad_is_mine /
    /// ad_is_target conditions do.
    ///
    /// Errors are logged but never re-thrown — failed analytics
    /// shouldn't crash the script.
    /// </summary>
    private async Task RecordCompetitorsAsync(
        IEnumerable<AdRecord> ads, RunContext ctx,
        long runId, string profileName, CancellationToken ct)
    {
        try
        {
            var batch = new List<CompetitorRecord>();
            var capturedAt = DateTime.UtcNow;
            var skippedMine    = 0;
            var skippedTarget  = 0;
            var skippedBlocked = 0;
            foreach (var ad in ads)
            {
                // Coalesce nullable strings to "" for ExtractHost which
                // takes a non-null parameter. AdRecord.Href is required
                // but technically AdRecord.DisplayUrl is nullable
                // (some ad shapes don't surface a display URL).
                var clickHost = ExtractHost(ad.Href ?? "");
                var dispHost  = ExtractHost(ad.DisplayUrl ?? "");
                if (string.IsNullOrEmpty(clickHost) && string.IsNullOrEmpty(dispHost))
                    continue;

                // Use the same dual-host (click + display) match logic
                // as the ad_is_mine / ad_is_target / ad_is_competitor
                // conditions. Set CurrentAdHref/CurrentAdDisplayUrl
                // briefly so AnyHostMatches sees this ad — restore to
                // the prior values after each iteration so we don't
                // pollute the wider script context.
                var savedHref = ctx.CurrentAdHref;
                var savedDisp = ctx.CurrentAdDisplayUrl;
                ctx.CurrentAdHref       = ad.Href ?? "";
                ctx.CurrentAdDisplayUrl = ad.DisplayUrl ?? "";
                try
                {
                    if (AnyHostMatches(ctx, ctx.MyDomains))     { skippedMine++;    continue; }
                    if (AnyHostMatches(ctx, ctx.TargetDomains)) { skippedTarget++;  continue; }
                    if (AnyHostMatches(ctx, ctx.BlockDomains))  { skippedBlocked++; continue; }
                }
                finally
                {
                    ctx.CurrentAdHref       = savedHref;
                    ctx.CurrentAdDisplayUrl = savedDisp;
                }

                // Survived all three filters → real competitor. Use the
                // click host as the primary domain, fall back to display
                // host if the click goes through an affiliate tracker
                // with no recognisable host.
                var primaryHost = !string.IsNullOrEmpty(clickHost) ? clickHost : dispHost;
                batch.Add(new CompetitorRecord
                {
                    RunId = runId,
                    ProfileName = profileName,
                    CapturedAt = capturedAt,
                    Query = ctx.Vars.TryGetValue("current_query", out var q) ? q : "",
                    Domain = primaryHost,
                    AdTitle = ad.Title,
                    DisplayUrl = ad.DisplayUrl,
                    ClickUrl = ad.Href,
                });
            }
            if (batch.Count > 0)
            {
                await _competitors.RecordBatchAsync(batch, ct);
                _log.LogInformation(
                    "Recorded {N} competitor observation(s) for run #{R} " +
                    "(skipped {M} mine, {T} target, {B} blocked)",
                    batch.Count, runId, skippedMine, skippedTarget, skippedBlocked);
            }
            else
            {
                // Visibility log — when EVERY observed ad got filtered
                // out (often happens if every ad host is in My/Target),
                // surface why so the user can verify their domain lists
                // aren't over-greedy.
                var totalScanned = skippedMine + skippedTarget + skippedBlocked;
                if (totalScanned > 0)
                {
                    _log.LogInformation(
                        "Recorded 0 competitor observations for run #{R}: " +
                        "all {N} ads were classified as own/target/blocked " +
                        "(mine={M}, target={T}, blocked={B})",
                        runId, totalScanned, skippedMine, skippedTarget, skippedBlocked);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record competitor batch");
        }
    }

    /// <summary>
    /// Read window.location.href off the live browser session. Returns
    /// "" on any failure (driver dead, JS threw, navigation in flight)
    /// — callers treat empty as "couldn't read" and fall back gracefully.
    /// Used by foreach_ad's per-iteration resync to detect when a
    /// click navigated us away from the SERP.
    /// </summary>
    private static async Task<string> GetCurrentUrlAsync(IBrowserSession s, CancellationToken ct)
    {
        try
        {
            var url = await s.ExecuteScriptAsync("return location.href;", null, ct) as string;
            return url ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// "Are we on the SAME SERP page" — host + path equality, ignoring
    /// query string differences (Google appends &amp;sxsrf=, &amp;ei=,
    /// session tokens per request that change every refresh) and
    /// ignoring fragment. Both sides empty → false; one side empty →
    /// false. We can't compare full URLs because Google would always
    /// look "different" between page reloads.
    ///
    /// Trade-off: we accept that "/search" + "/search" matches even
    /// when the QUERY differs (q=goodmedika vs q=other). Inside
    /// foreach_ad we entered the loop on a SERP for one specific
    /// query, so a different /search?q=... would be a script bug
    /// (someone navigated mid-loop) — better to navigate back to the
    /// captured serpUrl than to keep iterating on the wrong page.
    /// </summary>
    private static bool IsSameSerpPage(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (!Uri.TryCreate(a, UriKind.Absolute, out var ua)) return false;
        if (!Uri.TryCreate(b, UriKind.Absolute, out var ub)) return false;
        return string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ua.AbsolutePath, ub.AbsolutePath, StringComparison.OrdinalIgnoreCase);
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
                        return SplitItemsString(v);
                    return Array.Empty<string>();
                case string s:
                    return SplitItemsString(s);

                // System.Text.Json deserialises `IReadOnlyDictionary<string, object?>`
                // values as JsonElement (NOT raw .NET types), so a JSON string
                // like `"items": "a\nb\nc"` lands here as
                // `JsonElement{ValueKind=String}`. Without these two cases
                // the switch fell through to `Array.Empty<string>()` and the
                // foreach silently iterated zero times — exactly the
                // symptom of "browser launches, lands on chrome://new-tab,
                // never navigates". The $varname prefix is honoured here
                // too so dynamic refs work whether the value arrived as a
                // raw string (older code paths) or a JsonElement (the
                // normal deserialised path).
                case JsonElement el when el.ValueKind == JsonValueKind.String:
                {
                    var s = el.GetString() ?? "";
                    if (s.StartsWith("$"))
                    {
                        return ctx.Vars.TryGetValue(s[1..], out var v2)
                            ? SplitItemsString(v2)
                            : Array.Empty<string>();
                    }
                    return SplitItemsString(s);
                }
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
    /// Split a free-form items string on commas, newlines, and
    /// carriage returns. The legacy web project's UI offered a
    /// textarea where users typed one keyword per line; ours kept
    /// the same semantic but the original splitter only honoured
    /// commas, so a newline-delimited list collapsed into one
    /// giant item with embedded `\n` characters — the foreach then
    /// iterated once with a malformed value (e.g. a search URL
    /// containing literal newlines), the SERP returned no ads, and
    /// the rest of the script silently no-op'd. Phase 38 fix:
    /// split on `, \n \r` together so all three layouts work
    /// (CSV, single-line, multiline).
    /// </summary>
    private static string[] SplitItemsString(string s)
        => s.Split(new[] { ',', '\n', '\r' },
                   StringSplitOptions.RemoveEmptyEntries
                 | StringSplitOptions.TrimEntries);

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

    /// <summary>
    /// Phase 69 — alias form, profile-scoped: <c>{{vault.SEED}}</c>.
    /// One identifier (no dot-separated path), letters/digits/underscore.
    /// Resolved via <see cref="GhostShell.Core.Models.VaultAliases"/>
    /// catalog + per-profile vault item lookup at run start.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex VaultAliasPattern = new(
        @"\{\{\s*vault\.([A-Za-z_][A-Za-z0-9_]*)\s*\}\}",
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
            // Phase 69 — alias form: {{vault.SEED}} → profile-bound
            // crypto_wallet.seed_phrase. Resolved aliases live in
            // ctx.VaultAliases (alias→cleartext) — populated by the
            // runner BEFORE script execution from a single
            // ResolveAliasesAsync round-trip.
            if (ctx.VaultAliases.Count > 0)
            {
                src = VaultAliasPattern.Replace(src, m =>
                {
                    var alias = m.Groups[1].Value;
                    if (!ctx.VaultAliases.TryGetValue(alias, out var v)) return m.Value;
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

    /// <summary>
    /// Phase 69 — companion to <see cref="CollectVaultRefs"/>: scan the
    /// script's JSON for <c>{{vault.ALIAS}}</c> single-identifier
    /// placeholders. The runner passes the result to
    /// <c>IVaultService.ResolveAliasesAsync(profileName, aliases)</c>
    /// at run start so all profile-bound credentials materialise in
    /// one round-trip. Aliases are case-insensitive but de-duped to
    /// the canonical form from <see cref="GhostShell.Core.Models.VaultAliases.All"/>.
    /// Unknown aliases are silently dropped — the placeholder will
    /// fall through to literal text at interpolation time, which the
    /// user can grep for in the log to identify typos.
    /// </summary>
    public static IReadOnlyList<string> CollectVaultAliases(params string?[] jsonPayloads)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in jsonPayloads)
        {
            if (string.IsNullOrEmpty(json)) continue;
            foreach (System.Text.RegularExpressions.Match m in VaultAliasPattern.Matches(json))
            {
                var alias = m.Groups[1].Value;
                if (GhostShell.Core.Models.VaultAliases.IsKnown(alias))
                    seen.Add(alias);
            }
        }
        return seen.ToList();
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
    /// Hard-abort signal — used when a step has
    /// <c>abort_on_error=true</c> and threw, OR when the browser
    /// session dies mid-run (NoSuchWindow, proxy timeout). Walks all
    /// the way out of nested loops/conditions to the top-level executor.
    /// Phase 61c — promoted to public so the abort is visible to the
    /// caller (RealProfileRunner) which previously couldn't tell
    /// "script ran cleanly" from "script aborted because the browser
    /// died" — both reached the same successful-completion code path.
    /// </summary>
    public sealed class ScriptAbortException : Exception
    {
        public ScriptAbortException(string m) : base(m) { }
    }
}
