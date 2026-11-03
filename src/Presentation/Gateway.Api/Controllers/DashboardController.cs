using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.DTOs;
using Gateway.Domain.Enums;
using Gateway.Infrastructure.Security;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/dashboard")]
[Authorize(Policy = "MerchantViewer")]
public class DashboardController : ControllerBase
{
    private readonly GatewayDbContext _dbContext;

    public DashboardController(GatewayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    private bool TryGetResolvedMerchantId(out Guid merchantId)
    {
        merchantId = Guid.Empty;
        if (HttpContext.Items.TryGetValue("ResolvedMerchantId", out var resolved) && resolved is Guid id)
        {
            merchantId = id;
            return true;
        }
        return false;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(CancellationToken ct)
    {
        if (!TryGetResolvedMerchantId(out var merchantId)) return Forbid();

        var overview = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.MerchantId == merchantId)
            .GroupBy(i => 1)
            .Select(g => new DashboardOverviewDto
            {
                TotalInvoices = g.Count(),
                PaidInvoices = g.Count(i => i.Status == InvoiceStatus.Paid || i.Status == InvoiceStatus.Overpaid),
                PendingInvoices = g.Count(i => i.Status == InvoiceStatus.Pending || i.Status == InvoiceStatus.PartiallyPaid),
                ExpiredOrFailedInvoices = g.Count(i =>
                    i.Status == InvoiceStatus.Expired ||
                    i.Status == InvoiceStatus.Cancelled ||
                    i.Status == InvoiceStatus.Underpaid),
                TotalReceivedAmount = g.Sum(i => i.PaidAmountUnits) / 1_000_000m,
                FiatCurrency = "USD",
                RecentInvoices = g.OrderByDescending(i => i.CreatedAtUtc)
                    .Take(5)
                    .Select(i => new RecentInvoiceDto
                    {
                        Id = i.Id,
                        FiatAmount = i.FiatAmount,
                        Status = i.Status.ToString(),
                        CreatedAtUtc = i.CreatedAtUtc
                    }).ToList()
            })
            .FirstOrDefaultAsync(ct);

        return Ok(overview ?? new DashboardOverviewDto());
    }

    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        if (!TryGetResolvedMerchantId(out var merchantId)) return Forbid();

        var merchant = await _dbContext.Merchants
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == merchantId, ct);

        if (merchant == null) return NotFound(new { error = "Merchant profile not found." });

        var wallets = await _dbContext.Wallets
            .AsNoTracking()
            .Where(w => w.MerchantId == merchantId)
            .Select(w => new MerchantWalletDto
            {
                WalletId = w.Id,
                Network = w.Network,
                TotalAddressesAllocated = w.LastDerivationIndex,
                CreatedAtUtc = w.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Ok(new MerchantProfileDto
        {
            MerchantId = merchant.Id,
            Name = merchant.Name,
            IsActive = merchant.IsActive,
            CreatedAtUtc = merchant.CreatedAtUtc,
            Wallets = wallets
        });
    }

    [HttpPut("profile")]
    [Authorize(Policy = "MerchantAdmin")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateMerchantProfileRequest request, CancellationToken ct)
    {
        if (!TryGetResolvedMerchantId(out var merchantId)) return Forbid();

        var merchant = await _dbContext.Merchants.FirstOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant == null) return NotFound(new { error = "Merchant not found." });

        merchant.UpdateName(request.Name);

        if (!string.IsNullOrWhiteSpace(request.DefaultWebhookUrl))
        {
            if (!SsrfValidator.IsDestinationSafe(request.DefaultWebhookUrl, allowHttp: false))
            {
                return BadRequest(new { error = "Invalid or prohibited webhook URL destination." });
            }
            merchant.SetDefaultWebhookUrl(request.DefaultWebhookUrl);
        }

        await _dbContext.SaveChangesAsync(ct);
        return Ok(new { message = "Profile updated successfully." });
    }

    [HttpGet("invoices")]
    public async Task<IActionResult> GetInvoices([FromQuery] DashboardInvoiceFilterRequest filter, CancellationToken ct)
    {
        if (!TryGetResolvedMerchantId(out var merchantId)) return Forbid();

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var query = _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.MerchantId == merchantId);

        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<InvoiceStatus>(filter.Status, true, out var status))
        {
            query = query.Where(i => i.Status == status);
        }

        if (filter.FromDateUtc.HasValue)
            query = query.Where(i => i.CreatedAtUtc >= filter.FromDateUtc.Value);

        if (filter.ToDateUtc.HasValue)
            query = query.Where(i => i.CreatedAtUtc <= filter.ToDateUtc.Value);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new DashboardInvoiceSummaryDto
            {
                Id = i.Id,
                FiatAmount = i.FiatAmount,
                FiatCurrency = i.FiatCurrency,
                ExpectedAmountUnits = i.ExpectedAmountUnits,
                PaidAmountUnits = i.PaidAmountUnits,
                Status = i.Status.ToString(),
                DepositAddress = i.DepositAddress,
                CreatedAtUtc = i.CreatedAtUtc,
                ExpiresAtUtc = i.ExpiresAtUtc
            })
            .ToListAsync(ct);

        return Ok(new PagedDashboardResult<DashboardInvoiceSummaryDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        });
    }

    [HttpGet("invoices/{invoiceId:guid}")]
    public async Task<IActionResult> GetInvoiceDetails(Guid invoiceId, CancellationToken ct)
    {
        if (!TryGetResolvedMerchantId(out var merchantId)) return Forbid();

        var result = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.Id == invoiceId && i.MerchantId == merchantId)
            .Select(i => new DashboardInvoiceDetailsDto
            {
                Id = i.Id,
                DepositAddress = i.DepositAddress,
                FiatAmount = i.FiatAmount,
                FiatCurrency = i.FiatCurrency,
                Currency = i.Currency,
                Network = i.Network,
                ExpectedAmountUnits = i.ExpectedAmountUnits,
                PaidAmountUnits = i.PaidAmountUnits,
                Status = i.Status.ToString(),
                CreatedAtUtc = i.CreatedAtUtc,
                ExpiresAtUtc = i.ExpiresAtUtc,
                PaidAtUtc = i.PaidAtUtc,
                Transfers = i.Transfers.Select(t => new PaymentTransferSummaryDto
                {
                    Id = t.Id,
                    TransactionHash = t.TransactionHash,
                    FromAddress = t.FromAddress,
                    ToAddress = t.ToAddress,
                    AmountUnits = t.AmountUnits,
                    BlockNumber = t.BlockNumber,
                    Status = t.Status.ToString(),
                    ObservedAtUtc = t.ObservedAtUtc
                }).ToList()
            })
            .FirstOrDefaultAsync(ct);

        if (result == null) return NotFound(new { error = "Invoice not found." });
        return Ok(result);
    }
}