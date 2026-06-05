// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GhostShell.Core.Chains;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Chains;

/// <summary>
/// Phase 57 — minimal JSON-RPC client for EVM + Solana reads. No third-party
/// web3 dependency: a single POST per call, parsed with System.Text.Json. Each
/// call is bounded by a 15 s timeout so a dead RPC can't hang a farm pre-flight.
/// Read-only — never signs, never sends keys.
/// </summary>
public sealed class JsonRpcChainClient : IChainRpcClient
{
    private readonly HttpClient _http;
    private readonly ILogger<JsonRpcChainClient>? _log;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    public JsonRpcChainClient(HttpClient http, ILogger<JsonRpcChainClient>? log = null)
    {
        _http = http;
        _log  = log;
    }

    public async Task<BigInteger> GetNativeBalanceAsync(ChainDescriptor chain, string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address)) return BigInteger.Zero;
        if (chain.Family == ChainFamily.Solana)
        {
            // { result: { value: <lamports number> } }
            var res = await RpcAsync(chain.RpcUrl, "getBalance", new object[] { address }, ct);
            if (res.ValueKind == JsonValueKind.Object && res.TryGetProperty("value", out var v) &&
                v.ValueKind == JsonValueKind.Number)
            {
                // Defensive parse: TryGetInt64 can reject otherwise-valid numbers
                // (exponent form, etc.). Don't silently report 0 — that would make
                // assert_balance fail "as if drained". Fall back to the raw text.
                if (v.TryGetInt64(out var lamports)) return new BigInteger(lamports);
                if (BigInteger.TryParse(v.GetRawText(), out var big)) return big;
            }
            throw new InvalidOperationException($"getBalance returned no usable value for {address} on {chain.Id}");
        }
        // EVM: result is a hex wei quantity. A JSON-null result means the node
        // couldn't answer — surface it (don't pretend the wallet has 0, which
        // would make assert_balance fail as if drained).
        var hex = await RpcAsync(chain.RpcUrl, "eth_getBalance", new object[] { address, "latest" }, ct);
        if (hex.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"eth_getBalance returned no value for {address} on {chain.Id}");
        return ChainUnits.ParseHexQuantity(hex.GetString());
    }

    public async Task<BigInteger> GetGasPriceWeiAsync(ChainDescriptor chain, CancellationToken ct = default)
    {
        if (chain.Family != ChainFamily.Evm) return BigInteger.Zero;
        var hex = await RpcAsync(chain.RpcUrl, "eth_gasPrice", System.Array.Empty<object>(), ct);
        if (hex.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"eth_gasPrice returned no value on {chain.Id}");
        return ChainUnits.ParseHexQuantity(hex.GetString());
    }

    public async Task<long> GetTransactionCountAsync(ChainDescriptor chain, string address, bool pending = true, CancellationToken ct = default)
    {
        if (chain.Family != ChainFamily.Evm || string.IsNullOrWhiteSpace(address)) return 0;
        var hex = await RpcAsync(chain.RpcUrl, "eth_getTransactionCount",
            new object[] { address, pending ? "pending" : "latest" }, ct);
        if (hex.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"eth_getTransactionCount returned no value for {address} on {chain.Id}");
        var big = ChainUnits.ParseHexQuantity(hex.GetString());
        // A nonce never legitimately exceeds long.MaxValue — a value that big is
        // garbage; refuse it rather than return a poisoned sentinel.
        if (big > long.MaxValue)
            throw new InvalidOperationException($"eth_getTransactionCount out of range for {address} on {chain.Id}");
        return (long)big;
    }

    public async Task<TxState> GetTransactionStateAsync(ChainDescriptor chain, string txHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(txHash)) return TxState.Unknown;
        try
        {
            if (chain.Family == ChainFamily.Solana)
            {
                // getSignatureStatuses([sig], {searchTransactionHistory:true})
                var res = await RpcAsync(chain.RpcUrl, "getSignatureStatuses",
                    new object[] { new[] { txHash }, new { searchTransactionHistory = true } }, ct);
                if (res.ValueKind == JsonValueKind.Object && res.TryGetProperty("value", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                {
                    var first = arr[0];
                    if (first.ValueKind == JsonValueKind.Null) return TxState.Pending;
                    if (first.TryGetProperty("err", out var err) && err.ValueKind != JsonValueKind.Null)
                        return TxState.Failed;
                    if (first.TryGetProperty("confirmationStatus", out var cs) && cs.ValueKind == JsonValueKind.String)
                    {
                        var s = cs.GetString();
                        return s is "confirmed" or "finalized" ? TxState.Success : TxState.Pending;
                    }
                    return TxState.Pending;
                }
                return TxState.Unknown;
            }

            // EVM: eth_getTransactionReceipt → null = pending; status 0x1/0x0.
            var receipt = await RpcAsync(chain.RpcUrl, "eth_getTransactionReceipt", new object[] { txHash }, ct);
            if (receipt.ValueKind == JsonValueKind.Null) return TxState.Pending;
            if (receipt.ValueKind == JsonValueKind.Object && receipt.TryGetProperty("status", out var st) &&
                st.ValueKind == JsonValueKind.String)
            {
                var hex = st.GetString();
                return ChainUnits.ParseHexQuantity(hex) == BigInteger.One ? TxState.Success : TxState.Failed;
            }
            return TxState.Pending;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "tx state probe failed for {Hash} on {Chain}", txHash, chain.Id);
            return TxState.Unknown;
        }
    }

    /// <summary>One JSON-RPC POST. Returns the <c>result</c> element (which may
    /// be a JSON null — callers distinguish null vs object). Throws on transport
    /// failure or an RPC <c>error</c> object.</summary>
    private async Task<JsonElement> RpcAsync(string rpcUrl, string method, object[] @params, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);

        var payload = new { jsonrpc = "2.0", id = 1, method, @params };
        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, rpcUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        // CRITICAL (audit C1): the per-call CancelAfter(15s) cancels `cts`, not the
        // caller's `ct`. A bare OperationCanceledException here would be re-thrown by
        // callers as if the USER pressed Stop, aborting the whole script run. Convert
        // an internal-timeout cancel into a TimeoutException so callers' fail-closed /
        // degrade-to-unknown paths run; only a real caller-cancel stays an OCE.
        HttpResponseMessage resp;
        System.IO.Stream stream;
        JsonDocument doc;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            resp.EnsureSuccessStatusCode();
            stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"RPC '{method}' on {rpcUrl} timed out after {CallTimeout.TotalSeconds:0}s");
        }
        using var _resp = resp;
        await using var _stream = stream;
        using var _doc = doc;
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            var msg = error.TryGetProperty("message", out var m) ? m.GetString() : error.GetRawText();
            throw new InvalidOperationException($"RPC error from {method}: {msg}");
        }
        if (root.TryGetProperty("result", out var result))
            return result.Clone();   // clone — doc is disposed at scope exit
        return default;              // ValueKind == Undefined
    }
}
