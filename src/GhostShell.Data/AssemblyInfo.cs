// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Runtime.CompilerServices;

// Phase 28 — let the unit-test project poke at internal classes
// (TrafficService's bucket math + MergeDeltas pre-merge helper).
// Keeps the service `internal sealed` so callers in other layers
// have to go through the IService interface, while the test tier
// gets to verify the deterministic helpers directly.
[assembly: InternalsVisibleTo("GhostShell.Tests")]
