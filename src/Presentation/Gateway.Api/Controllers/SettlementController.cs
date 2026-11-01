using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Api.Authentication;
using Gateway.Application.Contracts;
using Gateway.Application.DTOs;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Gateway.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/settlement")]
[RequireMerchantSignature]
public class SettlementController : ControllerBase
{
    private readonly GatewayDbContext _dbContext;
    private readonly ITronSweepService _tronSweepService;
    private readonly string _usdtContractAddress;

    public SettlementController(GatewayDbContext dbContext, IConfiguration configuration, ITronSweepService  tronSweepService)
    {
        _dbContext = dbContext;
        _tronSweepService = tronSweepService;
        _usdtContractAddress = configuration["Tron:UsdtContractAddress"] ?? "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
    }

    [HttpPost("address")]
    public async Task<IActionResult> SetSettlementAddress([FromBody] UpdateSettlementAddressRequest request, CancellationToken ct)
    {
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        var merchant = await _dbContext.Merchants.FirstOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant == null)
            return NotFound(new { error = "Merchant not found." });

        try
        {
            merchant.SetSettlementAddress(request.SettlementAddress);
            await _dbContext.SaveChangesAsync(ct);
            return Ok(new { message = "Settlement address updated successfully.", settlementAddress = merchant.SettlementAddress });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("candidates")]
    public async Task<IActionResult> GetSweepCandidates(CancellationToken ct)
    {
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        // Find derived addresses belonging to this merchant that have confirmed payments
        var candidates = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.MerchantId == merchantId && (i.Status == InvoiceStatus.Paid || i.Status == InvoiceStatus.Overpaid))
            .Join(_dbContext.DerivedAddresses,
                  inv => inv.DerivedAddressId,
                  addr => addr.Id,
                  (inv, addr) => new
                  {
                      addr.Id,
                      addr.AddressIndex,
                      addr.Address,
                      inv.PaidAmountUnits
                  })
            .GroupBy(x => new { x.Id, x.AddressIndex, x.Address })
            .Select(g => new SweepCandidateDto
            {
                DerivedAddressId = g.Key.Id,
                AddressIndex = (uint)g.Key.AddressIndex,
                Address = g.Key.Address,
                BalanceUnits = g.Sum(x => x.PaidAmountUnits),
                BalanceUsdt = g.Sum(x => x.PaidAmountUnits) / 1_000_000m
            })
            .Where(c => c.BalanceUnits > 0)
            .ToListAsync(ct);

        return Ok(candidates);
    }

    [HttpPost("tasks")]
    public async Task<IActionResult> CreateSweepTask([FromBody] CreateSweepTaskRequest request, CancellationToken ct)
    {
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        var merchant = await _dbContext.Merchants.FirstOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant == null || string.IsNullOrEmpty(merchant.SettlementAddress))
        {
            return BadRequest(new { error = "Settlement address must be configured before creating sweep tasks." });
        }

        var derivedAddress = await _dbContext.DerivedAddresses
            .Join(_dbContext.Wallets,
                  da => da.WalletId,
                  w => w.Id,
                  (da, w) => new { da, w.MerchantId })
            .Where(x => x.MerchantId == merchantId && x.da.Id == request.DerivedAddressId)
            .Select(x => x.da)
            .FirstOrDefaultAsync(ct);

        if (derivedAddress == null)
            return NotFound(new { error = "Derived address not found or does not belong to this merchant." });

        // Calculate confirmed balance
        var totalConfirmedUnits = await _dbContext.Invoices
            .Where(i => i.DerivedAddressId == derivedAddress.Id && (i.Status == InvoiceStatus.Paid || i.Status == InvoiceStatus.Overpaid))
            .SumAsync(i => i.PaidAmountUnits, ct);

        if (totalConfirmedUnits <= 0)
            return BadRequest(new { error = "No confirmed funds available to sweep for this address." });

        // Check if there is an active sweep task in progress
        var activeTaskExists = await _dbContext.SweepTasks
            .AnyAsync(s => s.DerivedAddressId == derivedAddress.Id && 
                          (s.Status == SweepStatus.PendingEvaluation || 
                           s.Status == SweepStatus.FundingRequired || 
                           s.Status == SweepStatus.ReadyToSign ||
                           s.Status == SweepStatus.Broadcasting), ct);

        if (activeTaskExists)
            return Conflict(new { error = "A sweep operation is already in progress for this address." });

        var sweepTask = new SweepTask(
            merchantId,
            derivedAddress.Id,
            derivedAddress.Address,
            merchant.SettlementAddress,
            _usdtContractAddress,
            totalConfirmedUnits
        );

        _dbContext.SweepTasks.Add(sweepTask);
        await _dbContext.SaveChangesAsync(ct);

        return Ok(new SweepTaskDto
        {
            SweepTaskId = sweepTask.Id,
            SourceAddress = sweepTask.SourceAddress,
            DestinationAddress = sweepTask.DestinationAddress,
            AmountUsdt = sweepTask.AmountUnits / 1_000_000m,
            Status = sweepTask.Status.ToString(),
            CreatedAtUtc = sweepTask.CreatedAtUtc
        });
    }
    
    [HttpPost("tasks/prepare")]
    public async Task<IActionResult> PrepareSweepTransaction(
        [FromBody] PrepareSweepTransactionRequest request,
        [FromServices] ITronSweepService sweepService,
        CancellationToken ct)
    {
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        var task = await _dbContext.SweepTasks
            .FirstOrDefaultAsync(t => t.Id == request.SweepTaskId && t.MerchantId == merchantId, ct);

        if (task == null)
            return NotFound(new { error = "Sweep task not found." });

        if (task.Status != SweepStatus.PendingEvaluation && task.Status != SweepStatus.ReadyToSign)
            return BadRequest(new { error = $"Cannot prepare transaction in status {task.Status}." });

        try
        {
            var rawTx = await sweepService.CreateUnsignedSweepTransactionAsync(
                task.SourceAddress,
                task.DestinationAddress,
                task.AmountUnits,
                ct);

            task.MarkReadyToSign();
            await _dbContext.SaveChangesAsync(ct);

            using var doc = System.Text.Json.JsonDocument.Parse(rawTx);
            var txHash = doc.RootElement.GetProperty("txID").GetString() ?? string.Empty;

            return Ok(new PrepareSweepTransactionResponse
            {
                SweepTaskId = task.Id,
                RawTransactionJson = rawTx,
                TxHash = txHash,
                Message = "Unsigned transaction generated. Sign this payload with the merchant's key/signer and broadcast."
            });
        }
        catch (Exception ex)
        {
            task.MarkFailed(ex.Message);
            await _dbContext.SaveChangesAsync(ct);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("tasks/broadcast")]
    public async Task<IActionResult> BroadcastSweep(
        [FromBody] BroadcastSweepRequest request,
        [FromServices] ITronSweepService sweepService,
        CancellationToken ct)
    {
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        var task = await _dbContext.SweepTasks
            .FirstOrDefaultAsync(t => t.Id == request.SweepTaskId && t.MerchantId == merchantId, ct);

        if (task == null)
            return NotFound(new { error = "Sweep task not found." });

        if (task.Status != SweepStatus.ReadyToSign)
            return BadRequest(new { error = $"Task is not in 'ReadyToSign' status. Current: {task.Status}" });

        try
        {
            var txId = await sweepService.BroadcastSignedTransactionAsync(request.SignedTransactionJson, ct);
            task.MarkBroadcasting(txId);
            await _dbContext.SaveChangesAsync(ct);

            return Ok(new { message = "Transaction broadcasted successfully.", txId = txId });
        }
        catch (Exception ex)
        {
            task.MarkFailed(ex.Message);
            await _dbContext.SaveChangesAsync(ct);
            return BadRequest(new { error = ex.Message });
        }
    }
}