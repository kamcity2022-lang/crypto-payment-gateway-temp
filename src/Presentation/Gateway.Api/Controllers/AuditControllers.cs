using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/dashboard/audit")]
[Authorize(Policy = "MerchantOwner")]
public sealed class MerchantAuditController : ControllerBase
{
    private readonly GatewayDbContext _db;
    public MerchantAuditController(GatewayDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!HttpContext.Items.TryGetValue("ResolvedMerchantId", out var resolved) ||
            resolved is not Guid merchantId || merchantId == Guid.Empty)
            return Forbid();

        page = Math.Clamp(page, 1, 10000);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var query = _db.AuditEntries.AsNoTracking()
            .Where(a => a.MerchantId == merchantId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(a => a.OccurredAtUtc)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new {
                a.Id, a.ActorUserId, a.Action, a.TargetType,
                a.TargetId, a.OccurredAtUtc
            }).ToListAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { items, totalCount = total, page, pageSize,
            totalPages = (int)Math.Ceiling((double)total / pageSize) });
    }
}

[ApiController]
[Route("api/v1/admin/audit")]
[Authorize(Policy = "SuperAdminOnly")]
public sealed class AdminAuditController : ControllerBase
{
    private readonly GatewayDbContext _db;
    public AdminAuditController(GatewayDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? merchantId = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 10000);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var query = _db.AuditEntries.AsNoTracking().AsQueryable();
        if (merchantId.HasValue)
            query = query.Where(a => a.MerchantId == merchantId.Value);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(a => a.OccurredAtUtc)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new {
                a.Id, a.MerchantId, a.ActorUserId, a.Action,
                a.TargetType, a.TargetId, a.OccurredAtUtc
            }).ToListAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { items, totalCount = total, page, pageSize,
            totalPages = (int)Math.Ceiling((double)total / pageSize) });
    }
}
