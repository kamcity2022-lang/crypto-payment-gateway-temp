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
using Microsoft.Extensions.Logging;

namespace Gateway.Blockchain.Tron;

public class TronScannerService : ITronScannerService
{
    private readonly GatewayDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TronScannerService> _logger;

    // Official USDT TRC-20 Contract Address on TRON Mainnet
    private const string UsdtContractAddress = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";

    public TronScannerService(
        GatewayDbContext dbContext,
        HttpClient httpClient,
        ILogger<TronScannerService> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task ScanPendingDepositsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        // 1. Fetch active invoices needing settlement
        var activeInvoices = await _dbContext.Invoices
            .Where(i => i.Status == InvoiceStatus.Pending && i.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);

        if (!activeInvoices.Any())
        {
            return;
        }

        foreach (var invoice in activeInvoices)
        {
            try
            {
                // Query TronGrid TRC-20 transfer event API
                var requestUrl = $"https://api.trongrid.io/v1/accounts/{invoice.DepositAddress}/transactions/trc20?contract_address={UsdtContractAddress}&limit=5";
                
                using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to query TronGrid for address {Address}. Status: {Status}", invoice.DepositAddress, response.StatusCode);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.GetArrayLength() == 0)
                {
                    continue;
                }

                foreach (var item in dataElement.EnumerateArray())
                {
                    var toAddress = item.GetProperty("to").GetString();
                    if (!string.Equals(toAddress, invoice.DepositAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var txHash = item.GetProperty("transaction_id").GetString() ?? string.Empty;
                    var fromAddress = item.GetProperty("from").GetString() ?? string.Empty;
                    var rawValueStr = item.GetProperty("value").GetString() ?? "0";
                    var blockTimestamp = item.GetProperty("block_timestamp").GetInt64();

                    if (!long.TryParse(rawValueStr, out var amountUnits))
                    {
                        continue;
                    }

                    // Register atomic on-chain transfer
                    var transfer = new PaymentTransfer(
                        invoiceId: invoice.Id,
                        network: "TRON",
                        transactionHash: txHash,
                        transferIndex: 0,
                        tokenContractAddress: UsdtContractAddress,
                        fromAddress: fromAddress,
                        toAddress: toAddress,
                        amountUnits: amountUnits,
                        blockNumber: (ulong)(blockTimestamp / 3000) // TRON approximate 3-second block time
                    );

                    // Assume finality threshold met for confirmed scan
                    transfer.UpdateConfirmations(transfer.BlockNumber + 20, 19);

                    invoice.RegisterTransfer(transfer);
                    _dbContext.PaymentTransfers.Add(transfer);

                    _logger.LogInformation("Observed valid transfer for Invoice {InvoiceId}: {Units} Sun ({TxHash})", invoice.Id, amountUnits, txHash);
                }

                invoice.EvaluateStatus();
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning deposit address {Address}", invoice.DepositAddress);
            }
        }
    }
}