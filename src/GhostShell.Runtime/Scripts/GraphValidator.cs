// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Phase 21 — pre-save validation for graph-mode scripts. The editor
/// runs this before persisting and surfaces non-fatal issues
/// (warnings) and fatal ones (blockers) to the user.
///
/// Levels:
///   • Error   — graph cannot run as-is. Save is blocked.
///   • Warning — graph runs but has structural smells. Save is allowed.
///
/// All checks are pure functions over the parsed graph; the editor
/// can call <see cref="Validate"/> on every save click without
/// touching the runner or the database.
/// </summary>
public static class GraphValidator
{
    public enum Severity { Warning, Error }

    public sealed record Issue(Severity Level, string Code, string Message, string? NodeId);

    /// <summary>
    /// Run the full validation suite. Returns an empty list when the
    /// graph is clean. Caller decides what to do based on the
    /// <see cref="Issue.Level"/> of each item.
    /// </summary>
    public static IReadOnlyList<Issue> Validate(GraphTraverser.ParsedGraph g)
    {
        var issues = new List<Issue>();

        if (g.Nodes.Count == 0)
        {
            issues.Add(new Issue(Severity.Error, "EMPTY",
                "Graph has no nodes — add at least one action.", null));
            return issues;
        }

        // ── Entry points ──
        var entryNodes = g.Nodes.Where(n => g.Incoming[n.Id].Count == 0).ToList();
        if (entryNodes.Count == 0)
        {
            issues.Add(new Issue(Severity.Error, "NO_ENTRY",
                "Graph has no entry node — every node has an incoming edge. " +
                "The runtime can't decide where to start.", null));
        }
        else if (entryNodes.Count > 1)
        {
            issues.Add(new Issue(Severity.Warning, "MULTIPLE_ENTRIES",
                $"{entryNodes.Count} candidate entry nodes. The runner picks " +
                "the topmost (lowest Y); other candidates are unreachable.",
                entryNodes[0].Id));
        }

        // ── Reachability from entry ──
        if (entryNodes.Count > 0)
        {
            var entry = GraphTraverser.FindEntry(g);
            if (entry is not null)
            {
                var reachable = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<string>();
                queue.Enqueue(entry.Id);
                reachable.Add(entry.Id);
                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    foreach (var e in g.Outgoing[cur])
                        if (reachable.Add(e.To)) queue.Enqueue(e.To);
                }
                foreach (var n in g.Nodes)
                {
                    if (!reachable.Contains(n.Id))
                    {
                        issues.Add(new Issue(Severity.Warning, "UNREACHABLE",
                            $"Node '{n.Id}' ({n.Type}) is unreachable from the entry — it will never run.",
                            n.Id));
                    }
                }
            }
        }

        // ── Orphan nodes (no in AND no out) ──
        // Skipped if the orphan IS the entry — single-node scripts are
        // legitimate and produce in=0/out=0 for that lone node.
        if (g.Nodes.Count > 1)
        {
            foreach (var n in g.Nodes)
            {
                if (g.Incoming[n.Id].Count == 0 && g.Outgoing[n.Id].Count == 0)
                {
                    issues.Add(new Issue(Severity.Warning, "ORPHAN",
                        $"Node '{n.Id}' ({n.Type}) has no edges — disconnected from the graph.",
                        n.Id));
                }
            }
        }

        // ── Conditional branch sanity ──
        foreach (var n in g.Nodes)
        {
            if (!string.Equals(n.Type, "if", StringComparison.OrdinalIgnoreCase)) continue;
            var outs = g.Outgoing[n.Id];
            if (outs.Count == 0)
            {
                issues.Add(new Issue(Severity.Warning, "IF_NO_BRANCHES",
                    $"if-node '{n.Id}' has no outgoing edges — both branches are dead ends.",
                    n.Id));
                continue;
            }
            var hasThen = outs.Any(e => string.Equals(e.Label, "then", StringComparison.OrdinalIgnoreCase));
            var hasElse = outs.Any(e => string.Equals(e.Label, "else", StringComparison.OrdinalIgnoreCase));
            if (!hasThen && !hasElse)
            {
                issues.Add(new Issue(Severity.Warning, "IF_UNLABELED",
                    $"if-node '{n.Id}' outgoing edges have no 'then'/'else' labels — " +
                    "runtime falls back to the first edge for both branches.",
                    n.Id));
            }
        }

        // ── Non-conditional fan-out ──
        // Multiple outgoing edges only make sense on if-nodes (then/else).
        // Anywhere else they're an authoring error — runtime takes only
        // the first edge, the rest are dead.
        foreach (var n in g.Nodes)
        {
            if (string.Equals(n.Type, "if", StringComparison.OrdinalIgnoreCase)) continue;
            if (g.Outgoing[n.Id].Count > 1)
            {
                issues.Add(new Issue(Severity.Warning, "AMBIGUOUS_FANOUT",
                    $"Node '{n.Id}' ({n.Type}) has {g.Outgoing[n.Id].Count} outgoing edges " +
                    "but isn't an if-node — only the first edge will be followed.",
                    n.Id));
            }
        }

        // ── Self-loops ──
        // Allowed (acts as a 1-node cycle, capped by MaxNodeVisits) but
        // worth flagging since most authors create them by mistake.
        foreach (var e in g.Edges)
        {
            if (string.Equals(e.From, e.To, StringComparison.Ordinal))
            {
                issues.Add(new Issue(Severity.Warning, "SELF_LOOP",
                    $"Node '{e.From}' has an edge to itself — runs until the visit cap " +
                    $"({GraphTraverser.MaxNodeVisits}) trips.", e.From));
            }
        }

        return issues;
    }
}
