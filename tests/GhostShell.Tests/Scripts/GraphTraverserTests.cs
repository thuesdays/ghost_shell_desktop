// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Runtime.Scripts;
using Xunit;

namespace GhostShell.Tests.Scripts;

/// <summary>
/// Unit tests for the Phase 21 graph runtime helpers — JSON parsing,
/// entry-node selection, and edge-picking with branch hints. Driven
/// by the audit findings documented in the Phase 21 report.
/// </summary>
public class GraphTraverserTests
{
    [Fact]
    public void Parse_returns_null_for_null_or_empty_input()
    {
        Assert.Null(GraphTraverser.Parse(null,    null));
        Assert.Null(GraphTraverser.Parse("",      null));
        Assert.Null(GraphTraverser.Parse("   ",   null));
    }

    [Fact]
    public void Parse_returns_null_for_non_array_root()
    {
        Assert.Null(GraphTraverser.Parse("{\"id\":\"x\"}", null));
    }

    [Fact]
    public void Parse_skips_nodes_with_missing_id_or_type()
    {
        var nodes = """
        [
          { "id": "a", "type": "navigate" },
          { "id": "",  "type": "log"      },
          {            "type": "log"      },
          { "id": "b", "type": ""         },
          { "id": "c", "type": "log"      }
        ]
        """;
        var g = GraphTraverser.Parse(nodes, null);
        Assert.NotNull(g);
        Assert.Equal(2, g!.Nodes.Count);
        Assert.Contains(g.Nodes, n => n.Id == "a");
        Assert.Contains(g.Nodes, n => n.Id == "c");
    }

    [Fact]
    public void Parse_drops_edges_that_reference_unknown_nodes()
    {
        var nodes = """[{ "id": "a", "type": "log" }, { "id": "b", "type": "log" }]""";
        var edges = """
        [
          { "from": "a", "to": "b" },
          { "from": "a", "to": "ghost" },
          { "from": "missing", "to": "b" }
        ]
        """;
        var g = GraphTraverser.Parse(nodes, edges);
        Assert.NotNull(g);
        Assert.Single(g!.Edges);
        Assert.Equal("a", g.Edges[0].From);
        Assert.Equal("b", g.Edges[0].To);
    }

    [Fact]
    public void Parse_hydrates_per_step_flags_from_node_json()
    {
        // Phase 21 audit fix #5 — flags must round-trip through Parse.
        var nodes = """
        [
          {
            "id": "a", "type": "click_selector",
            "params": { "selector": ".btn" },
            "enabled": false,
            "probability": 0.5,
            "abort_on_error": true,
            "skip_on_my_domain": true,
            "skip_on_target": true,
            "only_on_target": true,
            "only_on_my_domain": true
          }
        ]
        """;
        var g = GraphTraverser.Parse(nodes, null);
        Assert.NotNull(g);
        var step = g!.Nodes[0].Step;
        Assert.False(step.Enabled);
        Assert.Equal(0.5, step.Probability);
        Assert.True(step.AbortOnError);
        Assert.True(step.SkipOnMyDomain);
        Assert.True(step.SkipOnTarget);
        Assert.True(step.OnlyOnTarget);
        Assert.True(step.OnlyOnMyDomain);
    }

    [Fact]
    public void FindEntry_picks_topmost_then_leftmost_when_multiple_candidates()
    {
        var nodes = """
        [
          { "id": "low",      "type": "log", "x": 100, "y": 300 },
          { "id": "topright", "type": "log", "x": 500, "y": 50  },
          { "id": "topleft",  "type": "log", "x": 50,  "y": 50  }
        ]
        """;
        var g = GraphTraverser.Parse(nodes, null)!;
        var entry = GraphTraverser.FindEntry(g);
        Assert.NotNull(entry);
        Assert.Equal("topleft", entry!.Id);
    }

    [Fact]
    public void FindEntry_returns_null_when_every_node_has_inbound()
    {
        // a → b → a is a cycle with no node lacking inbound.
        var nodes = """[{ "id": "a", "type": "log" }, { "id": "b", "type": "log" }]""";
        var edges = """[{"from":"a","to":"b"},{"from":"b","to":"a"}]""";
        var g = GraphTraverser.Parse(nodes, edges)!;
        Assert.Null(GraphTraverser.FindEntry(g));
    }

    [Fact]
    public void PickNextEdge_returns_null_when_no_edges()
    {
        Assert.Null(GraphTraverser.PickNextEdge(Array.Empty<GraphTraverser.GraphEdge>()));
    }

    [Fact]
    public void PickNextEdge_picks_labelled_edge_matching_branch_hint()
    {
        var edges = new[]
        {
            new GraphTraverser.GraphEdge { From = "a", To = "b", Label = "then" },
            new GraphTraverser.GraphEdge { From = "a", To = "c", Label = "else" },
        };
        Assert.Equal("b", GraphTraverser.PickNextEdge(edges, "then")!.To);
        Assert.Equal("c", GraphTraverser.PickNextEdge(edges, "else")!.To);
    }

    [Fact]
    public void PickNextEdge_falls_through_to_default_label_when_hint_missing()
    {
        var edges = new[]
        {
            new GraphTraverser.GraphEdge { From = "a", To = "b", Label = "default" },
            new GraphTraverser.GraphEdge { From = "a", To = "c", Label = "fallback" },
        };
        Assert.Equal("b", GraphTraverser.PickNextEdge(edges, "missing-label")!.To);
    }

    [Fact]
    public void PickNextEdge_falls_through_to_first_when_no_default_and_no_match()
    {
        var edges = new[]
        {
            new GraphTraverser.GraphEdge { From = "a", To = "b", Label = "x" },
            new GraphTraverser.GraphEdge { From = "a", To = "c", Label = "y" },
        };
        Assert.Equal("b", GraphTraverser.PickNextEdge(edges, "z")!.To);
    }
}
