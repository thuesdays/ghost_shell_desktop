// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using GhostShell.Core.Models;

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Phase 21 — graph-mode runtime helpers.
///
/// Parses the script's <see cref="Script.NodesJson"/> + <see cref="Script.EdgesJson"/>
/// into an executable structure, picks an entry point, and yields
/// each node in execution order following outgoing edges. The
/// caller (ScriptRunner) feeds those nodes through the same
/// dispatcher used in list mode — so all 40+ actions, conditions,
/// and per-step flags work identically in both modes.
///
/// Execution rules:
///   • Entry point = node with no incoming edges. If multiple nodes
///     qualify, the one with the lowest Y (highest on the canvas)
///     wins; if Y ties, lowest X.
///   • Out-degree 1: follow that edge to the next node.
///   • Out-degree &gt; 1 on an <c>if</c> node: pick the edge labelled
///     "then" or "else" based on the evaluated condition. If the
///     matching label is missing, fall through to a "default" label,
///     then to the first edge by stable order.
///   • Out-degree &gt; 1 on any other node: take the first edge by
///     stable insertion order. Other branches are ignored — graphs
///     with non-conditional fan-out are an authoring error and the
///     validator flags them.
///   • Cycles: capped at <see cref="MaxNodeVisits"/> to prevent
///     pathological graphs from spinning forever. Hitting the cap
///     logs a warning and aborts the run with status=partial.
/// </summary>
public sealed class GraphTraverser
{
    /// <summary>Hard cap on total node visits in one graph run. Blocks
    /// pathological cycles (a → b → a → b …) without manual edge
    /// removal. 10000 is enough for any reasonable script — typical
    /// runs visit &lt; 100 nodes.</summary>
    public const int MaxNodeVisits = 10_000;

    public sealed class GraphNode
    {
        public required string Id { get; init; }
        public required string Type { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public required ScriptStep Step { get; init; }
    }

    public sealed class GraphEdge
    {
        public required string From { get; init; }
        public required string To { get; init; }
        /// <summary>Optional label — usually "then" / "else" / "default".</summary>
        public string? Label { get; init; }
    }

    public sealed class ParsedGraph
    {
        public required IReadOnlyList<GraphNode> Nodes { get; init; }
        public required IReadOnlyList<GraphEdge> Edges { get; init; }
        public required Dictionary<string, GraphNode> NodeIndex { get; init; }
        public required Dictionary<string, List<GraphEdge>> Outgoing { get; init; }
        public required Dictionary<string, List<GraphEdge>> Incoming { get; init; }
    }

    /// <summary>
    /// Parse a script's nodes + edges JSON into an indexed graph
    /// structure ready for traversal. Returns null if the JSON is
    /// missing or malformed (caller should fall back to list mode
    /// or report an error).
    /// </summary>
    public static ParsedGraph? Parse(string? nodesJson, string? edgesJson)
    {
        if (string.IsNullOrWhiteSpace(nodesJson)) return null;

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        try
        {
            using var ndoc = JsonDocument.Parse(nodesJson);
            if (ndoc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var n in ndoc.RootElement.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Object) continue;
                var id = n.TryGetProperty("id", out var idEl)
                    ? idEl.GetString() ?? "" : "";
                var type = n.TryGetProperty("type", out var tEl)
                    ? tEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type)) continue;
                var x = n.TryGetProperty("x", out var xEl) && xEl.ValueKind == JsonValueKind.Number
                    ? xEl.GetDouble() : 0;
                var y = n.TryGetProperty("y", out var yEl) && yEl.ValueKind == JsonValueKind.Number
                    ? yEl.GetDouble() : 0;

                // Build a ScriptStep from this node's payload — same
                // shape the dispatcher already understands. We treat
                // the node JSON as a step JSON for everything except
                // the id/x/y fields.
                var step = ParseStep(n);
                nodes.Add(new GraphNode
                {
                    Id   = id,
                    Type = type,
                    X    = x,
                    Y    = y,
                    Step = step,
                });
            }
        }
        catch
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(edgesJson))
        {
            try
            {
                using var edoc = JsonDocument.Parse(edgesJson);
                if (edoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in edoc.RootElement.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.Object) continue;
                        var from = e.TryGetProperty("from", out var fr)
                            ? fr.GetString() ?? "" : "";
                        var to   = e.TryGetProperty("to", out var tr)
                            ? tr.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) continue;
                        var label = e.TryGetProperty("label", out var l)
                            ? l.GetString() : null;
                        edges.Add(new GraphEdge { From = from, To = to, Label = label });
                    }
                }
            }
            catch { /* malformed edges → ignore (treat as no edges) */ }
        }

        var index = nodes.ToDictionary(n => n.Id, n => n, StringComparer.Ordinal);
        // Drop edges that reference unknown nodes — the validator will
        // flag them in the editor; the runtime just ignores them.
        edges = edges.Where(e => index.ContainsKey(e.From) && index.ContainsKey(e.To)).ToList();

        var outgoing = nodes.ToDictionary(
            n => n.Id, _ => new List<GraphEdge>(), StringComparer.Ordinal);
        var incoming = nodes.ToDictionary(
            n => n.Id, _ => new List<GraphEdge>(), StringComparer.Ordinal);
        foreach (var e in edges)
        {
            outgoing[e.From].Add(e);
            incoming[e.To].Add(e);
        }

        return new ParsedGraph
        {
            Nodes = nodes,
            Edges = edges,
            NodeIndex = index,
            Outgoing  = outgoing,
            Incoming  = incoming,
        };
    }

    /// <summary>Build a <see cref="ScriptStep"/> from one node's JSON
    /// payload. Mirrors the existing list-mode parser — picks up
    /// type / params / enabled / per-step flags / nested branches.</summary>
    private static ScriptStep ParseStep(JsonElement n)
    {
        var type = n.GetProperty("type").GetString() ?? "";
        var paramsDict = new Dictionary<string, object?>();
        if (n.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in p.EnumerateObject())
                paramsDict[prop.Name] = prop.Value.Clone();
        }
        var enabled = !n.TryGetProperty("enabled", out var en) || en.GetBoolean();
        var probability = n.TryGetProperty("probability", out var pr) && pr.ValueKind == JsonValueKind.Number
            ? Math.Clamp(pr.GetDouble(), 0.0, 1.0)
            : 1.0;
        var abortOnError    = n.TryGetProperty("abort_on_error", out var ae)    && ae.ValueKind == JsonValueKind.True;
        var skipOnMyDomain  = n.TryGetProperty("skip_on_my_domain", out var sm) && sm.ValueKind == JsonValueKind.True;
        var skipOnTarget    = n.TryGetProperty("skip_on_target", out var st)    && st.ValueKind == JsonValueKind.True;
        var onlyOnTarget    = n.TryGetProperty("only_on_target", out var ot)    && ot.ValueKind == JsonValueKind.True;
        var onlyOnMyDomain  = n.TryGetProperty("only_on_my_domain", out var om) && om.ValueKind == JsonValueKind.True;

        return new ScriptStep
        {
            Type           = type,
            Params         = paramsDict,
            Enabled        = enabled,
            Probability    = probability,
            AbortOnError   = abortOnError,
            SkipOnMyDomain = skipOnMyDomain,
            SkipOnTarget   = skipOnTarget,
            OnlyOnTarget   = onlyOnTarget,
            OnlyOnMyDomain = onlyOnMyDomain,
        };
    }

    /// <summary>
    /// Pick the entry node — one with no inbound edges. Ties broken
    /// by topmost (lowest Y), then leftmost (lowest X) on the canvas.
    /// Returns null if every node has an inbound edge (graph is one
    /// big cycle, no entry — validator flags this).
    /// </summary>
    public static GraphNode? FindEntry(ParsedGraph g)
    {
        var candidates = g.Nodes
            .Where(n => g.Incoming[n.Id].Count == 0)
            .OrderBy(n => n.Y)
            .ThenBy(n => n.X)
            .ToList();
        return candidates.FirstOrDefault();
    }

    /// <summary>
    /// Pick the next outgoing edge to follow. The <paramref name="branchHint"/>
    /// (e.g. "then" / "else" — supplied by the dispatcher after it
    /// evaluates an <c>if</c> node's condition) selects the matching
    /// labelled edge; when no labelled edge exists, falls through to
    /// "default" then the first edge.
    /// </summary>
    public static GraphEdge? PickNextEdge(
        IReadOnlyList<GraphEdge> outgoing, string? branchHint = null)
    {
        if (outgoing.Count == 0) return null;
        if (outgoing.Count == 1) return outgoing[0];
        if (!string.IsNullOrEmpty(branchHint))
        {
            var labelled = outgoing.FirstOrDefault(
                e => string.Equals(e.Label, branchHint, StringComparison.OrdinalIgnoreCase));
            if (labelled is not null) return labelled;
        }
        var def = outgoing.FirstOrDefault(
            e => string.Equals(e.Label, "default", StringComparison.OrdinalIgnoreCase));
        return def ?? outgoing[0];
    }
}
