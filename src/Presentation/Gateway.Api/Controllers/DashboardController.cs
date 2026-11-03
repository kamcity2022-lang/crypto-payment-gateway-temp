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
[Authorize(Policy = "MerchantViewer")] // Viewer can read, Owner/Admin can edit
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
        // ✅ STRICT: Only trust ResolvedMerchantId set by MerchantIdHandler (Human-User Identity path)
        // Never fall back to HMAC MerchantId here.
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
        if (!TryGetResolvedMerchantId(out var merchantId))
        {
            return Forbid(); // Blocks SuperAdmins without explicit merchant context
        }

        var now = DateTime.UtcNow;
        var overview = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.MerchantId == merchantId)
            .GroupBy(i => 1)
            .Select(g => new DashboardOverviewDto
            {
                TotalInvoices = g.Count(),
                PaidInvoices = g.Count(i => i.Status == InvoiceStatus.Paid || i.Status == InvoiceStatus.Overpaid),
                PendingInvoices = g.Count(i => i.Status == InvoiceStatus.Pending || i.Status == InvoiceStatus.PartiallyPaid),
                ExpiredOrFailedInvoices = g.Count(i => i.Status == InvoiceStatus.Expired || i.Status == InvoiceStatus.Cancelled || i.Status == InvoiceStatus.Underpaid),
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
        if (!TryGetResolvedMerchantId(out var merchantId))
        {
            return Forbid();
        }

        var profile = await _dbContext.Merchants
            .AsNoTracking()
            .Where(m => m.Id == merchantId)
            .Select(m => new MerchantProfileDto
            {
                MerchantId = m.Id,
                Name = m.Name,
                IsActive = m.IsActive,
                CreatedAtUtc = m.CreatedAtUtc,
                // Wallets are fetched separately or joined if needed, keeping it simple for MVP
            })
            .FirstOrDefaultAsync(ct);

        if (profile == null) return NotFound(new { error = "Merchant profile not found." });

        return Ok(profile);
    }

    [HttpPut("profile")]
    [Authorize(Policy = "MerchantAdmin")] // Only Admin or Owner can update profile
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateMerchantProfileRequest request, CancellationToken ct)
    {
        if (!TryGetResolvedMerchantId(out var merchantId))
        {
            return Forbid();
        }

        var merchant = await _dbContext.Merchants.FirstOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant == null) return NotFound(new { error = "Merchant not found." });

        merchant.GetType().GetProperty("Name")!.SetValue(merchant, request.Name.Trim());
        
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
}