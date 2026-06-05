// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Services;
using GhostShell.Runtime.Scripts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GhostShell.Tests.Scripts;

/// <summary>
/// Feature #3 — captcha router selection logic. Pins provider ordering,
/// capability gating, manual fallback, the "sorry"-is-unsolvable rule, and the
/// per-provider metrics. Browser/HTTP-bound solver internals aren't unit-tested
/// here; the routing brain is.
/// </summary>
public sealed class CaptchaSolverRouterTests
{
    private static CaptchaSolverRouter Router(ICaptchaSolver manual, params ICaptchaSolver[] automated)
        => new(manual, automated, NullLogger<CaptchaSolverRouter>.Instance);

    [Fact]
    public async Task PrefersFirstAutomatedThatHandlesKind()
    {
        var a = new Fake("a", automated: true, handles: _ => true, result: true);
        var b = new Fake("b", automated: true, handles: _ => true, result: true);
        var manual = new Fake("manual", automated: false, handles: _ => true, result: true);

        var ok = await Router(manual, a, b).SolveAsync(null!, "recaptcha", TimeSpan.FromSeconds(1));

        Assert.True(ok);
        Assert.Equal(1, a.SolveCalls);
        Assert.Equal(0, b.SolveCalls);
        Assert.Equal(0, manual.SolveCalls);
    }

    [Fact]
    public async Task FallsThroughToNextAutomatedOnFailure()
    {
        var a = new Fake("a", automated: true, handles: _ => true, result: false);
        var b = new Fake("b", automated: true, handles: _ => true, result: true);
        var manual = new Fake("manual", automated: false, handles: _ => true, result: true);

        var ok = await Router(manual, a, b).SolveAsync(null!, "hcaptcha", TimeSpan.FromSeconds(1));

        Assert.True(ok);
        Assert.Equal(1, a.SolveCalls);
        Assert.Equal(1, b.SolveCalls);
        Assert.Equal(0, manual.SolveCalls);
    }

    [Fact]
    public async Task SkipsUnconfiguredAndIncapableProviders()
    {
        var unconfigured = new Fake("unconf", automated: false, handles: _ => true, result: true);
        var wrongKind    = new Fake("wrong",  automated: true,  handles: k => k == "turnstile", result: true);
        var manual       = new Fake("manual", automated: false, handles: _ => true, result: true);

        var ok = await Router(manual, unconfigured, wrongKind).SolveAsync(null!, "recaptcha", TimeSpan.FromSeconds(1));

        Assert.True(ok); // only manual could take it
        Assert.Equal(0, unconfigured.SolveCalls);
        Assert.Equal(0, wrongKind.SolveCalls);
        Assert.Equal(1, manual.SolveCalls);
    }

    [Fact]
    public async Task FallsBackToManual_WhenNoAutomated()
    {
        var manual = new Fake("manual", automated: false, handles: _ => true, result: true);
        var ok = await Router(manual).SolveAsync(null!, "recaptcha", TimeSpan.FromSeconds(1));
        Assert.True(ok);
        Assert.Equal(1, manual.SolveCalls);
    }

    [Fact]
    public async Task FallsBackToManual_WhenAllAutomatedFail()
    {
        var a = new Fake("a", automated: true, handles: _ => true, result: false);
        var manual = new Fake("manual", automated: false, handles: _ => true, result: true);
        var ok = await Router(manual, a).SolveAsync(null!, "recaptcha", TimeSpan.FromSeconds(1));
        Assert.True(ok);
        Assert.Equal(1, a.SolveCalls);
        Assert.Equal(1, manual.SolveCalls);
    }

    [Theory]
    [InlineData("sorry")]
    [InlineData("cloudflare")]
    public async Task RefusesNetworkBlockInterstitials(string kind)
    {
        var a = new Fake("a", automated: true, handles: _ => true, result: true);
        var manual = new Fake("manual", automated: false, handles: _ => true, result: true);
        var ok = await Router(manual, a).SolveAsync(null!, kind, TimeSpan.FromSeconds(1));
        Assert.False(ok);
        Assert.Equal(0, a.SolveCalls);
        Assert.Equal(0, manual.SolveCalls);
    }

    [Fact]
    public async Task RecordsPerProviderMetrics()
    {
        var a = new Fake("a", automated: true, handles: _ => true, result: false);
        var manual = new Fake("manual", automated: false, handles: _ => true, result: true);
        var router = Router(manual, a);
        await router.SolveAsync(null!, "recaptcha", TimeSpan.FromSeconds(1));

        var m = router.Metrics;
        Assert.Equal((0, 1), m["a"]);       // failed once
        Assert.Equal((1, 0), m["manual"]);  // succeeded once
    }

    private sealed class Fake : ICaptchaSolver
    {
        private readonly bool _automated;
        private readonly Func<string, bool> _handles;
        private readonly bool _result;
        public Fake(string name, bool automated, Func<string, bool> handles, bool result)
        { ProviderName = name; _automated = automated; _handles = handles; _result = result; }

        public string ProviderName { get; }
        public int SolveCalls;
        public bool IsAutomated => _automated;
        public bool CanHandle(string kind) => _handles(kind);
        public Task<string?> DetectAsync(IBrowserSession session, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<bool> SolveAsync(IBrowserSession session, string kind, TimeSpan timeout, CancellationToken ct = default)
        { SolveCalls++; return Task.FromResult(_result); }
    }
}
