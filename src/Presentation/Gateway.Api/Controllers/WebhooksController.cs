using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Api.Authentication;
using Gateway.Application.DTOs;
using Gateway.Domain.Entities;
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
    
    [HttpPost("test")]
    public async Task<IActionResult> TriggerTestWebhook([FromBody] TestWebhookRequest request, CancellationToken ct)
    {
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        var merchant = await _dbContext.Merchants.FirstOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant == null)
            return NotFound(new { error = "Merchant not found." });

        var destination = string.IsNullOrWhiteSpace(request.DestinationUrl)
            ? (await _dbContext.Invoices.Where(i => i.MerchantId == merchantId).OrderByDescending(i => i.CreatedAtUtc).Select(i => i.CallbackUrl).FirstOrDefaultAsync(ct) ?? "https://example.com/webhook")
            : request.DestinationUrl.Trim();

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "webhook.test",
            merchantId = merchant.Id,
            timestamp = DateTime.UtcNow,
            message = "This is a test webhook notification from Crypto Payment Gateway."
        });

        var outboxEvent = new OutboxEvent(
            aggregateId: merchant.Id,
            eventType: "webhook.test",
            payload: payload,
            destinationUrl: destination,
            secretKey: merchant.EncryptedWebhookSecret
        );

        _dbContext.OutboxEvents.Add(outboxEvent);
        await _dbContext.SaveChangesAsync(ct);

        return Ok(new TestWebhookResponse
        {
            EventId = outboxEvent.Id,
            DestinationUrl = outboxEvent.DestinationUrl,
            Status = "Queued",
            DispatchedAtUtc = outboxEvent.CreatedAtUtc
        });
    }
}