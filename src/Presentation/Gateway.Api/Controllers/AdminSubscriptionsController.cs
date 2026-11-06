using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Api.Auditing;
using Gateway.Application.DTOs;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/admin/merchants/{merchantId:guid}/subscription")]
[Authorize(Policy = "SuperAdminOnly")]
public sealed class AdminSubscriptionsController : ControllerBase
{
    private readonly GatewayDbContext _db;

    public AdminSubscriptionsController(GatewayDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetSubscription(Guid merchantId, CancellationToken ct)
    {
        if (!await _db.Merchants.AnyAsync(m => m.Id == merchantId, ct))
            return NotFound(new { error = "MERCHANT_NOT_FOUND" });

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

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(Guid merchantId, CancellationToken ct)
    {
        if (!await _db.Merchants.AnyAsync(m => m.Id == merchantId, ct))
            return NotFound(new { error = "MERCHANT_NOT_FOUND" });

        var subscriptions = await _db.Subscriptions.AsNoTracking()
            .Include(s => s.Plan)
            .Where(s => s.MerchantId == merchantId)
            .OrderByDescending(s => s.Status == SubscriptionStatus.Active)
            .ThenByDescending(s => s.CreatedAtUtc)
            .ThenByDescending(s => s.Id)
            .ToListAsync(ct);

        return Ok(subscriptions.Select(s => SubscriptionResponseDto.From(s, s.Plan)).ToList());
    }

    [HttpPost("assign")]
    public async Task<IActionResult> Assign(
        Guid merchantId, [FromBody] AssignSubscriptionRequest request, CancellationToken ct)
    {
        if (request.PlanId == Guid.Empty)
            return BadRequest(new { error = "PLAN_ID_REQUIRED" });

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // Locking the merchant serializes assignment, cancellation and plan
        // changes, even when no active subscription row exists yet.
        var merchant = await LockMerchantAsync(merchantId, ct);
        if (merchant is null)
            return NotFound(new { error = "MERCHANT_NOT_FOUND" });

        if (await _db.Subscriptions.AnyAsync(
                s => s.MerchantId == merchantId && s.Status == SubscriptionStatus.Active, ct))
            return Conflict(new { error = "ACTIVE_SUBSCRIPTION_EXISTS" });

        var plan = await LockPlanForReadAsync(request.PlanId, ct);
        if (plan is null) return NotFound(new { error = "PLAN_NOT_FOUND" });
        if (!plan.IsActive) return Conflict(new { error = "PLAN_NOT_ACTIVE" });

        // A new active subscription is intentional only after a previous
        // subscription was cancelled or marked PastDue; history is retained.
        var subscription = new Subscription(merchantId, plan.Id);
        _db.Subscriptions.Add(subscription);
        AuditTrail.Record(_db, User, merchantId, "subscription.assigned", "Subscription", subscription.Id.ToString());
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return CreatedAtAction(nameof(GetSubscription), new { merchantId },
            SubscriptionResponseDto.From(subscription, plan));
    }

    [HttpPut("plan")]
    public async Task<IActionResult> ChangePlan(
        Guid merchantId, [FromBody] AssignSubscriptionRequest request, CancellationToken ct)
    {
        if (request.PlanId == Guid.Empty)
            return BadRequest(new { error = "PLAN_ID_REQUIRED" });

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (await LockMerchantAsync(merchantId, ct) is null)
            return NotFound(new { error = "MERCHANT_NOT_FOUND" });

        var subscription = await LockActiveSubscriptionAsync(merchantId, ct);
        if (subscription is null)
            return Conflict(new { error = "ACTIVE_SUBSCRIPTION_REQUIRED" });

        var plan = await LockPlanForReadAsync(request.PlanId, ct);
        if (plan is null) return NotFound(new { error = "PLAN_NOT_FOUND" });
        if (!plan.IsActive) return Conflict(new { error = "PLAN_NOT_ACTIVE" });

        // Keep the same subscription, period and consumed usage. Switching
        // plans must never give the merchant free quota by resetting counters.
        // Explicit merchant-specific limits remain until cleared separately.
        subscription.ChangePlan(plan.Id);
        AuditTrail.Record(_db, User, merchantId, "subscription.plan_changed", "Subscription", subscription.Id.ToString());
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(SubscriptionResponseDto.From(subscription, plan));
    }

    [HttpPut("limits")]
    public async Task<IActionResult> SetLimits(
        Guid merchantId, [FromBody] OverrideLimitsRequest request, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (await LockMerchantAsync(merchantId, ct) is null)
            return NotFound(new { error = "MERCHANT_NOT_FOUND" });

        var subscription = await LockActiveSubscriptionAsync(merchantId, ct);
        if (subscription is null)
            return Conflict(new { error = "ACTIVE_SUBSCRIPTION_REQUIRED" });

        subscription.OverrideLimits(request.CustomInvoiceLimit, request.CustomVolumeLimitUsd);
        AuditTrail.Record(_db, User, merchantId, "subscription.limits_changed", "Subscription", subscription.Id.ToString());
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var plan = await _db.Plans.AsNoTracking().SingleAsync(p => p.Id == subscription.PlanId, ct);
        return Ok(SubscriptionResponseDto.From(subscription, plan));
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel(Guid merchantId, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (await LockMerchantAsync(merchantId, ct) is null)
            return NotFound(new { error = "MERCHANT_NOT_FOUND" });

        var subscription = await LockActiveSubscriptionAsync(merchantId, ct);
        if (subscription is null)
            return Conflict(new { error = "ACTIVE_SUBSCRIPTION_REQUIRED" });

        subscription.Cancel();
        AuditTrail.Record(_db, User, merchantId, "subscription.cancelled", "Subscription", subscription.Id.ToString());
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var plan = await _db.Plans.AsNoTracking().SingleAsync(p => p.Id == subscription.PlanId, ct);
        return Ok(SubscriptionResponseDto.From(subscription, plan));
    }

    private Task<Merchant?> LockMerchantAsync(Guid merchantId, CancellationToken ct) =>
        _db.Merchants.FromSqlInterpolated(
            $"SELECT * FROM \"Merchants\" WHERE \"Id\" = {merchantId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

    private Task<Subscription?> LockActiveSubscriptionAsync(Guid merchantId, CancellationToken ct) =>
        _db.Subscriptions.FromSqlInterpolated(
            $"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
            .FirstOrDefaultAsync(ct);

    private Task<Plan?> LockPlanForReadAsync(Guid planId, CancellationToken ct) =>
        _db.Plans.FromSqlInterpolated(
            $"SELECT * FROM \"Plans\" WHERE \"Id\" = {planId} FOR SHARE")
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
}
