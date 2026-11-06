using System;
using System.Linq;
using System.Data;
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
using System.Security.Cryptography;

namespace Gateway.Blockchain.Tron;

public class TronScannerService : ITronScannerService
{
    private readonly GatewayDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TronScannerService> _logger;
    private readonly string _usdtContractAddress;
    private const string TransferTopicSignature = "ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";

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
                               ?? throw new InvalidOperationException(
                                   "Configuration 'Tron:UsdtContractAddress' is required.");
    }

    public async Task ScanPendingDepositsAsync(CancellationToken cancellationToken)
    {
        var solidifiedBlockNumber = await GetSolidifiedBlockNumberAsync(cancellationToken);
        if (solidifiedBlockNumber == 0)
        {
            _logger.LogWarning("SolidityNode head block height could not be determined. Aborting scan cycle safely.");
            return;
        }

        // Serialize scans across worker replicas and atomically persist
        // transfers, invoice states, outbox events and scan cursors.
        // If TronGrid fails mid-cycle, all changes are rolled back so the
        // same page can be retried without missing a transfer.
        await using var tx = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await _dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(6238975401203981)", cancellationToken);

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
                        ((i.Status == InvoiceStatus.Paid || i.Status == InvoiceStatus.Overpaid) &&
                         i.PaidAtUtc.HasValue && i.PaidAtUtc.Value >= overpaymentRetention))
            .ToListAsync(cancellationToken);

        foreach (var invoice in invoices)
        {
            if (!await ScanInvoiceWithPaginationAsync(invoice, solidifiedBlockNumber, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                _dbContext.ChangeTracker.Clear();
                _logger.LogWarning("TRON scan aborted; unresolved provider data will be retried on the next cycle.");
                return;
            }

            // Time-based transitions are written by the Worker with their
            // outbox event, not by a public GET request to Hosted Checkout.
            // Evaluate only AFTER a successful scan so a late payment does not
            // emit a spurious expiration event before being observed.
            var beforeTimeTransition = invoice.Status;
            invoice.EvaluateStatus();
            if (beforeTimeTransition != invoice.Status)
            {
                await QueueInvoiceStatusWebhookAsync(invoice, null, cancellationToken);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task<bool> ScanInvoiceWithPaginationAsync(Invoice invoice, ulong solidifiedBlockNumber,
        CancellationToken ct)
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
            var url =
                $"https://api.trongrid.io/v1/accounts/{invoice.DepositAddress}/transactions/trc20?contract_address={_usdtContractAddress}&limit=20";
            if (!string.IsNullOrEmpty(fingerprint)) url += $"&fingerprint={Uri.EscapeDataString(fingerprint)}";
            else if (cursor.LastProcessedBlockTimestamp > 0)
                url += $"&min_timestamp={cursor.LastProcessedBlockTimestamp}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;

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

                if (!item.TryGetProperty("event_index", out var idxEl) ||
                    !idxEl.TryGetInt32(out var eventIndex)) continue;

                var rawValueStr = item.GetProperty("value").GetString() ?? "0";
                if (!long.TryParse(rawValueStr, out var amountUnits) || amountUnits <= 0) continue;

                var blockTimestamp = item.GetProperty("block_timestamp").GetInt64();

                var txDetail =
                    await VerifySolidifiedTransactionAsync(txHash, invoice.DepositAddress, amountUnits, eventIndex, ct);
                if (!txDetail.IsSolidified || txDetail.BlockNumber > solidifiedBlockNumber)
                {
                    // A missing/unavailable receipt or a not-yet-solidified
                    // block must never advance the cursor past this transfer.
                    return false;
                }
                if (!txDetail.IsSuccess)
                {
                    // A solidified transaction with a failed receipt, wrong
                    // contract or invalid event is safely ignored.
                    _logger.LogWarning("Tx {TxHash} rejected by solidified receipt validation.", txHash);
                    continue;
                }

                var existingTransfer = await _dbContext.PaymentTransfers
                    .FirstOrDefaultAsync(
                        t => t.Network == "TRON" && t.TransactionHash == txHash && t.TransferIndex == eventIndex, ct);

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

                    transfer.UpdateConfirmations(solidifiedBlockNumber, 1);

                    var previousStatus = invoice.Status;

                    // Register before adding to DbContext to prevent EF relationship
                    // fixup from making the transfer appear as a duplicate.
                    if (invoice.RegisterTransfer(transfer))
                    {
                        _dbContext.PaymentTransfers.Add(transfer);

                        var currentStatus = invoice.Status;

                        if (previousStatus != currentStatus)
                        {
                            await QueueInvoiceStatusWebhookAsync(invoice, txHash, ct);
                        }
                    }

                    _dbContext.Entry(invoice).State = EntityState.Modified;
                }
                else
                {
                    existingTransfer.UpdateConfirmations(solidifiedBlockNumber, 1);
                    invoice.EvaluateStatus();
                    _dbContext.Entry(invoice).State = EntityState.Modified;
                }

                cursor.UpdateCursor(blockTimestamp, txDetail.BlockNumber);
            }

            if (doc.RootElement.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("fingerprint", out var fpProp) && !string.IsNullOrWhiteSpace(fpProp.GetString()))
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
        return true;
    }

    private async Task QueueInvoiceStatusWebhookAsync(
        Invoice invoice, string? transactionHash, CancellationToken ct)
    {
        var merchant = await _dbContext.Merchants.FindAsync(
            new object[] { invoice.MerchantId }, ct);
        if (merchant is null)
        {
            // An Invoice without its Merchant is an integrity failure. Do not
            // silently commit a state transition without its webhook event.
            throw new InvalidOperationException(
                $"Merchant missing while emitting invoice status event for {invoice.Id}.");
        }

        var eventType = $"invoice.{invoice.Status.ToString().ToLowerInvariant()}";
        var payload = JsonSerializer.Serialize(new
        {
            @event = eventType,
            invoiceId = invoice.Id,
            status = invoice.Status.ToString(),
            paidUnits = invoice.PaidAmountUnits,
            expectedUnits = invoice.ExpectedAmountUnits,
            txHash = transactionHash
        });

        _dbContext.OutboxEvents.Add(new OutboxEvent(
            invoice.Id, eventType, payload,
            invoice.CallbackUrl, merchant.EncryptedWebhookSecret));
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
                header.TryGetProperty("raw_data", out var raw) && raw.TryGetProperty("number", out var num))
                return (ulong)num.GetInt64();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query solidified head block.");
        }

        return 0;
    }

    public async Task<(bool IsSolidified, bool IsSuccess, ulong BlockNumber)> VerifySolidifiedTransactionAsync(
        string txHash, string expectedToAddress, long expectedAmountUnits, int expectedEventIndex, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(txHash)) return (false, false, 0);
            if (expectedAmountUnits <= 0) return (false, false, 0);
            if (expectedEventIndex < 0) return (false, false, 0);

            var jsonPayload = JsonSerializer.Serialize(new { value = txHash });
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var res = await _httpClient.PostAsync("https://api.trongrid.io/walletsolidity/gettransactioninfobyid",
                content, ct);
            if (!res.IsSuccessStatusCode) return (false, false, 0);

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("blockNumber", out var bNum)) return (false, false, 0);
            ulong blockNumber = (ulong)bNum.GetInt64();
            if (blockNumber == 0) return (false, false, 0);

            if (!doc.RootElement.TryGetProperty("receipt", out var receipt) ||
                !receipt.TryGetProperty("result", out var resultStr) || !string.Equals(resultStr.GetString(), "SUCCESS",
                    StringComparison.OrdinalIgnoreCase))
                return (true, false, blockNumber);

            if (!doc.RootElement.TryGetProperty("log", out var logs) || logs.GetArrayLength() == 0)
                return (true, false, blockNumber);

            if (expectedEventIndex >= logs.GetArrayLength())
            {
                _logger.LogWarning("event_index {Idx} out of bounds (logs={Count}) for tx {Tx}. Rejecting.",
                    expectedEventIndex, logs.GetArrayLength(), txHash);
                return (true, false, blockNumber);
            }

            var targetLog = logs[expectedEventIndex];

            string expectedContractHex;
            string expectedRecipientHex;
            try
            {
                expectedContractHex = TronAddressToHex(_usdtContractAddress);
                expectedRecipientHex = TronAddressToHex(expectedToAddress);
            }
            catch (Exception)
            {
                return (false, false, 0);
            }

            if (!IsMatchingUsdtTransfer(targetLog, expectedContractHex, expectedRecipientHex, expectedAmountUnits))
            {
                _logger.LogWarning(
                    "Log at event_index {Idx} for tx {Tx} does not match expected USDT transfer. Rejecting.",
                    expectedEventIndex, txHash);
                return (true, false, blockNumber);
            }

            int matchCount = 0;
            foreach (var log in logs.EnumerateArray())
            {
                if (IsMatchingUsdtTransfer(log, expectedContractHex, expectedRecipientHex, expectedAmountUnits))
                {
                    matchCount++;
                    if (matchCount > 1) return (true, false, blockNumber);
                }
            }

            return (true, true, blockNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying tx {TxHash}", txHash);
            return (false, false, 0);
        }
    }

    private bool IsMatchingUsdtTransfer(JsonElement log, string expectedContractHex, string expectedRecipientHex,
        long expectedAmountUnits)
    {
        if (!log.TryGetProperty("address", out var cAddr)) return false;
        var contractRaw = cAddr.GetString();
        if (string.IsNullOrWhiteSpace(contractRaw)) return false;
        var contractHex = NormalizeHexAddress(contractRaw);
        if (contractHex == null ||
            !string.Equals(contractHex, expectedContractHex, StringComparison.OrdinalIgnoreCase)) return false;

        if (!log.TryGetProperty("topics", out var topics) || topics.GetArrayLength() < 3) return false;

        var sigRaw = topics[0].GetString();
        var sig = NormalizeTopicHex(sigRaw);
        if (sig == null || !string.Equals(sig, TransferTopicSignature, StringComparison.OrdinalIgnoreCase)) return false;

        var recipientTopicRaw = topics[2].GetString();
        var recipientTopic = NormalizeTopicHex(recipientTopicRaw);
        if (recipientTopic == null || recipientTopic.Length != 64) return false;
        var recipientEvm = recipientTopic.Substring(24);
        if (!string.Equals(recipientEvm, expectedRecipientHex, StringComparison.OrdinalIgnoreCase)) return false;

        if (!log.TryGetProperty("data", out var dataProp)) return false;
        var dataHex = StripHexPrefix(dataProp.GetString());
        if (string.IsNullOrWhiteSpace(dataHex)) return false;
        if (!IsHex(dataHex)) return false;

        if (!System.Numerics.BigInteger.TryParse(dataHex, System.Globalization.NumberStyles.HexNumber, null,
                out var parsed))
            return false;

        if (parsed < 0 || parsed > long.MaxValue) return false;
        if ((long)parsed != expectedAmountUnits) return false;

        return true;
    }

    private static string? NormalizeTopicHex(string? raw)
    {
        var s = StripHexPrefix(raw);
        if (string.IsNullOrEmpty(s) || !IsHex(s)) return null;
        return s.ToLowerInvariant();
    }

    private static string? NormalizeHexAddress(string? raw)
    {
        var s = StripHexPrefix(raw);
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length == 42 && s.StartsWith("41", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (s.Length != 40 || !IsHex(s)) return null;
        return s.ToLowerInvariant();
    }

    private static string? StripHexPrefix(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return s;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }

        return true;
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
            if (index < 0) throw new FormatException("Invalid Base58 character.");
            intData = intData * 58 + index;
        }

        var bytes = intData.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = 0;
        for (var i = 0; i < base58.Length && base58[i] == '1'; i++) leadingZeros++;
        var result = new byte[leadingZeros + bytes.Length];
        Buffer.BlockCopy(bytes, 0, result, leadingZeros, bytes.Length);

        if (result.Length < 5) throw new FormatException("Base58Check payload too short.");

        var payload = new byte[result.Length - 4];
        Buffer.BlockCopy(result, 0, payload, 0, payload.Length);
        var checksum = new byte[4];
        Buffer.BlockCopy(result, payload.Length, checksum, 0, 4);

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash1 = sha256.ComputeHash(payload);
        var hash2 = sha256.ComputeHash(hash1);

        if (hash2[0] != checksum[0] || hash2[1] != checksum[1] || hash2[2] != checksum[2] || hash2[3] != checksum[3])
            throw new FormatException("Invalid Base58Check checksum.");

        if (payload.Length != 21) throw new FormatException("Invalid TRON address length.");
        if (payload[0] != 0x41) throw new FormatException("Invalid TRON network prefix.");

        return payload;
    }
}