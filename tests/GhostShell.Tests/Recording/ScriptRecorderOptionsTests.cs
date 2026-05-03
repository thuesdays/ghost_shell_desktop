// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Services;
using Xunit;

namespace GhostShell.Tests.Recording;

/// <summary>
/// Phase 65 — sanity tests for ScriptRecorderOptions defaults. The
/// recorder service itself is hard to unit-test (needs a live browser
/// session), so we focus on the option-class invariants here.
/// </summary>
public sealed class ScriptRecorderOptionsTests
{
    [Fact]
    public void Defaults_CaptureAllGestureTypes()
    {
        var opts = new ScriptRecorderOptions();

        Assert.True(opts.CaptureClicks);
        Assert.True(opts.CaptureTyping);
        Assert.True(opts.CaptureNavigations);
        Assert.True(opts.CaptureScrolls);
        Assert.True(opts.CaptureDwells);
    }

    [Fact]
    public void Defaults_TimingFieldsHaveSaneValues()
    {
        var opts = new ScriptRecorderOptions();

        // Dwells fire after gaps > 1.5s.
        Assert.InRange(opts.DwellMinMs, 500, 5000);
        // Scroll noise filter — at least a few dozen pixels.
        Assert.InRange(opts.ScrollMinPixels, 20, 300);
        // Typing debounce — not too aggressive, not too lazy.
        Assert.InRange(opts.TypingDebounceMs, 200, 2000);
    }

    [Fact]
    public void InitOnly_AllowsCustomization()
    {
        var opts = new ScriptRecorderOptions
        {
            CaptureClicks = false,
            CaptureTyping = false,
            DwellMinMs = 5000,
        };

        Assert.False(opts.CaptureClicks);
        Assert.False(opts.CaptureTyping);
        Assert.Equal(5000, opts.DwellMinMs);
        // Other fields remain default.
        Assert.True(opts.CaptureNavigations);
    }
}
