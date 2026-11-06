using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gateway.Blockchain.Tron;

public class TronSweepService : ITronSweepService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TronSweepService> _logger;
    private readonly string _usdtContractAddress;
    private readonly TronNetworkEndpoints _network;

    public TronSweepService(HttpClient httpClient, IConfiguration configuration, ILogger<TronSweepService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _network = TronNetworkEndpoints.FromConfiguration(configuration);
        _usdtContractAddress = _network.TokenContract;
    }

    public async Task<string> CreateUnsignedSweepTransactionAsync(
        string sourceAddress,
        string destinationAddress,
        long amountUnits,
        CancellationToken ct)
    {
        // Parameter encoding for TRC-20 transfer(address _to, uint256 _value)
        // Function Selector: a9059cbb
        var cleanDest = destinationAddress.Trim();
        var destHex = TronAddressToHex(cleanDest);
        var parameter = destHex.PadLeft(64, '0') + amountUnits.ToString("x").PadLeft(64, '0');

        var payload = new
        {
            owner_address = sourceAddress,
            contract_address = _usdtContractAddress,
            function_selector = "transfer(address,uint256)",
            parameter = parameter,
            fee_limit = 30_000_000L, // 30 TRX max fee limit for energy consumption
            call_value = 0
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(_network.Endpoint("wallet/triggersmartcontract"), content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("TronGrid trigger contract call failed: {Error}", err);
            throw new InvalidOperationException($"Failed to trigger smart contract transaction: {response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("result", out var resultProp) ||
            !resultProp.TryGetProperty("result", out var isOk) ||
            !isOk.GetBoolean())
        {
            throw new InvalidOperationException("Smart contract call simulation failed on TRON node.");
        }

        if (!doc.RootElement.TryGetProperty("transaction", out var txProp))
        {
            throw new InvalidOperationException("Node did not return an unsigned transaction payload.");
        }

        return txProp.GetRawText();
    }

    public async Task<string> BroadcastSignedTransactionAsync(string signedTxJson, CancellationToken ct)
    {
        using var content = new StringContent(signedTxJson, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(_network.Endpoint("wallet/broadcasttransaction"), content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Transaction broadcast failed: {Error}", err);
            throw new InvalidOperationException($"Broadcast failed: {response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("result", out var res) && res.GetBoolean())
        {
            return doc.RootElement.GetProperty("txid").GetString() ?? string.Empty;
        }

        var message = doc.RootElement.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown broadcast rejection";
        throw new InvalidOperationException($"Transaction broadcast rejected: {message}");
    }

    private static string TronAddressToHex(string base58Address)
    {
        var bytes = Base58CheckDecode(base58Address);
        // Skip first byte (0x41) for standard EVM address ABI parameter
        return Convert.ToHexString(bytes.AsSpan(1)).ToLowerInvariant();
    }

    private static byte[] Base58CheckDecode(string base58)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var intData = System.Numerics.BigInteger.Zero;

        for (var i = 0; i < base58.Length; i++)
        {
            var index = alphabet.IndexOf(base58[i]);
            if (index < 0) throw new FormatException("Invalid Base58 character.");
            intData = intData * 58 + index;
        }

        var bytes = intData.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = 0;
        for (var i = 0; i < base58.Length && base58[i] == '1'; i++)
            leadingZeros++;

        var result = new byte[leadingZeros + bytes.Length];
        Buffer.BlockCopy(bytes, 0, result, leadingZeros, bytes.Length);

        // Exclude 4-byte checksum at the end
        var payload = new byte[result.Length - 4];
        Buffer.BlockCopy(result, 0, payload, 0, payload.Length);
        return payload;
    }
}