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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gateway.Blockchain.Tron;

public class TronScannerService : ITronScannerService
{
    private readonly GatewayDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TronScannerService> _logger;
    private readonly string _usdtContractAddress;
    private readonly ISecretProtector _secretProtector;

    public TronScannerService(
        GatewayDbContext dbContext,
        HttpClient httpClient,
        IConfiguration configuration,
        ISecretProtector secretProtector,
        ILogger<TronScannerService> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _secretProtector = secretProtector;
        _logger = logger;
        _usdtContractAddress = configuration["Tron:UsdtContractAddress"] 
            ?? throw new InvalidOperationException("Configuration 'Tron:UsdtContractAddress' is required.");
    }

    public async Task ScanPendingDepositsAsync(CancellationToken cancellationToken)
    {
        var solidifiedBlockNumber = await GetSolidifiedBlockNumberAsync(cancellationToken);
        if (solidifiedBlockNumber == 0)
        {
            _logger.LogWarning("Unable to resolve solidified block height. Aborting scan cycle.");
            return;
        }

        // Include Pending, PartiallyPaid, Underpaid, and recently Expired or Paid (for overpayment auditing)
        var retentionThreshold = DateTime.UtcNow.AddDays(-7);
        var invoices = await _dbContext.Invoices
            .Include(i => i.Transfers)
            .Where(i => i.CreatedAtUtc >= retentionThreshold &&
                        (i.Status == InvoiceStatus.Pending ||
                         i.Status == InvoiceStatus.PartiallyPaid ||
                         i.Status == InvoiceStatus.Underpaid ||
                         i.Status == InvoiceStatus.Paid ||
                         i.Status == InvoiceStatus.Expired))
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
                _logger.LogWarning("TronGrid query returned {Status} for address {Address}", response.StatusCode, invoice.DepositAddress);
                return; // Stop pagination on error; do not advance cursor
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in dataElement.EnumerateArray())
            {
                var toAddress = item.GetProperty("to").GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(toAddress) || !string.Equals(toAddress, invoice.DepositAddress, StringComparison.OrdinalIgnoreCase))
                    continue;

                var txHash = item.GetProperty("transaction_id").GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(txHash)) continue;

                // Establish strict event/log index
                if (!item.TryGetProperty("event_index", out var idxEl) || !idxEl.TryGetInt32(out var eventIndex))
                {
                    _logger.LogError("Transfer event index could not be determined for Tx {TxHash}. Skipping to prevent duplicate accounting.", txHash);
                    continue;
                }

                var rawValueStr = item.GetProperty("value").GetString() ?? "0";
                if (!long.TryParse(rawValueStr, out var amountUnits) || amountUnits <= 0) continue;

                var blockTimestamp = item.GetProperty("block_timestamp").GetInt64();

                // Reorganization protection: Verify transaction via SolidityNode
                var txDetail = await VerifySolidifiedTransactionAsync(txHash, ct);
                if (!txDetail.IsSolidified || !txDetail.IsSuccess)
                {
                    _logger.LogWarning("Tx {TxHash} not solidified or execution failed. Skipping transfer credit.", txHash);
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
                        transfer.UpdateConfirmations(solidifiedBlockNumber, 1); // Confirmed in solidified chain
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

                cursor.UpdateCursor(blockTimestamp, txDetail.BlockNumber);
            }

            // Check next page fingerprint
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
            _logger.LogError(ex, "Failed to fetch solidified head block from TronGrid.");
        }
        return 0;
    }

    private async Task<(bool IsSolidified, bool IsSuccess, ulong BlockNumber)> VerifySolidifiedTransactionAsync(string txHash, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { value = txHash }), System.Text.Encoding.UTF8, "application/json");
            var res = await _httpClient.PostAsync("https://api.trongrid.io/walletsolidity/gettransactioninfobyid", content, ct);

            if (!res.IsSuccessStatusCode) return (false, false, 0);

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("blockNumber", out var bNum))
            {
                return (false, false, 0);
            }

            ulong blockNumber = (ulong)bNum.GetInt64();
            bool isSuccess = true;

            if (doc.RootElement.TryGetProperty("receipt", out var receipt) &&
                receipt.TryGetProperty("result", out var resultStr))
            {
                isSuccess = string.Equals(resultStr.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase);
            }

            return (true, isSuccess, blockNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying solidified transaction for hash {TxHash}", txHash);
            return (false, false, 0);
        }
    }
}