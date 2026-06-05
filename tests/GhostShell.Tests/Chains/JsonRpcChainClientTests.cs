// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GhostShell.Core.Chains;
using GhostShell.Runtime.Chains;
using Xunit;

namespace GhostShell.Tests.Chains;

/// <summary>
/// Phase 57 — RPC client parsing, exercised against a fake transport so we pin
/// the EVM/Solana response shapes without a live network.
/// </summary>
public sealed class JsonRpcChainClientTests
{
    private static readonly ChainDescriptor Eth = CuratedChainCatalog.TryGet("ethereum")!;
    private static readonly ChainDescriptor Sol = CuratedChainCatalog.TryGet("solana")!;

    private static JsonRpcChainClient Client(Dictionary<string, string> resultByMethod, bool error = false)
        => new(new HttpClient(new FakeHandler(resultByMethod, error)));

    [Fact]
    public async Task Evm_Balance_ParsesHexWei()
    {
        var c = Client(new() { ["eth_getBalance"] = "\"0xde0b6b3a7640000\"" }); // 1 ETH
        var bal = await c.GetNativeBalanceAsync(Eth, "0xabc");
        Assert.Equal(BigInteger.Parse("1000000000000000000"), bal);
    }

    [Fact]
    public async Task Solana_Balance_ParsesValueLamports()
    {
        var c = Client(new() { ["getBalance"] = "{\"value\":50000000}" });
        var bal = await c.GetNativeBalanceAsync(Sol, "SoLaddr");
        Assert.Equal(new BigInteger(50_000_000), bal);
    }

    [Fact]
    public async Task Evm_GasPrice_And_TxCount()
    {
        var c = Client(new()
        {
            ["eth_gasPrice"] = "\"0x3b9aca00\"",          // 1 gwei
            ["eth_getTransactionCount"] = "\"0x5\"",
        });
        Assert.Equal(new BigInteger(1_000_000_000), await c.GetGasPriceWeiAsync(Eth));
        Assert.Equal(5, await c.GetTransactionCountAsync(Eth, "0xabc"));
    }

    [Fact]
    public async Task Evm_TxState_SuccessFailedPending()
    {
        Assert.Equal(TxState.Success, await Client(new() { ["eth_getTransactionReceipt"] = "{\"status\":\"0x1\"}" })
            .GetTransactionStateAsync(Eth, "0xhash"));
        Assert.Equal(TxState.Failed, await Client(new() { ["eth_getTransactionReceipt"] = "{\"status\":\"0x0\"}" })
            .GetTransactionStateAsync(Eth, "0xhash"));
        Assert.Equal(TxState.Pending, await Client(new() { ["eth_getTransactionReceipt"] = "null" })
            .GetTransactionStateAsync(Eth, "0xhash"));
    }

    [Fact]
    public async Task Solana_TxState()
    {
        Assert.Equal(TxState.Success, await Client(new()
            { ["getSignatureStatuses"] = "{\"value\":[{\"confirmationStatus\":\"finalized\",\"err\":null}]}" })
            .GetTransactionStateAsync(Sol, "sig"));
        Assert.Equal(TxState.Failed, await Client(new()
            { ["getSignatureStatuses"] = "{\"value\":[{\"confirmationStatus\":\"confirmed\",\"err\":{\"x\":1}}]}" })
            .GetTransactionStateAsync(Sol, "sig"));
        Assert.Equal(TxState.Pending, await Client(new()
            { ["getSignatureStatuses"] = "{\"value\":[null]}" })
            .GetTransactionStateAsync(Sol, "sig"));
    }

    [Fact]
    public async Task RpcError_Throws_OnBalance()
    {
        var c = Client(new(), error: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.GetNativeBalanceAsync(Eth, "0xabc"));
    }

    [Fact]
    public async Task Solana_GasPrice_IsZero_NotSupported()
        => Assert.Equal(BigInteger.Zero, await Client(new()).GetGasPriceWeiAsync(Sol));

    [Fact]
    public async Task Evm_Balance_NullResult_Throws_NotSilentZero()
    {
        // A node that returns JSON null must surface, not pretend balance is 0.
        var c = Client(new() { ["eth_getBalance"] = "null" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.GetNativeBalanceAsync(Eth, "0xabc"));
    }

    [Fact]
    public async Task Evm_TxCount_OutOfRange_Throws()
    {
        // Absurdly large nonce → refuse rather than clamp to a poisoned sentinel.
        var c = Client(new() { ["eth_getTransactionCount"] = "\"0xffffffffffffffffffffffff\"" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.GetTransactionCountAsync(Eth, "0xabc"));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _byMethod;
        private readonly bool _error;
        public FakeHandler(Dictionary<string, string> byMethod, bool error)
        {
            _byMethod = byMethod;
            _error = error;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var method = doc.RootElement.GetProperty("method").GetString() ?? "";

            string full = _error
                ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32000,\"message\":\"boom\"}}"
                : $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{(_byMethod.TryGetValue(method, out var r) ? r : "null")}}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(full, Encoding.UTF8, "application/json"),
            };
        }
    }
}
