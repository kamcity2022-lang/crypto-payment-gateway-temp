using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.DTOs;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = "SuperAdminOnly")]
public class AdminController : ControllerBase
{
    private readonly GatewayDbContext _dbContext;

    public AdminController(GatewayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(CancellationToken ct)
    {
        var overview = await _dbContext.Merchants
            .AsNoTracking()
            .GroupBy(m => 1)
            .Select(g => new PlatformOverviewDto
            {
                TotalMerchants = g.Count(),
                ActiveMerchants = g.Count(m => m.IsActive),
                InactiveMerchants = g.Count(m => !m.IsActive),
                TotalWallets = _dbContext.Wallets.AsNoTracking().Count()
            })
            .FirstOrDefaultAsync(ct);

        return Ok(overview ?? new PlatformOverviewDto());
    }

    [HttpGet("merchants")]
    public async Task<IActionResult> GetMerchants([FromQuery] AdminMerchantFilterRequest filter, CancellationToken ct)
    {
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var query = _dbContext.Merchants.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            // ✅ اصلاح: استفاده از ILike برای ترجمه صحیح به SQL در PostgreSQL
            query = query.Where(m => EF.Functions.ILike(m.Name, $"%{search}%"));
        }

        if (filter.IsActive.HasValue)
        {
            query = query.Where(m => m.IsActive == filter.IsActive.Value);
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(m => m.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new MerchantSummaryDto
            {
                MerchantId = m.Id,
                Name = m.Name,
                IsActive = m.IsActive,
                CreatedAtUtc = m.CreatedAtUtc,
                WalletCount = _dbContext.Wallets.AsNoTracking().Count(w => w.MerchantId == m.Id)
            })
            .ToListAsync(ct);

        return Ok(new PagedMerchantResultDto
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        });
    }

    [HttpGet("merchants/{merchantId:guid}")]
    public async Task<IActionResult> GetMerchantDetails(Guid merchantId, CancellationToken ct)
    {
        var merchant = await _dbContext.Merchants
            .AsNoTracking()
            .Where(m => m.Id == merchantId)
            .Select(m => new MerchantDetailsDto
            {
                MerchantId = m.Id,
                Name = m.Name,
                IsActive = m.IsActive,
                CreatedAtUtc = m.CreatedAtUtc,
                DefaultWebhookUrl = m.DefaultWebhookUrl,
                WalletCount = _dbContext.Wallets.AsNoTracking().Count(w => w.MerchantId == m.Id),
                TotalInvoiceCount = _dbContext.Invoices.AsNoTracking().Count(i => i.MerchantId == m.Id)
            })
            .FirstOrDefaultAsync(ct);

        if (merchant == null)
        {
            return NotFound(new { error = "Merchant not found." });
        }

        merchant.Wallets = await _dbContext.Wallets
            .AsNoTracking()
            .Where(w => w.MerchantId == merchantId)
            .Select(w => new MerchantWalletSummaryDto
            {
                WalletId = w.Id,
                Network = w.Network,
                TotalAddressesAllocated = w.LastDerivationIndex,
                CreatedAtUtc = w.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Ok(merchant);
    }
    
    [HttpPost("merchants/{merchantId:guid}/activate")]
    public async Task<IActionResult> ActivateMerchant(Guid merchantId, CancellationToken ct)
    {
        var merchant = await _dbContext.Merchants.FirstOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant == null) return NotFound(new { error = "Merchant not found." });

        merchant.Activate();
        await _dbContext.SaveChangesAsync(ct);

        return Ok(new { message = "Merchant activated successfully." });
    }

    [HttpPost("merchants/{merchantId:guid}/deactivate")]
    public async Task<IActionResult> DeactivateMerchant(Guid merchantId, CancellationToken ct)
    {
        var merchant = await _dbContext.Merchants.FirstOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant == null) return NotFound(new { error = "Merchant not found." });

        merchant.Deactivate();
        await _dbContext.SaveChangesAsync(ct);

        return Ok(new { message = "Merchant deactivated successfully." });
    }
}