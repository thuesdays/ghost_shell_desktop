// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using GhostShell.Runtime.Scripts;
using Xunit;

namespace GhostShell.Tests.Scripts;

/// <summary>
/// Phase 21 audit fix #5 — per-step flags must round-trip in graph
/// mode the same way they do in list mode. This test parses a node
/// JSON, walks it through GraphTraverser, and verifies every flag
/// survives.
///
/// We also exercise common edge-cases (NaN-y probability, missing
/// flags treated as defaults).
/// </summary>
public class ScriptRoundTripTests
{
    [Fact]
    public void All_per_step_flags_round_trip_through_graph_parser()
    {
        var nodesJson = """
        [
          {
            "id": "n1",
            "type": "click_selector",
            "params": { "selector": ".btn" },
            "x": 10, "y": 20,
            "enabled": false,
            "probability": 0.42,
            "abort_on_error": true,
            "skip_on_my_domain": true,
            "skip_on_target": true,
            "only_on_target": true,
            "only_on_my_domain": true
          }
        ]
        """;

        var g = GraphTraverser.Parse(nodesJson, null);
        Assert.NotNull(g);
        var step = g!.Nodes[0].Step;
        Assert.False(step.Enabled);
        Assert.Equal(0.42, step.Probability, precision: 2);
        Assert.True(step.AbortOnError);
        Assert.True(step.SkipOnMyDomain);
        Assert.True(step.SkipOnTarget);
        Assert.True(step.OnlyOnTarget);
        Assert.True(step.OnlyOnMyDomain);
    }

    [Fact]
    public void Probability_is_clamped_to_zero_one_range()
    {
        var nodesJson = """
        [
          { "id": "a", "type": "log", "probability": -0.5 },
          { "id": "b", "type": "log", "probability":  1.7 },
          { "id": "c", "type": "log", "probability":  0.0 },
          { "id": "d", "type": "log" }
        ]
        """;
        var g = GraphTraverser.Parse(nodesJson, null)!;
        Assert.Equal(0.0, g.Nodes[0].Step.Probability);
        Assert.Equal(1.0, g.Nodes[1].Step.Probability);
        Assert.Equal(0.0, g.Nodes[2].Step.Probability);
        Assert.Equal(1.0, g.Nodes[3].Step.Probability); // default
    }

    [Fact]
    public void Missing_flags_default_to_false_and_one()
    {
        var nodesJson = """
        [{ "id": "a", "type": "log", "params": {} }]
        """;
        var g = GraphTraverser.Parse(nodesJson, null)!;
        var step = g.Nodes[0].Step;
        Assert.True(step.Enabled);             // default true
        Assert.Equal(1.0, step.Probability);   // default 1.0
        Assert.False(step.AbortOnError);
        Assert.False(step.SkipOnMyDomain);
        Assert.False(step.SkipOnTarget);
        Assert.False(step.OnlyOnTarget);
        Assert.False(step.OnlyOnMyDomain);
    }

    [Fact]
    public void Edge_label_round_trip()
    {
        var nodesJson = """
        [{"id":"a","type":"if"},{"id":"b","type":"log"},{"id":"c","type":"log"}]
        """;
        var edgesJson = """
        [{"from":"a","to":"b","label":"then"},
         {"from":"a","to":"c","label":"else"}]
        """;
        var g = GraphTraverser.Parse(nodesJson, edgesJson)!;
        Assert.Equal(2, g.Edges.Count);
        Assert.Contains(g.Edges, e => e.Label == "then" && e.To == "b");
        Assert.Contains(g.Edges, e => e.Label == "else" && e.To == "c");
    }

    [Fact]
    public void Malformed_edges_json_treated_as_no_edges()
    {
        var nodesJson = """[{"id":"a","type":"log"}]""";
        var g = GraphTraverser.Parse(nodesJson, "not-json");
        Assert.NotNull(g);
        Assert.Empty(g!.Edges);
    }

    [Fact]
    public void Params_dictionary_preserves_json_element_values()
    {
        // Phase 14 contract: params survive as JsonElement so the
        // dispatcher's typed reads (ParamString / ParamInt / ParamBool)
        // continue to work identically in graph mode.
        var nodesJson = """
        [{
          "id":"a","type":"navigate",
          "params": { "url": "https://example.com/", "tries": 3, "force": true }
        }]
        """;
        var g = GraphTraverser.Parse(nodesJson, null)!;
        var p = g.Nodes[0].Step.Params;
        Assert.True(p.ContainsKey("url"));
        Assert.True(p.ContainsKey("tries"));
        Assert.True(p.ContainsKey("force"));
        // Each value is stored as JsonElement (clone so subsequent
        // GraphTraverser disposals don't break us).
        Assert.IsType<JsonElement>(p["url"]);
        Assert.IsType<JsonElement>(p["tries"]);
        Assert.IsType<JsonElement>(p["force"]);
    }
}
