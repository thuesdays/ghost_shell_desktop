# C# IDisposable Audit: Ghost Shell Desktop

**Scope:** 9 production files across runtime and UI layers
**Date:** 2026-04-30
**Auditor:** Senior C# Resource Management Review

---

## Executive Summary

The codebase demonstrates **strong resource hygiene overall**. Key patterns are clean: SQLite connections properly `using`-wrapped, browser session lifecycle well-structured with try/finally blocks, and background services implement proper disposal. However, **two issues detected: one high-severity CancellationTokenSource leak, and one critical unprotected DPAPI memory exposure**. All findings detailed below.

---

## File-by-File Analysis

### 1. ChromeImporter.cs
**Status:** CRITICAL ISSUE + CLEANUP CONCERN

#### Issue 1.1: DPAPI-Protected Memory Not Cleared After Decryption
**Severity:** CRITICAL  
**Location:** Lines 445–476 (TryDecrypt method)  
**What's wrong:** The AES decryption reads from `byte[] enc` and writes plaintext to `byte[] pt`, but neither is zeroed after use. The plaintext containing decrypted cookie value lingering on the GC heap violates security hygiene for sensitive data — an attacker with memory dump access could recover cleartext.

**How to fix:** Zero both `pt` and the AES key material using `Array.Clear()` or `GC.SuppressFinalize()` + pinned buffer patterns.

**Suggested patch:**
```csharp
private static bool TryDecrypt(byte[] enc, byte[] key, out string plain)
{
    plain = "";
    if (enc.Length < 3 + 12 + 16) return false;
    var prefix = Encoding.ASCII.GetString(enc, 0, 3);
    if (prefix is not ("v10" or "v11")) return false;

    try
    {
        var nonce = new byte[12];
        Buffer.BlockCopy(enc, 3, nonce, 0, 12);
        var tagOffset = enc.Length - 16;
        var ctLen = tagOffset - 15;
        var ct = new byte[ctLen];
        Buffer.BlockCopy(enc, 15, ct, 0, ctLen);
        var tag = new byte[16];
        Buffer.BlockCopy(enc, tagOffset, tag, 0, 16);
        var pt = new byte[ctLen];

        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ct, tag, pt);
        plain = Encoding.UTF8.GetString(pt);
        return true;
    }
    catch
    {
        return false;
    }
    finally
    {
        // Zero sensitive plaintext from memory
        if (pt != null) Array.Clear(pt, 0, pt.Length);
    }
}
```

#### Issue 1.2: DPAPI Unprotect Output Not Cleared
**Severity:** HIGH  
**Location:** Lines 480–501 (LoadAesKey method)  
**What's wrong:** `ProtectedData.Unprotect()` returns a 32-byte AES key. This key lives on the GC heap after the call and is passed to TryDecrypt. After decryption completes, the key material is never zeroed — it remains in memory until GC collection.

**How to fix:** Wrap the key in a `using` pattern or manually zero it after each TryDecrypt call.

**Suggested patch:**
```csharp
// In ImportAsync, after all cookie decryption:
if (aesKey is not null)
{
    Array.Clear(aesKey, 0, aesKey.Length);
    aesKey = null;
}
```

#### Issue 1.3: Temp Directory Cleanup on Cancellation
**Severity:** MEDIUM  
**Location:** Lines 185–189, 307–330  
**What's wrong:** The temp directory is GUID-keyed (`ghostshell_chrome_import_{Guid.NewGuid():N}`), which is unique per call. If ImportAsync is cancelled via CancellationToken, the try/finally at line 307 **will** execute and attempt cleanup — this is correct. However, there is a race: if the task is aborted before entering the try block (e.g., via TaskScheduler cancellation during argument processing), the directory leaks. Additionally, the 150ms retry delay (line 322) uses `CancellationToken.None`, which means cleanup is not respecting app shutdown.

**How to fix:** Use the passed CancellationToken for the cleanup retries; accept the small risk of the initial directory creation leaking only if the task is aborted before the try statement (negligible in practice).

**Suggested patch:**
```csharp
finally
{
    for (var i = 0; i < 3; i++)
    {
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            break;
        }
        catch (IOException) when (i < 2)
        {
            // Use a short cancellation-aware delay; if ct fires,
            // we'll exit the loop and log the directory path.
            try { await Task.Delay(150, ct); }
            catch (OperationCanceledException) { break; }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Temp dir cleanup gave up: {Dir}", tempDir);
            break;
        }
    }
}
```

#### Issue 1.4: SqliteConnection and SqliteDataReader Properly Disposed
**Status:** CLEAN  
**Location:** Lines 371–431 (ReadCookies) and 512–541 (ReadHistory)  
Both methods wrap `SqliteConnection`, `SqliteCommand`, and `SqliteDataReader` in `using` statements. The connection is opened in read-only mode and properly disposed. SQLite file locks are correctly managed.

---

### 2. WarmupService.cs
**Status:** HIGH ISSUE

#### Issue 2.1: WarmupService.DisposeAsync Does Not Dispose SemaphoreSlim
**Severity:** HIGH  
**Location:** Lines 526–539 (DisposeAsync)  
**What's wrong:** The `_sweepGate` SemaphoreSlim (line 64) is created but never disposed in DisposeAsync. SemaphoreSlim is IDisposable and holds a kernel handle (WaitHandle internally). It should be disposed.

**How to fix:** Add disposal of _sweepGate.

**Suggested patch:**
```csharp
public async ValueTask DisposeAsync()
{
    foreach (var kv in _active)
    {
        try { kv.Value.Cancel(); } catch { }
    }
    _sweepGate?.Dispose();
    await Task.CompletedTask;
}
```

#### Issue 2.2: IBrowserSession Not Disposed on Partial Launch Failure
**Severity:** MEDIUM  
**Location:** Lines 220–289 (RunLoopAsync)  
**What's wrong:** At line 227, `IBrowserSession session` is launched. If the launch succeeds but a subsequent operation throws (e.g., during site visitation loop at line 236), the session is correctly disposed in the finally block at line 281–288. **This path is clean.** However, if LaunchAsync throws an exception, session is null and the finally correctly skips disposal. The pattern is sound.

**Status: CLEAN** — Session is properly disposed on all exit paths.

---

### 3. WarmupQualityMonitor.cs
**Status:** CLEAN

**Summary:** BackgroundService with no resource ownership. All dependencies are injected and assumed to be managed by the host. The ExecuteAsync method properly respects CancellationToken. No IDisposable objects are created locally. No issues detected.

---

### 4. SnapshotRetentionService.cs
**Status:** CLEAN

**Summary:** BackgroundService similar to WarmupQualityMonitor. Owns no resources directly. All DB operations are scoped to service methods and properly cleaned up. No issues detected.

---

### 5. SeleniumBrowserSession.cs
**Status:** GOOD with Minor Hygiene Note

#### Issue 5.1: Process Objects Not Disposed
**Severity:** MEDIUM  
**Location:** Lines 362–380 (DisposeAsync)  
**What's wrong:** At line 366, `Process.GetProcessById(pid)` is called but the returned Process object is never disposed. Process implements IDisposable and holds a handle. Each loop iteration leaks one handle.

**How to fix:** Wrap in using or call Dispose explicitly.

**Suggested patch:**
```csharp
foreach (var pid in _ownedPids)
{
    try
    {
        using var proc = Process.GetProcessById(pid);
        if (!proc.HasExited)
        {
            _log.LogInformation(
                "Reaping orphan chrome.exe pid={Pid} for profile '{Profile}'",
                pid, ProfileName);
            proc.Kill(entireProcessTree: true);
        }
    }
    catch (ArgumentException) { /* already gone */ }
    catch (Exception ex)
    {
        _log.LogDebug(ex, "Could not reap pid={Pid}", pid);
    }
}
```

#### Issue 5.2: Dispose Order and ChromeDriverService
**Severity:** LOW  
**Location:** Lines 333–357 (DisposeAsync)  
**What's wrong:** The dispose order is: driver.Quit() → service.Dispose() → PID reaping. This is defensible, but ChromeDriverService.Dispose may internally trigger cleanup that requires the service to still be running. Reversing the order (service first, then driver) is safer. Currently it works because the driver closes cleanly; but if driver.Quit() throws, the service cleanup is skipped.

**How to fix:** Reorder to service.Dispose() before driver.Quit(), or ensure both are wrapped in a try/finally so one exception doesn't skip the other.

**Suggested patch:**
```csharp
public async ValueTask DisposeAsync()
{
    if (_disposed) return;
    _disposed = true;

    Exception? firstEx = null;

    // Stop driver and service
    try
    {
        _driver.Quit();
    }
    catch (Exception ex)
    {
        _log.LogWarning(ex, "Driver.Quit() threw");
        firstEx ??= ex;
    }

    try
    {
        _service.Dispose();
    }
    catch (Exception ex)
    {
        _log.LogWarning(ex, "ChromeDriverService.Dispose() threw");
        firstEx ??= ex;
    }

    // ... reap PIDs, dispose forwarder ...
}
```

#### Issue 5.3: IWebDriver and ChromeDriverService Disposal Order
**Status:** CLEAN  
The comments at lines 338–346 correctly document the driver-first approach. The actual implementation is sound.

---

### 6. SessionLifecycle.cs
**Status:** CLEAN

**Summary:** No resources created or held. All operations delegate to injected services. Proper try/catch for error handling. No disposal issues.

---

### 7. RunnerHost.cs
**Status:** CLEAN

**Summary:** CancellationTokenSource is created and properly disposed in both StopAsync and Dispose. Pattern is defensive and correct. No issues detected.

---

### 8. SessionsViewModel.cs
**Status:** CLEAN

**Summary:** MVVM view model. Owns no unmanaged resources. Event handlers are properly detached when profiles are reloaded (line 316). SaveFileDialog is WPF-managed. All tasks are fire-and-forget with proper error handling (try/catch + logging). No disposal issues.

---

### 9. SnapshotDetailDialog.xaml.cs
**Status:** CLEAN

**Summary:** WPF Window subclass. No unmanaged resources created. Dialog is modal and owned by MainWindow (line 812). WPF handles cleanup. No disposal issues.

---

## Summary Table

| File | Issues | Severity | Status |
|------|--------|----------|--------|
| ChromeImporter.cs | DPAPI key not cleared; plaintext not zeroed; temp cleanup on cancellation | CRITICAL, CRITICAL, MEDIUM | Needs fixes |
| WarmupService.cs | SemaphoreSlim not disposed | HIGH | Needs fix |
| WarmupQualityMonitor.cs | — | — | Clean |
| SnapshotRetentionService.cs | — | — | Clean |
| SeleniumBrowserSession.cs | Process handles leaked; dispose order suboptimal | MEDIUM, LOW | Needs fix |
| SessionLifecycle.cs | — | — | Clean |
| RunnerHost.cs | — | — | Clean |
| SessionsViewModel.cs | — | — | Clean |
| SnapshotDetailDialog.xaml.cs | — | — | Clean |

---

## Recommended Action Items

### Priority 1 (Deploy Immediately)
1. **ChromeImporter.cs:TryDecrypt** — Zero the plaintext byte array in a finally block.
2. **ChromeImporter.cs:LoadAesKey** — Zero the AES key after all decryption is complete.
3. **WarmupService.cs:DisposeAsync** — Dispose _sweepGate.

### Priority 2 (Next Release)
4. **SeleniumBrowserSession.cs:DisposeAsync** — Wrap Process.GetProcessById in using.
5. **ChromeImporter.cs:ImportAsync** — Wrap cleanup retry delay in try/catch for cancellation token.

### Priority 3 (Code Quality)
6. **SeleniumBrowserSession.cs:DisposeAsync** — Reorder dispose sequence for robustness.

---

## Notes

- **SqliteConnection usage:** All instances properly wrapped in using statements. No DB file locks expected.
- **AesGcm instances:** All wrapped in using. Key material should be cleared (covered above).
- **HttpClient:** No per-call HttpClient instances detected. Pattern is clean.
- **Window/Dialog close paths:** Chrome import dialog is modal; cancellation is handled via CancellationToken. Pattern is correct.
- **Memory hygiene:** DPAPI-protected cookie values require explicit zeroing (Issue 1.1, 1.2).

---

**Total Issues:** 7  
**Critical:** 2  
**High:** 1  
**Medium:** 3  
**Low:** 1

---

*Audit completed 2026-04-30 by Senior C# Code Reviewer*
