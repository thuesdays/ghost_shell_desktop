// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.App.Navigation;

/// <summary>
/// One row in the sidebar. Either a navigable page (<see cref="PageKey"/>
/// non-null) or a section label (<see cref="IsSectionLabel"/> true).
/// We keep this dumb on purpose — the sidebar control just renders
/// what MainViewModel hands it.
/// </summary>
public sealed class NavItem
{
    public string? PageKey { get; init; }
    public required string Label { get; init; }
    public string Icon { get; init; } = string.Empty;
    public bool IsSectionLabel { get; init; }
    public string? Badge { get; init; }
}
