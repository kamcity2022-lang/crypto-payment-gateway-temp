using System;

namespace Gateway.Application.DTOs;

public class WebhookEventDto
{
    public Guid EventId { get; set; }
    public Guid InvoiceId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string DestinationUrl { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public bool IsProcessed { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
}

public class RedeliverWebhookResponse
{
    public Guid EventId { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime ScheduledAtUtc { get; set; }
}