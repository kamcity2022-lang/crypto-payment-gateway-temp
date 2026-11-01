using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Api.Authentication;
using Gateway.Application.DTOs;
using Gateway.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/webhooks")]
[RequireMerchantSignature]
public class WebhooksController : ControllerBase
{
    private readonly GatewayDbContext _dbContext;

    public WebhooksController(GatewayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("invoices/{invoiceId:guid}")]
    public async Task<IActionResult> GetInvoiceWebhooks(Guid invoiceId, CancellationToken ct)
    {
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        // تایید تعلق فاکتور به مرچنت درخواست‌دهنده
        var invoiceBelongsToMerchant = await _dbContext.Invoices
            .AnyAsync(i => i.Id == invoiceId && i.MerchantId == merchantId, ct);

        if (!invoiceBelongsToMerchant)
            return NotFound(new { error = "Invoice not found or unauthorized access." });

        var events = await _dbContext.OutboxEvents
            .AsNoTracking()
            .Where(e => e.AggregateId == invoiceId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Select(e => new WebhookEventDto
            {
                EventId = e.Id,
                InvoiceId = e.AggregateId,
                EventType = e.EventType,
                DestinationUrl = e.DestinationUrl,
                Payload = e.Payload,
                AttemptCount = e.AttemptCount,
                IsProcessed = e.IsProcessed,
                LastError = e.LastError,
                CreatedAtUtc = e.CreatedAtUtc,
                NextAttemptAtUtc = e.NextAttemptAtUtc
            })
            .ToListAsync(ct);

        return Ok(events);
    }

    [HttpPost("events/{eventId:guid}/redeliver")]
    public async Task<IActionResult> RedeliverEvent(Guid eventId, CancellationToken ct)
    {
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        var outboxEvent = await _dbContext.OutboxEvents
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

        if (outboxEvent == null)
            return NotFound(new { error = "Webhook event not found." });

        // تایید دسترسی مرچنت بر پایه فاکتور مربوطه
        var invoiceBelongsToMerchant = await _dbContext.Invoices
            .AnyAsync(i => i.Id == outboxEvent.AggregateId && i.MerchantId == merchantId, ct);

        if (!invoiceBelongsToMerchant)
            return Unauthorized(new { error = "You do not have permission to redeliver this event." });

        // بازنشانی وضعیت تلاش برای مخابره فوری توسط Worker
        outboxEvent.ScheduleManualRedelivery();
        await _dbContext.SaveChangesAsync(ct);

        return Ok(new RedeliverWebhookResponse
        {
            EventId = outboxEvent.Id,
            Message = "Webhook redelivery successfully scheduled.",
            ScheduledAtUtc = outboxEvent.NextAttemptAtUtc!.Value
        });
    }
}