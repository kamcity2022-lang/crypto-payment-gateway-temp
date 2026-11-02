using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.Contracts;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Gateway.Persistence;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gateway.Blockchain.Tron;

public class TronScannerService : ITronScannerService
{
    private readonly GatewayDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TronScannerService> _logger;
    private readonly string _usdtContractAddress;

    public TronScannerService(
        GatewayDbContext dbContext,
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<TronScannerService> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _usdtContractAddress = configuration["Tron:UsdtContractAddress"] 
                               ?? throw new InvalidOperationException("Configuration 'Tron:UsdtContractAddress' is required.");
    }

    public async Task ScanPendingDepositsAsync(CancellationToken cancellationToken)
    {
        var solidifiedBlockNumber = await GetSolidifiedBlockNumberAsync(cancellationToken);
        if (solidifiedBlockNumber == 0)
        {
            _logger.LogWarning("SolidityNode head block height could not be determined. Aborting scan cycle safely.");
            return;
        }

        var now = DateTime.UtcNow;
        var lateDays = int.TryParse(_configuration["Tron:Scanner:LatePaymentRetentionDays"], out var l) ? l : 30;
        var overDays = int.TryParse(_configuration["Tron:Scanner:OverpaymentRetentionDays"], out var o) ? o : 7;
        var latePaymentRetention = now.AddDays(-lateDays);
        var overpaymentRetention = now.AddDays(-overDays);

        var invoices = await _dbContext.Invoices
            .Include(i => i.Transfers)
            .Where(i => i.Status == InvoiceStatus.Pending ||
                        i.Status == InvoiceStatus.PartiallyPaid ||
                        i.Status == InvoiceStatus.Underpaid ||
                        (i.Status == InvoiceStatus.Expired && i.ExpiresAtUtc >= latePaymentRetention) ||
                        (i.Status == InvoiceStatus.Paid && i.PaidAtUtc.HasValue && i.PaidAtUtc.Value >= overpaymentRetention))
            .ToListAsync(cancellationToken);

        foreach (var invoice in invoices)
            await ScanInvoiceWithPaginationAsync(invoice, solidifiedBlockNumber, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ScanInvoiceWithPaginationAsync(Invoice invoice, ulong solidifiedBlockNumber, CancellationToken ct)
    {
        var cursor = await _dbContext.BlockchainScanCursors
            .FirstOrDefaultAsync(c => c.Network == "TRON" && c.Address == invoice.DepositAddress, ct);

        if (cursor == null)
        {
            cursor = new BlockchainScanCursor("TRON", invoice.DepositAddress, 0, 0);
            _dbContext.BlockchainScanCursors.Add(cursor);
        }

        string? fingerprint = cursor.LastFingerprint;
        bool hasMorePages = true;

        while (hasMorePages)
        {
            var url = $"https://api.trongrid.io/v1/accounts/{invoice.DepositAddress}/transactions/trc20?contract_address={_usdtContractAddress}&limit=20";
            if (!string.IsNullOrEmpty(fingerprint)) url += $"&fingerprint={Uri.EscapeDataString(fingerprint)}";
            else if (cursor.LastProcessedBlockTimestamp > 0) url += $"&min_timestamp={cursor.LastProcessedBlockTimestamp}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.GetArrayLength() == 0)
            {
                cursor.SetFingerprint(null);
                break;
            }

            foreach (var item in dataElement.EnumerateArray())
            {
                var toAddress = item.GetProperty("to").GetString() ?? string.Empty;
                if (!string.Equals(toAddress, invoice.DepositAddress, StringComparison.OrdinalIgnoreCase)) continue;

                var txHash = item.GetProperty("transaction_id").GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(txHash)) continue;

                if (!item.TryGetProperty("event_index", out var idxEl) || !idxEl.TryGetInt32(out var eventIndex)) continue;

                var rawValueStr = item.GetProperty("value").GetString() ?? "0";
                if (!long.TryParse(rawValueStr, out var amountUnits) || amountUnits <= 0) continue;

                var blockTimestamp = item.GetProperty("block_timestamp").GetInt64();

                var txDetail = await VerifySolidifiedTransactionAsync(txHash, invoice.DepositAddress, amountUnits, eventIndex, ct);
                if (!txDetail.IsSolidified || !txDetail.IsSuccess) continue;

                var existingTransfer = await _dbContext.PaymentTransfers
                    .FirstOrDefaultAsync(t => t.Network == "TRON" && t.TransactionHash == txHash && t.TransferIndex == eventIndex, ct);

                if (existingTransfer == null)
                {
                    var fromAddress = item.GetProperty("from").GetString() ?? string.Empty;
                    var transfer = new PaymentTransfer(invoice.Id, "TRON", txHash, eventIndex, _usdtContractAddress, fromAddress, toAddress, amountUnits, txDetail.BlockNumber);
                    _dbContext.PaymentTransfers.Add(transfer);
                    
                    if (invoice.RegisterTransfer(transfer))
                    {
                        transfer.UpdateConfirmations(solidifiedBlockNumber, 1);
                        var previousStatus = invoice.Status;
                        var currentStatus = invoice.EvaluateStatus();

                        if (previousStatus != currentStatus)
                        {
                            var merchant = await _dbContext.Merchants.FindAsync(new object[] { invoice.MerchantId }, ct);
                            if (merchant != null)
                            {
                                var payload = JsonSerializer.Serialize(new {
                                    @event = $"invoice.{currentStatus.ToString().ToLowerInvariant()}",
                                    invoiceId = invoice.Id, status = currentStatus.ToString(),
                                    paidUnits = invoice.PaidAmountUnits, expectedUnits = invoice.ExpectedAmountUnits, txHash = txHash
                                });
                                _dbContext.OutboxEvents.Add(new OutboxEvent(invoice.Id, $"invoice.{currentStatus.ToString().ToLowerInvariant()}", payload, invoice.CallbackUrl, merchant.EncryptedWebhookSecret));
                            }
                        }
                    }
                }
                else
                {
                    existingTransfer.UpdateConfirmations(solidifiedBlockNumber, 1);
                    invoice.EvaluateStatus();
                }
                cursor.UpdateCursor(blockTimestamp, txDetail.BlockNumber);
            }

            if (doc.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("fingerprint", out var fpProp) && !string.IsNullOrWhiteSpace(fpProp.GetString()))
            {
                fingerprint = fpProp.GetString();
                cursor.SetFingerprint(fingerprint);
            }
            else
            {
                hasMorePages = false;
                cursor.SetFingerprint(null);
            }
        }
    }

    private async Task<ulong> GetSolidifiedBlockNumberAsync(CancellationToken ct)
    {
        try
        {
            var res = await _httpClient.GetAsync("https://api.trongrid.io/walletsolidity/getnowblock", ct);
            if (!res.IsSuccessStatusCode) return 0;
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("block_header", out var header) && header.TryGetProperty("raw_data", out var raw) && raw.TryGetProperty("number", out var num))
                return (ulong)num.GetInt64();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to query solidified head block."); }
        return 0;
    }

    public async Task<(bool IsSolidified, bool IsSuccess, ulong BlockNumber)> VerifySolidifiedTransactionAsync(
        string txHash, string expectedToAddress, long expectedAmountUnits, int expectedEventIndex, CancellationToken ct)
    {
        try
        {
            var jsonPayload = JsonSerializer.Serialize(new { value = txHash });
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var res = await _httpClient.PostAsync("https://api.trongrid.io/walletsolidity/gettransactioninfobyid", content, ct);
            if (!res.IsSuccessStatusCode) return (false, false, 0);

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("blockNumber", out var bNum)) return (false, false, 0);
            ulong blockNumber = (ulong)bNum.GetInt64();
            if (blockNumber == 0) return (false, false, 0);

            if (!doc.RootElement.TryGetProperty("receipt", out var receipt) || !receipt.TryGetProperty("result", out var resultStr) || !string.Equals(resultStr.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
                return (true, false, blockNumber);

            if (!doc.RootElement.TryGetProperty("log", out var logs) || logs.GetArrayLength() == 0) return (true, false, blockNumber);

            var expectedContractHex = TronAddressToHex(_usdtContractAddress);
            var expectedRecipientEvmHex = TronAddressToHex(expectedToAddress);
            int matchCount = 0;
            bool matched = false;

            foreach (var log in logs.EnumerateArray())
            {
                var contractAddress = log.TryGetProperty("address", out var cAddr) ? cAddr.GetString() : null;
                if (string.IsNullOrEmpty(contractAddress)) continue;

                var logContractHex = contractAddress.StartsWith("41") && contractAddress.Length == 42 ? contractAddress[2..] : (contractAddress.StartsWith("0x") && contractAddress.Length == 42 ? contractAddress[2..] : contractAddress);
                if (!string.Equals(logContractHex, expectedContractHex, StringComparison.OrdinalIgnoreCase)) continue;

                if (!log.TryGetProperty("topics", out var topics) || topics.GetArrayLength() < 3) continue;
                var signature = topics[0].GetString();
                if (!string.Equals(signature, "ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef", StringComparison.OrdinalIgnoreCase)) continue;

                var recipientTopicHex = topics[2].GetString() ?? string.Empty;
                if (recipientTopicHex.Length < 40) continue;
                var recipientEvmAddress = recipientTopicHex[^40..];
                if (!string.Equals(recipientEvmAddress, expectedRecipientEvmHex, StringComparison.OrdinalIgnoreCase)) continue;

                if (!log.TryGetProperty("data", out var dataProp)) continue;
                var dataHex = dataProp.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dataHex)) continue;
                if (dataHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) dataHex = dataHex[2..];

                if (!System.Numerics.BigInteger.TryParse(dataHex, System.Globalization.NumberStyles.HexNumber, null, out var parsedBigInt)) continue;
                if (parsedBigInt < 0 || parsedBigInt > long.MaxValue || (long)parsedBigInt != expectedAmountUnits) continue;

                matchCount++;
                if (matchCount > 1) return (true, false, blockNumber); // Ambiguous matches rejected
                matched = true;
            }
            return (true, matched, blockNumber);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error verifying tx {TxHash}", txHash); return (false, false, 0); }
    }

    private static string TronAddressToHex(string base58Address)
    {
        var bytes = Base58CheckDecode(base58Address);
        return Convert.ToHexString(bytes.AsSpan(1)).ToLowerInvariant();
    }

    private static byte[] Base58CheckDecode(string base58)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var intData = System.Numerics.BigInteger.Zero;
        for (var i = 0; i < base58.Length; i++)
        {
            var index = alphabet.IndexOf(base58[i]);
            if (index < 0) throw new FormatException();
            intData = intData * 58 + index;
        }
        var bytes = intData.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = 0;
        for (var i = 0; i < base58.Length && base58[i] == '1'; i++) leadingZeros++;
        var result = new byte[leadingZeros + bytes.Length];
        Buffer.BlockCopy(bytes, 0, result, leadingZeros, bytes.Length);
        var payload = new byte[result.Length - 4];
        Buffer.BlockCopy(result, 0, payload, 0, payload.Length);
        return payload;
    }
}