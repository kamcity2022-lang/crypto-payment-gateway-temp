using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Domain.Entities;
using Gateway.Infrastructure.Security;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

public sealed class UpdateWebhookSettingsRequest
{
    [Required, StringLength(2048)]
    public string WebhookUrl { get; set; } = string.Empty;
}

[ApiController]
[Route("api/v1/dashboard/webhooks")]
[Authorize(Policy = "MerchantViewer")]
public sealed class MerchantWebhookDashboardController : ControllerBase
{
    private readonly GatewayDbContext _db;

    public MerchantWebhookDashboardController(GatewayDbContext db) => _db = db;

    private bool TryMerchant(out Guid merchantId)
    {
        merchantId = Guid.Empty;
        return HttpContext.Items.TryGetValue("ResolvedMerchantId", out var value)
               && value is Guid id && (merchantId = id) != Guid.Empty;
    }

    private static bool IsSafeWebhookUrl(string url)
    {
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        return SsrfValidator.IsDestinationSafe(url, allowHttp: false);
    }

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();
        var merchant = await _db.Merchants.AsNoTracking()
            .Where(m => m.Id == merchantId)
            .Select(m => new { m.DefaultWebhookUrl })
            .SingleOrDefaultAsync(ct);
        if (merchant is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Ok(new { merchant.DefaultWebhookUrl });
    }

    [HttpPut("settings")]
    [Authorize(Policy = "MerchantAdmin")]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] UpdateWebhookSettingsRequest request, CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();
        var url = request.WebhookUrl?.Trim() ?? string.Empty;
        if (!IsSafeWebhookUrl(url))
            return BadRequest(new { error = "INVALID_WEBHOOK_DESTINATION" });

        var merchant = await _db.Merchants.SingleOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant is null) return NotFound();
        merchant.SetDefaultWebhookUrl(url);
        await _db.SaveChangesAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { merchant.DefaultWebhookUrl });
    }

    [HttpGet("events")]
    public async Task<IActionResult> GetEvents(
        [FromQuery] Guid? invoiceId, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();

        var size = Math.Clamp(pageSize, 1, 50);
        page = Math.Clamp(page, 1, 10000);
        var query = _db.OutboxEvents.AsNoTracking().Where(e =>
            (e.EventType == "webhook.test" && e.AggregateId == merchantId)
            || _db.Invoices.Any(i => i.Id == e.AggregateId && i.MerchantId == merchantId));

        if (invoiceId.HasValue)
            query = query.Where(e => e.AggregateId == invoiceId.Value &&
                                     e.EventType != "webhook.test");

        switch (status?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "all":
                break;
            case "delivered":
                query = query.Where(e => e.IsProcessed);
                break;
            case "queued":
                query = query.Where(e => !e.IsProcessed && e.AttemptCount == 0 &&
                                         e.NextAttemptAtUtc != null);
                break;
            case "retry":
                query = query.Where(e => !e.IsProcessed && e.AttemptCount > 0 &&
                                         e.NextAttemptAtUtc != null);
                break;
            case "exhausted":
                query = query.Where(e => !e.IsProcessed && e.NextAttemptAtUtc == null);
                break;
            default:
                return BadRequest(new { error = "INVALID_WEBHOOK_STATUS_FILTER" });
        }

        var total = await query.CountAsync(ct);
        var events = await query.OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(e => new
            {
                eventId = e.Id,
                invoiceId = e.EventType == "webhook.test" ? (Guid?)null : e.AggregateId,
                e.EventType,
                e.DestinationUrl,
                e.AttemptCount,
                e.IsProcessed,
                e.LastError,
                e.CreatedAtUtc,
                e.NextAttemptAtUtc,
                e.LockedUntilUtc,
                status = e.IsProcessed ? "Delivered" :
                    e.NextAttemptAtUtc == null ? "Exhausted" :
                    e.AttemptCount == 0 ? "Queued" : "Retry"
            })
            .ToListAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            items = events,
            page,
            pageSize = size,
            totalCount = total,
            totalPages = (int)Math.Ceiling((double)total / size)
        });
    }

    [HttpPost("test")]
    [Authorize(Policy = "MerchantAdmin")]
    public async Task<IActionResult> QueueTest(CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();
        var merchant = await _db.Merchants.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant is null) return NotFound();
        var url = merchant.DefaultWebhookUrl;
        if (string.IsNullOrEmpty(url) || !IsSafeWebhookUrl(url))
            return BadRequest(new { error = "DEFAULT_WEBHOOK_NOT_CONFIGURED" });

        var payload = JsonSerializer.Serialize(new
        {
            @event = "webhook.test",
            merchantId,
            timestamp = DateTime.UtcNow,
            message = "Test delivery from Crypto Payment Gateway."
        });
        var evt = new OutboxEvent(merchantId, "webhook.test", payload, url,
            merchant.EncryptedWebhookSecret);
        _db.OutboxEvents.Add(evt);
        await _db.SaveChangesAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return Accepted(new { eventId = evt.Id, status = "Queued" });
    }

    [HttpPost("events/{eventId:guid}/redeliver")]
    [Authorize(Policy = "MerchantAdmin")]
    public async Task<IActionResult> Redeliver(Guid eventId, CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();
        // The authorization predicate handles regular invoice events as well
        // as test events (whose aggregate ID is the Merchant ID itself).
        var owned = await _db.OutboxEvents.AsNoTracking().AnyAsync(e =>
            e.Id == eventId &&
            ((e.EventType == "webhook.test" && e.AggregateId == merchantId)
              || _db.Invoices.Any(i => i.Id == e.AggregateId &&
                                          i.MerchantId == merchantId)), ct);
        if (!owned) return NotFound(new { error = "WEBHOOK_EVENT_NOT_FOUND" });

        // Atomic compare-and-set avoids races with the Dispatcher and prevents
        // scheduling duplicate redelivery while a job is queued/in-flight.
        var now = DateTime.UtcNow;
        var updated = await _db.OutboxEvents
            .Where(e => e.Id == eventId &&
                        (e.LockedUntilUtc == null || e.LockedUntilUtc <= now) &&
                        (e.IsProcessed || e.NextAttemptAtUtc == null))
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.IsProcessed, false)
                .SetProperty(e => e.NextAttemptAtUtc, now)
                .SetProperty(e => e.LockedUntilUtc, (DateTime?)null)
                .SetProperty(e => e.LastError, (string?)null), ct);

        if (updated != 1)
            return Conflict(new { error = "WEBHOOK_ALREADY_QUEUED_OR_IN_FLIGHT" });

        Response.Headers.CacheControl = "no-store";
        return Accepted(new { eventId, status = "Queued" });
    }
}
