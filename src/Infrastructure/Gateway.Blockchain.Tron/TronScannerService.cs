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
    private readonly ulong _requiredConfirmations;
    private readonly string _usdtContractAddress;

    public TronScannerService(
        GatewayDbContext dbContext,
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<TronScannerService> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _logger = logger;

        // Parse configuration safely without relying on Microsoft.Extensions.Configuration.Binder
        var confVal = configuration["Tron:RequiredConfirmations"];
        _requiredConfirmations = ulong.TryParse(confVal, out var parsedConf) ? parsedConf : 19;
        _usdtContractAddress = configuration["Tron:UsdtContractAddress"] ?? "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
    }

    public async Task ScanPendingDepositsAsync(CancellationToken cancellationToken)
    {
        // 1. Get real current confirmed head block from TRON network
        var currentBlockNumber = await GetCurrentBlockNumberAsync(cancellationToken);
        if (currentBlockNumber == 0)
        {
            _logger.LogWarning("Unable to retrieve TRON head block. Skipping current scan cycle.");
            return;
        }

        // 2. Fetch active invoices (Pending, PartiallyPaid, plus recently expired buffer)
        var scanThreshold = DateTime.UtcNow.AddHours(-2);
        var invoices = await _dbContext.Invoices
            .Include(i => i.Transfers)
            .Where(i => (i.Status == InvoiceStatus.Pending || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Expired)
                        && i.CreatedAtUtc >= scanThreshold)
            .ToListAsync(cancellationToken);

        foreach (var invoice in invoices)
        {
            await ScanInvoiceAddressAsync(invoice, currentBlockNumber, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ScanInvoiceAddressAsync(Invoice invoice, ulong currentBlockNumber, CancellationToken ct)
    {
        var cursor = await _dbContext.BlockchainScanCursors
            .FirstOrDefaultAsync(c => c.Network == "TRON" && c.Address == invoice.DepositAddress, ct);

        if (cursor == null)
        {
            cursor = new BlockchainScanCursor("TRON", invoice.DepositAddress, 0, 0);
            _dbContext.BlockchainScanCursors.Add(cursor);
        }

        var minTimestamp = cursor.LastProcessedBlockTimestamp > 0 ? cursor.LastProcessedBlockTimestamp : 0;
        var url = $"https://api.trongrid.io/v1/accounts/{invoice.DepositAddress}/transactions/trc20?contract_address={_usdtContractAddress}&min_timestamp={minTimestamp}&limit=20";

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TronGrid API returned {Status} for address {Address}", response.StatusCode, invoice.DepositAddress);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Network failure while scanning TRON address {Address}", invoice.DepositAddress);
            return;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.GetArrayLength() == 0)
            return;

        long latestTimestamp = cursor.LastProcessedBlockTimestamp;

        foreach (var item in dataElement.EnumerateArray())
        {
            var toAddress = item.GetProperty("to").GetString() ?? string.Empty;
            if (!string.Equals(toAddress, invoice.DepositAddress, StringComparison.OrdinalIgnoreCase))
                continue;

            var txHash = item.GetProperty("transaction_id").GetString() ?? string.Empty;
            var fromAddress = item.GetProperty("from").GetString() ?? string.Empty;
            var rawValue = item.GetProperty("value").GetString() ?? "0";
            var blockTimestamp = item.GetProperty("block_timestamp").GetInt64();

            // Extract real event index from TRON response payload
            var eventIndex = 0;
            if (item.TryGetProperty("event_index", out var idxElement) && idxElement.TryGetInt32(out var parsedIdx))
            {
                eventIndex = parsedIdx;
            }

            if (!long.TryParse(rawValue, out var amountUnits))
                continue;

            // Fetch actual transaction detail to determine the exact block number
            var txBlockNumber = await GetTransactionBlockNumberAsync(txHash, ct);
            if (txBlockNumber == 0)
            {
                _logger.LogWarning("Unable to resolve block height for TxHash {TxHash}. Deferring processing.", txHash);
                continue;
            }

            var transfer = await _dbContext.PaymentTransfers
                .FirstOrDefaultAsync(t => t.Network == "TRON" && t.TransactionHash == txHash && t.TransferIndex == eventIndex, ct);

            if (transfer == null)
            {
                transfer = new PaymentTransfer(
                    invoice.Id,
                    "TRON",
                    txHash,
                    eventIndex,
                    _usdtContractAddress,
                    fromAddress,
                    toAddress,
                    amountUnits,
                    txBlockNumber);

                _dbContext.PaymentTransfers.Add(transfer);
                invoice.RegisterTransfer(transfer);
            }

            // Real confirmation check
            transfer.UpdateConfirmations(currentBlockNumber, _requiredConfirmations);
            var prevStatus = invoice.Status;
            var newStatus = invoice.EvaluateStatus();

            // Enqueue transactional outbox event on meaningful state change
            if (prevStatus != newStatus && (newStatus == InvoiceStatus.Paid || newStatus == InvoiceStatus.Overpaid || newStatus == InvoiceStatus.PartiallyPaid))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    @event = $"invoice.{newStatus.ToString().ToLowerInvariant()}",
                    invoiceId = invoice.Id,
                    status = newStatus.ToString(),
                    paidUnits = invoice.PaidAmountUnits,
                    txHash = txHash
                });

                var outbox = new OutboxEvent(invoice.Id, $"invoice.{newStatus.ToString().ToLowerInvariant()}", payload, invoice.CallbackUrl, "merchant_secret");
                _dbContext.OutboxEvents.Add(outbox);
            }

            latestTimestamp = Math.Max(latestTimestamp, blockTimestamp);
        }

        cursor.UpdateCursor(latestTimestamp, currentBlockNumber);
    }

    private async Task<ulong> GetCurrentBlockNumberAsync(CancellationToken ct)
    {
        try
        {
            var res = await _httpClient.GetAsync("https://api.trongrid.io/wallet/getnowblock", ct);
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
            _logger.LogError(ex, "Failed to retrieve current block number from TronGrid.");
        }
        return 0;
    }

    private async Task<ulong> GetTransactionBlockNumberAsync(string txHash, CancellationToken ct)
    {
        try
        {
            var res = await _httpClient.PostAsync("https://api.trongrid.io/wallet/gettransactioninfobyid",
                new StringContent(JsonSerializer.Serialize(new { value = txHash })), ct);

            if (!res.IsSuccessStatusCode) return 0;

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("blockNumber", out var num))
            {
                return (ulong)num.GetInt64();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve tx info for {TxHash}", txHash);
        }
        return 0;
    }
}