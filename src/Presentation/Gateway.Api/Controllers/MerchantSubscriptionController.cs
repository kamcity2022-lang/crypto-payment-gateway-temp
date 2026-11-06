using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.DTOs;
using Gateway.Domain.Enums;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/dashboard/subscription")]
[Authorize(Policy = "MerchantViewer")]
public sealed class MerchantSubscriptionController : ControllerBase
{
    private readonly GatewayDbContext _db;
    public MerchantSubscriptionController(GatewayDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetMine(CancellationToken ct)
    {
        // MerchantIdHandler resolves the tenant; do not accept a merchant ID
        // from query string or the request body.
        if (!HttpContext.Items.TryGetValue("ResolvedMerchantId", out var resolved) ||
            resolved is not Guid merchantId)
            return Forbid();

        var subscription = await _db.Subscriptions.AsNoTracking()
            .Include(s => s.Plan)
            .Where(s => s.MerchantId == merchantId)
            .OrderByDescending(s => s.Status == SubscriptionStatus.Active)
            .ThenByDescending(s => s.CreatedAtUtc)
            .ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

        return subscription is null
            ? NotFound(new { error = "SUBSCRIPTION_NOT_FOUND" })
            : Ok(SubscriptionResponseDto.From(subscription, subscription.Plan));
    }
}
