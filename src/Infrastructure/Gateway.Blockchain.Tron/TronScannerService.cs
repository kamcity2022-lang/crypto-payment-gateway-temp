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
    private readonly IConfiguration _configuration; // ۱. اضافه شدن فیلد
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
        _configuration = configuration; // ۲. مقداردهی فیلد
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

        // ۳. خواندن بازه‌های رصد از _configuration بدون نیاز به GetValue یا وابستگی خارجی
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
        {
            await ScanInvoiceWithPaginationAsync(invoice, solidifiedBlockNumber, cancellationToken);
        }

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
            if (!string.IsNullOrEmpty(fingerprint))
            {
                url += $"&fingerprint={Uri.EscapeDataString(fingerprint)}";
            }
            else if (cursor.LastProcessedBlockTimestamp > 0)
            {
                url += $"&min_timestamp={cursor.LastProcessedBlockTimestamp}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TronGrid query returned HTTP {Status} for address {Address}. Cursor not advanced.", response.StatusCode, invoice.DepositAddress);
                return; // Fail-closed: Stop pagination without advancing cursor
            }

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
                if (!string.Equals(toAddress, invoice.DepositAddress, StringComparison.OrdinalIgnoreCase))
                    continue;

                var txHash = item.GetProperty("transaction_id").GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(txHash)) continue;

                if (!item.TryGetProperty("event_index", out var idxEl) || !idxEl.TryGetInt32(out var eventIndex))
                {
                    _logger.LogError("Transfer event_index could not be determined for Tx {TxHash}. Skipping to prevent duplicate accounting.", txHash);
                    continue;
                }

                var rawValueStr = item.GetProperty("value").GetString() ?? "0";
                if (!long.TryParse(rawValueStr, out var amountUnits) || amountUnits <= 0) continue;

                var blockTimestamp = item.GetProperty("block_timestamp").GetInt64();

                // Fail-Closed Validation via SolidityNode
                var txDetail = await VerifySolidifiedTransactionAsync(
                    txHash, 
                    invoice.DepositAddress, 
                    amountUnits, 
                    eventIndex, 
                    ct);
                
                if (!txDetail.IsSolidified || !txDetail.IsSuccess)
                {
                    _logger.LogWarning("Tx {TxHash} rejected: IsSolidified={Solidified}, IsSuccess={Success}.", txHash, txDetail.IsSolidified, txDetail.IsSuccess);
                    continue;
                }

                var existingTransfer = await _dbContext.PaymentTransfers
                    .FirstOrDefaultAsync(t => t.Network == "TRON" && t.TransactionHash == txHash && t.TransferIndex == eventIndex, ct);

                if (existingTransfer == null)
                {
                    var fromAddress = item.GetProperty("from").GetString() ?? string.Empty;
                    var transfer = new PaymentTransfer(
                        invoice.Id,
                        "TRON",
                        txHash,
                        eventIndex,
                        _usdtContractAddress,
                        fromAddress,
                        toAddress,
                        amountUnits,
                        txDetail.BlockNumber);

                    _dbContext.PaymentTransfers.Add(transfer);
                    var registered = invoice.RegisterTransfer(transfer);

                    if (registered)
                    {
                        transfer.UpdateConfirmations(solidifiedBlockNumber, 1);
                        var previousStatus = invoice.Status;
                        var currentStatus = invoice.EvaluateStatus();

                        if (previousStatus != currentStatus)
                        {
                            var merchant = await _dbContext.Merchants.FindAsync(new object[] { invoice.MerchantId }, ct);
                            if (merchant != null)
                            {
                                var payload = JsonSerializer.Serialize(new
                                {
                                    @event = $"invoice.{currentStatus.ToString().ToLowerInvariant()}",
                                    invoiceId = invoice.Id,
                                    status = currentStatus.ToString(),
                                    paidUnits = invoice.PaidAmountUnits,
                                    expectedUnits = invoice.ExpectedAmountUnits,
                                    txHash = txHash
                                });

                                var outbox = new OutboxEvent(
                                    invoice.Id,
                                    $"invoice.{currentStatus.ToString().ToLowerInvariant()}",
                                    payload,
                                    invoice.CallbackUrl,
                                    merchant.EncryptedWebhookSecret);

                                _dbContext.OutboxEvents.Add(outbox);
                            }
                        }
                    }
                }
                else
                {
                    // Reconcile confirmations
                    existingTransfer.UpdateConfirmations(solidifiedBlockNumber, 1);
                    invoice.EvaluateStatus();
                }

                cursor.UpdateCursor(blockTimestamp, txDetail.BlockNumber);
            }

            if (doc.RootElement.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("fingerprint", out var fpProp) &&
                !string.IsNullOrWhiteSpace(fpProp.GetString()))
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
            if (doc.RootElement.TryGetProperty("block_header", out var header) &&
                header.TryGetProperty("raw_data", out var raw) &&
                raw.TryGetProperty("number", out var num))
            {
                return (ulong)num.GetInt64();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query solidified head block from TronGrid.");
        }
        return 0;
    }

    public async Task<(bool IsSolidified, bool IsSuccess, ulong BlockNumber)> VerifySolidifiedTransactionAsync(
        string txHash, 
        string expectedToAddress, 
        long expectedAmountUnits, 
        int expectedEventIndex,
        CancellationToken ct)
    {
        try
        {
            var jsonPayload = JsonSerializer.Serialize(new { value = txHash });
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            
            var res = await _httpClient.PostAsync("https://api.trongrid.io/walletsolidity/gettransactioninfobyid", content, ct);
            if (!res.IsSuccessStatusCode) return (false, false, 0);

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("blockNumber", out var bNum))
            {
                return (false, false, 0);
            }

            ulong blockNumber = (ulong)bNum.GetInt64();
            if (blockNumber == 0) return (false, false, 0);

            // Receipt validation
            if (!doc.RootElement.TryGetProperty("receipt", out var receipt) ||
                !receipt.TryGetProperty("result", out var resultStr) ||
                !string.Equals(resultStr.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                return (true, false, blockNumber);
            }

            // TRC-20 Transfer Event Validation
            if (!doc.RootElement.TryGetProperty("log", out var logs) || logs.GetArrayLength() == 0)
            {
                return (true, false, blockNumber);
            }

            var logsArray = logs.EnumerateArray().ToList();
            if (expectedEventIndex < 0 || expectedEventIndex >= logsArray.Count)
            {
                _logger.LogWarning("Event index {Index} is out of range for Tx {TxHash}", expectedEventIndex, txHash);
                return (true, false, blockNumber);
            }

            var log = logsArray[expectedEventIndex];
            var contractAddress = log.TryGetProperty("address", out var cAddr) ? cAddr.GetString() : null;
            
            // Normalize TRON USDT Contract
            var expectedContractHex = TronAddressToHex(_usdtContractAddress);
            if (!string.Equals(contractAddress, expectedContractHex, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(contractAddress, _usdtContractAddress, StringComparison.OrdinalIgnoreCase))
            {
                return (true, false, blockNumber);
            }

            if (!log.TryGetProperty("topics", out var topics) || topics.GetArrayLength() < 3)
            {
                return (true, false, blockNumber);
            }

            var signature = topics[0].GetString();
            if (!string.Equals(signature, "ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef", StringComparison.OrdinalIgnoreCase))
            {
                return (true, false, blockNumber);
            }

            // Decode recipient from topics[2]
            var recipientTopicHex = topics[2].GetString() ?? string.Empty;
            if (recipientTopicHex.Length < 40) return (true, false, blockNumber);
            
            var recipientEvmAddress = recipientTopicHex[^40..]; // Last 20 bytes
            var decodedRecipientTron = EvmHexToTronAddress(recipientEvmAddress);

            if (!string.Equals(decodedRecipientTron, expectedToAddress, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Recipient mismatch for Tx {TxHash}. Expected {Exp}, Got {Actual}", txHash, expectedToAddress, decodedRecipientTron);
                return (true, false, blockNumber);
            }

            // Safe uint256 amount parsing
            if (!log.TryGetProperty("data", out var dataProp)) return (true, false, blockNumber);
            var dataHex = dataProp.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(dataHex)) return (true, false, blockNumber);

            if (!System.Numerics.BigInteger.TryParse(dataHex, System.Globalization.NumberStyles.HexNumber, null, out var parsedBigInt) ||
                parsedBigInt < 0 || parsedBigInt > long.MaxValue || (long)parsedBigInt != expectedAmountUnits)
            {
                _logger.LogWarning("Amount mismatch or uint256 overflow for Tx {TxHash}", txHash);
                return (true, false, blockNumber);
            }

            return (true, true, blockNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying solidified transaction {TxHash}", txHash);
            return (false, false, 0);
        }
    }

    private static string EvmHexToTronAddress(string hex20)
    {
        var raw = Convert.FromHexString(hex20);
        var payload = new byte[21];
        payload[0] = 0x41; // TRON Mainnet Address Prefix
        Buffer.BlockCopy(raw, 0, payload, 1, 20);
        
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var h1 = sha256.ComputeHash(payload);
        var h2 = sha256.ComputeHash(h1);
        
        var combined = new byte[25];
        Buffer.BlockCopy(payload, 0, combined, 0, 21);
        Buffer.BlockCopy(h2, 0, combined, 21, 4);

        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var intData = new System.Numerics.BigInteger(combined, isUnsigned: true, isBigEndian: true);
        var res = string.Empty;
        while (intData > 0)
        {
            intData = System.Numerics.BigInteger.DivRem(intData, 58, out var rem);
            res = alphabet[(int)rem] + res;
        }
        for (var i = 0; i < combined.Length && combined[i] == 0; i++) res = '1' + res;
        return res;
    }
    
    private static string TronAddressToHex(string base58Address)
    {
        var bytes = Base58CheckDecode(base58Address);
        // Skip first byte (0x41) for standard EVM address ABI parameter / log comparison
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