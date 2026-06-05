// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GhostShell.Core.Models;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>Outcome of an App-Bound CDP cookie recovery attempt.</summary>
public sealed record CdpExtractOutcome(IReadOnlyList<CookieEntry>? Cookies, string? Reason);

/// <summary>
/// App-Bound-Encryption (Chrome v127+) cookie recovery via CDP.
///
/// Since Chrome 127, cookie values carry a "v20" prefix and are encrypted with
/// a key wrapped by the browser's Elevation Service. DPAPI alone cannot unwrap
/// it. Critically, the App-Bound key is bound to the profile's ORIGINAL
/// user-data-dir path — copying the profile elsewhere and pointing the browser
/// at the copy yields zero readable cookies (verified). So the only reliable
/// recovery is to let the browser decrypt its OWN profile in place: launch the
/// source browser's real binary headless against its ORIGINAL user-data-dir with
/// <c>--remote-debugging-port</c> and read fully-decrypted cookies via the CDP
/// <c>Storage.getCookies</c> command.
///
/// Constraint: the source browser must NOT be running on that profile (its
/// process singleton would forward our launch to the live instance, and we must
/// not disturb the user's live profile). When it IS running we return a clear,
/// actionable reason so the UI can tell the user to close it. Fail-safe: any
/// failure returns null cookies + a reason; the caller keeps its DPAPI result.
/// </summary>
public sealed class CdpCookieExtractor
{
    private readonly ILogger _log;
    public CdpCookieExtractor(ILogger log) => _log = log;

    public async Task<CdpExtractOutcome> TryExtractAsync(
        string brandLabel, string userDataPath, string profileFolder,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return new(null, "App-Bound recovery is Windows-only.");

        var binary = FindBrowserBinary(brandLabel);
        if (binary is null)
            return new(null, $"Couldn't locate the {brandLabel} executable to decrypt App-Bound cookies.");

        if (IsBrowserRunning(binary))
            return new(null,
                $"{brandLabel} is running. App-Bound (v127+) cookies can only be decrypted by the browser " +
                $"itself on its own profile — close all {brandLabel} windows and re-run the import.");

        var profileDir = Path.Combine(userDataPath, profileFolder);
        if (!Directory.Exists(profileDir))
            return new(null, $"Profile folder not found: {profileDir}");

        Process? proc = null;
        try
        {
            // Launch the real browser headless against its OWN user-data-dir so
            // the Elevation Service decrypts the App-Bound key in place. A real
            // (hidden) window is opened so the cookie store fully initialises.
            var psi = new ProcessStartInfo
            {
                FileName = binary,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[]
            {
                "--headless=new", "--remote-debugging-port=0",
                $"--user-data-dir={userDataPath}", $"--profile-directory={profileFolder}",
                "--no-first-run", "--no-default-browser-check", "--restore-last-session=false",
                "--disable-sync", "--disable-background-networking", "--disable-component-update",
                "--disable-extensions", "--disable-default-apps", "about:blank",
            })
                psi.ArgumentList.Add(a);
            proc = Process.Start(psi);
            if (proc is null) return new(null, "Failed to start the source browser headless.");

            var port = await ReadDevToolsPortAsync(userDataPath, ct);
            if (port <= 0)
                return new(null, "Source browser didn't expose a debugging port (it may have forwarded to a running instance).");

            var wsUrl = await GetBrowserWsUrlAsync(port, ct);
            if (wsUrl is null) return new(null, "Couldn't reach the source browser's DevTools endpoint.");

            var cookies = await GetCookiesViaCdpAsync(wsUrl, ct);
            if (cookies is null) return new(null, "CDP Storage.getCookies returned no result.");

            _log.LogInformation(
                "CDP cookie recovery: pulled {N} cookie(s) from {Brand} (in-place, App-Bound decrypted)",
                cookies.Count, brandLabel);
            return new(cookies, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "CDP cookie recovery failed for {Brand}", brandLabel);
            return new(null, $"App-Bound recovery error: {ex.Message}");
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            try { proc?.Dispose(); } catch { }
            // Clear the stale SingletonLock our headless run may leave behind so
            // the user's next normal launch isn't slowed (Chrome would clear it
            // anyway, but be tidy).
            foreach (var lck in new[] { "SingletonLock", "SingletonCookie", "SingletonSocket" })
            {
                try { var f = Path.Combine(userDataPath, lck); if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }
    }

    /// <summary>True if a process running from exactly <paramref name="binary"/>
    /// is alive (so our launch would forward to it / clash with the live
    /// profile). Matches on the module path, not just the name, so GhostShell's
    /// own patched "chrome" never counts as the user's Google Chrome.</summary>
    private static bool IsBrowserRunning(string binary)
    {
        var procName = Path.GetFileNameWithoutExtension(binary);
        foreach (var p in Process.GetProcessesByName(procName))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, binary, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* access denied → can't confirm; ignore this PID */ }
            finally { p.Dispose(); }
        }
        return false;
    }

    private async Task<int> ReadDevToolsPortAsync(string userDataDir, CancellationToken ct)
    {
        var file = Path.Combine(userDataDir, "DevToolsActivePort");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(file))
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var rdr = new StreamReader(fs);
                    var line = await rdr.ReadLineAsync(ct);
                    if (int.TryParse(line?.Trim(), out var port) && port > 0) return port;
                }
            }
            catch { /* not ready */ }
            await Task.Delay(200, ct);
        }
        return 0;
    }

    private async Task<string?> GetBrowserWsUrlAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var ws)
                    && ws.ValueKind == JsonValueKind.String)
                    return ws.GetString();
            }
            catch { /* not up yet */ }
            await Task.Delay(250, ct);
        }
        return null;
    }

    private async Task<IReadOnlyList<CookieEntry>?> GetCookiesViaCdpAsync(string wsUrl, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(8));
            await ws.ConnectAsync(new Uri(wsUrl), connectCts.Token);
        }

        var req = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Storage.getCookies\"}");
        await ws.SendAsync(req, WebSocketMessageType.Text, true, ct);

        var buf = new byte[64 * 1024];
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                recvCts.CancelAfter(TimeSpan.FromSeconds(10));
                res = await ws.ReceiveAsync(buf, recvCts.Token);
                if (res.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);

            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idEl) || idEl.GetInt32() != 1) continue;
            if (!root.TryGetProperty("result", out var result)
                || !result.TryGetProperty("cookies", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return null;

            var outList = new List<CookieEntry>(arr.GetArrayLength());
            foreach (var c in arr.EnumerateArray())
            {
                string S(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                bool B(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
                long? expires = null;
                if (c.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.Number)
                {
                    var d = e.GetDouble();
                    if (d > 0) expires = (long)d;
                }
                var name = S("name");
                var domain = S("domain");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(domain)) continue;
                outList.Add(new CookieEntry
                {
                    Name = name,
                    Value = S("value"),
                    Domain = domain,
                    Path = string.IsNullOrEmpty(S("path")) ? "/" : S("path"),
                    Secure = B("secure"),
                    HttpOnly = B("httpOnly"),
                    SameSite = c.TryGetProperty("sameSite", out var ss) && ss.ValueKind == JsonValueKind.String
                        ? ss.GetString() : null,
                    ExpiresUnixSec = expires,
                });
            }
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
            return outList;
        }
        return null;
    }

    /// <summary>Locate the installed binary for a discovered browser brand.</summary>
    public static string? FindBrowserBinary(string brandLabel)
    {
        string? PF  = Environment.GetEnvironmentVariable("ProgramFiles");
        string? PFx = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        string? LAD = Environment.GetEnvironmentVariable("LOCALAPPDATA");

        IEnumerable<string?> Candidates() => brandLabel switch
        {
            "Google Chrome" => new[]
            {
                Join(PF,  @"Google\Chrome\Application\chrome.exe"),
                Join(PFx, @"Google\Chrome\Application\chrome.exe"),
                Join(LAD, @"Google\Chrome\Application\chrome.exe"),
            },
            "Microsoft Edge" => new[]
            {
                Join(PFx, @"Microsoft\Edge\Application\msedge.exe"),
                Join(PF,  @"Microsoft\Edge\Application\msedge.exe"),
            },
            "Brave Browser" => new[]
            {
                Join(PF,  @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                Join(PFx, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                Join(LAD, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            },
            "Chromium" => new[] { Join(LAD, @"Chromium\Application\chrome.exe") },
            "Vivaldi"  => new[] { Join(LAD, @"Vivaldi\Application\vivaldi.exe"),
                                  Join(PF,  @"Vivaldi\Application\vivaldi.exe") },
            "Yandex"   => new[] { Join(LAD, @"Yandex\YandexBrowser\Application\browser.exe") },
            _ => Array.Empty<string?>(),
        };

        return Candidates().FirstOrDefault(p => p is not null && File.Exists(p));
    }

    private static string? Join(string? root, string tail)
        => string.IsNullOrEmpty(root) ? null : Path.Combine(root, tail);
}
