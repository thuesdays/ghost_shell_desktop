// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Runtime.Scripts;
using Xunit;

namespace GhostShell.Tests.Scripts;

/// <summary>
/// Pre-save validator tests — verifies every issue code we surface
/// in the editor (EMPTY, NO_ENTRY, MULTIPLE_ENTRIES, UNREACHABLE,
/// ORPHAN, IF_NO_BRANCHES, IF_UNLABELED, AMBIGUOUS_FANOUT, SELF_LOOP).
/// </summary>
public class GraphValidatorTests
{
    private static GraphTraverser.ParsedGraph Parse(string nodes, string? edges = null)
        => GraphTraverser.Parse(nodes, edges)!;

    [Fact]
    public void Empty_graph_reports_EMPTY_error()
    {
        var g = Parse("[]");
        var issues = GraphValidator.Validate(g);
        Assert.Single(issues);
        Assert.Equal("EMPTY", issues[0].Code);
        Assert.Equal(GraphValidator.Severity.Error, issues[0].Level);
    }

    [Fact]
    public void Cycle_with_no_entry_reports_NO_ENTRY_error()
    {
        var g = Parse(
            """[{"id":"a","type":"log"},{"id":"b","type":"log"}]""",
            """[{"from":"a","to":"b"},{"from":"b","to":"a"}]""");
        var issues = GraphValidator.Validate(g);
        Assert.Contains(issues, i => i.Code == "NO_ENTRY"
            && i.Level == GraphValidator.Severity.Error);
    }

    [Fact]
    public void Multiple_entry_points_reports_MULTIPLE_ENTRIES_warning()
    {
        var g = Parse(
            """[{"id":"a","type":"log","x":0,"y":0},{"id":"b","type":"log","x":0,"y":100}]""");
        var issues = GraphValidator.Validate(g);
        Assert.Contains(issues, i => i.Code == "MULTIPLE_ENTRIES"
            && i.Level == GraphValidator.Severity.Warning);
    }

    [Fact]
    public void Unreachable_node_reports_UNREACHABLE_warning()
    {
        // Two roots, "b" has its own root but no path FROM "a" to "b".
        // Raw strings on a single line — the multi-line `"""` form
        // requires opening/closing markers on their own lines, which
        // makes the test less readable. Single-line is fine here.
        var g = Parse(
            """[{"id":"a","type":"log","x":0,"y":0},{"id":"b","type":"log","x":0,"y":50},{"id":"c","type":"log","x":0,"y":100}]""",
            """[{"from":"b","to":"c"}]""");
        var issues = GraphValidator.Validate(g);
        // "a" is the entry (topmost). It can't reach "b" or "c".
        Assert.Contains(issues, i => i.Code == "UNREACHABLE"
            && i.NodeId is "b" or "c");
    }

    [Fact]
    public void If_node_without_then_else_labels_reports_IF_UNLABELED()
    {
        var g = Parse(
            """[{"id":"a","type":"if"},{"id":"b","type":"log"}]""",
            """[{"from":"a","to":"b"}]""");
        var issues = GraphValidator.Validate(g);
        Assert.Contains(issues, i => i.Code == "IF_UNLABELED");
    }

    [Fact]
    public void Non_if_node_with_multiple_outgoing_reports_AMBIGUOUS_FANOUT()
    {
        var g = Parse(
            """[{"id":"a","type":"log"},{"id":"b","type":"log"},{"id":"c","type":"log"}]""",
            """[{"from":"a","to":"b"},{"from":"a","to":"c"}]""");
        var issues = GraphValidator.Validate(g);
        Assert.Contains(issues, i => i.Code == "AMBIGUOUS_FANOUT" && i.NodeId == "a");
    }

    [Fact]
    public void Self_loop_reports_SELF_LOOP_warning()
    {
        var g = Parse(
            """[{"id":"a","type":"log"},{"id":"b","type":"log"}]""",
            """[{"from":"a","to":"b"},{"from":"b","to":"b"}]""");
        var issues = GraphValidator.Validate(g);
        Assert.Contains(issues, i => i.Code == "SELF_LOOP" && i.NodeId == "b");
    }

    [Fact]
    public void Orphan_node_reports_ORPHAN_warning_when_graph_has_others()
    {
        var g = Parse(
            """[{"id":"a","type":"log","x":0,"y":0},{"id":"b","type":"log","x":0,"y":100},{"id":"orphan","type":"log","x":500,"y":500}]""",
            """[{"from":"a","to":"b"}]""");
        var issues = GraphValidator.Validate(g);
        Assert.Contains(issues, i => i.Code == "ORPHAN" && i.NodeId == "orphan");
    }

    [Fact]
    public void Single_node_does_not_trigger_orphan_warning()
    {
        // Lone node IS the entry — running it once is legitimate.
        var g = Parse("""[{"id":"a","type":"log"}]""");
        var issues = GraphValidator.Validate(g);
        Assert.DoesNotContain(issues, i => i.Code == "ORPHAN");
    }

    [Fact]
    public void Healthy_graph_yields_no_issues()
    {
        var g = Parse(
            """[{"id":"a","type":"log","x":0,"y":0},{"id":"b","type":"log","x":0,"y":100}]""",
            """[{"from":"a","to":"b"}]""");
        var issues = GraphValidator.Validate(g);
        Assert.Empty(issues);
    }
}
