using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.DTOs;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/admin/plans")]
[Authorize(Policy = "SuperAdminOnly")]
public sealed class AdminPlansController : ControllerBase
{
    private readonly GatewayDbContext _db;

    // Shared transaction-scoped lock for all writes affecting the default plan.
    // Preserve the database partial unique index on IsDefault.
    private const string DefaultPlanLockSql = "SELECT pg_advisory_xact_lock(6238975401203874)";

    public AdminPlansController(GatewayDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetPlans(CancellationToken ct)
    {
        var plans = await _db.Plans.AsNoTracking()
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);
        return Ok(plans.Select(ToResponse).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetPlan(Guid id, CancellationToken ct)
    {
        var plan = await _db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return plan is null ? NotFound(new { error = "PLAN_NOT_FOUND" }) : Ok(ToResponse(plan));
    }

    [HttpPost]
    public async Task<IActionResult> CreatePlan([FromBody] CreatePlanRequest request, CancellationToken ct)
    {
        var plan = new Plan(request.Name, request.Description ?? string.Empty,
            request.MonthlyInvoiceLimit, request.MonthlyVolumeLimitUsd,
            request.PriceUsd, isDefault: request.IsDefault);

        if (!request.IsDefault)
        {
            _db.Plans.Add(plan);
            await _db.SaveChangesAsync(ct);
            return CreatedAtAction(nameof(GetPlan), new { id = plan.Id }, ToResponse(plan));
        }

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockDefaultPlanAsync(ct);
        // Clear the old default *before* inserting the new one.
        await _db.Plans.Where(p => p.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false), ct);
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return CreatedAtAction(nameof(GetPlan), new { id = plan.Id }, ToResponse(plan));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdatePlan(Guid id, [FromBody] UpdatePlanRequest request, CancellationToken ct)
    {
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return NotFound(new { error = "PLAN_NOT_FOUND" });

        plan.Update(request.Name, request.Description ?? string.Empty,
            request.MonthlyInvoiceLimit, request.MonthlyVolumeLimitUsd, request.PriceUsd);
        await _db.SaveChangesAsync(ct);
        return Ok(ToResponse(plan));
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> ActivatePlan(Guid id, CancellationToken ct)
    {
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return NotFound(new { error = "PLAN_NOT_FOUND" });
        plan.Activate();
        await _db.SaveChangesAsync(ct);
        return Ok(ToResponse(plan));
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> DeactivatePlan(Guid id, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockDefaultPlanAsync(ct);
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return NotFound(new { error = "PLAN_NOT_FOUND" });
        if (plan.IsDefault)
            return Conflict(new {
                error = "DEFAULT_PLAN_CANNOT_BE_DEACTIVATED",
                message = "Select another default plan before deactivating this plan."
            });

        plan.Deactivate();
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // Existing subscriptions deliberately remain untouched.
        return Ok(ToResponse(plan));
    }

    [HttpPost("{id:guid}/set-default")]
    public async Task<IActionResult> SetDefaultPlan(Guid id, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockDefaultPlanAsync(ct);
        var target = await _db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (target is null) return NotFound(new { error = "PLAN_NOT_FOUND" });
        if (!target.IsActive)
            return Conflict(new {
                error = "INACTIVE_PLAN_CANNOT_BE_DEFAULT",
                message = "Activate the plan before setting it as default."
            });

        if (!target.IsDefault)
        {
            // ExecuteUpdate is immediate. Only the target plan is tracked here.
            await _db.Plans.Where(p => p.IsDefault && p.Id != id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false), ct);
            target.SetAsDefault(true);
            await _db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Ok(ToResponse(target));
    }

    private Task<int> LockDefaultPlanAsync(CancellationToken ct) =>
        _db.Database.ExecuteSqlRawAsync(DefaultPlanLockSql, ct);

    private static PlanResponseDto ToResponse(Plan p) => new()
    {
        Id = p.Id, Name = p.Name, Description = p.Description,
        MonthlyInvoiceLimit = p.MonthlyInvoiceLimit,
        MonthlyVolumeLimitUsd = p.MonthlyVolumeLimitUsd,
        PriceUsd = p.PriceUsd, IsActive = p.IsActive,
        IsDefault = p.IsDefault, CreatedAtUtc = p.CreatedAtUtc
    };
}
