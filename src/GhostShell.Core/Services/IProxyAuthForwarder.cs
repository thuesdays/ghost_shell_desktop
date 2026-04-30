// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Services;

/// <summary>
/// Local TCP forwarder that fronts an HTTP(S) proxy requiring
/// <c>Proxy-Authorization</c>. Chromium's <c>--proxy-server</c>
/// flag silently dies when credentials are baked into the URL
/// (the browser process exits before <c>DevToolsActivePort</c>
/// is created), and the alternative — a credential-supplying
/// extension — doesn't survive the patched chromedriver pipeline
/// reliably.
///
/// Implementations open a listener on <c>127.0.0.1:0</c>, accept
/// unauthenticated CONNECT/HTTP requests from Chromium, inject
/// the <c>Proxy-Authorization: Basic …</c> header on the first
/// request, then bidirectionally forward TCP traffic to the real
/// upstream proxy.
///
/// One forwarder instance maps to ONE browser session. Spawn it
/// in <see cref="IBrowserLauncher"/>, point Chromium at the
/// returned local URL, and dispose alongside the session.
/// </summary>
public interface IProxyAuthForwarder : IAsyncDisposable
{
    /// <summary>
    /// Bind a local listener and start accepting connections.
    /// Returns the local proxy URL Chromium should be pointed at,
    /// e.g. <c>http://127.0.0.1:54321</c>.
    /// </summary>
    /// <param name="upstreamProxyUrl">
    /// Full upstream URL — supports <c>user:pass@host:port</c>,
    /// <c>http://user:pass@host:port</c>, etc. Scheme defaults to
    /// http when missing.
    /// </param>
    Task<string> StartAsync(string upstreamProxyUrl, CancellationToken ct = default);

    /// <summary>True between StartAsync and DisposeAsync.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Local URL the forwarder is listening on (same as StartAsync's
    /// return value). Null until the listener is bound.
    /// </summary>
    string? LocalUrl { get; }
}

/// <summary>
/// Factory contract — a fresh forwarder per launch. Each session
/// gets its own instance so disposing one session doesn't cut
/// the connection of another. Registered as singleton in DI;
/// callers ask for new sessions via <see cref="Create"/>.
/// </summary>
public interface IProxyAuthForwarderFactory
{
    IProxyAuthForwarder Create();
}
