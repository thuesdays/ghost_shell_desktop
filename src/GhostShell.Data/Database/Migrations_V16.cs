// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V16 — Graph-mode editor support (Phase 21).
///
/// Adds three columns on <c>scripts</c> driving the new free-form
/// node-graph editor:
///
///   • <c>layout_mode</c> ("list" or "graph", default "list") —
///     scripts default to the existing sequential list-of-steps
///     execution model. Switching to "graph" enables free placement
///     of step nodes on a canvas with manual edges between them.
///   • <c>nodes_json</c> — JSON array of node objects. Each node has
///     a unique id, x/y coords, the same shape as a sequential step
///     (<c>type</c> + <c>params</c> + flags). NULL when layout=list.
///   • <c>edges_json</c> — JSON array of edge objects {from, to, label}.
///     Edges drive runtime traversal in graph mode. NULL when
///     layout=list.
///
/// Graph mode is fully back-compat: when layout_mode='list', the
/// runner uses StepsJson exactly as before. When layout_mode='graph',
/// the runner ignores StepsJson and traverses the node graph instead.
/// </summary>
internal static class Migrations_V16
{
    internal static readonly string[] Statements =
    {
        "ALTER TABLE scripts ADD COLUMN layout_mode TEXT NOT NULL DEFAULT 'list';",
        "ALTER TABLE scripts ADD COLUMN nodes_json  TEXT;",
        "ALTER TABLE scripts ADD COLUMN edges_json  TEXT;",
    };
}
