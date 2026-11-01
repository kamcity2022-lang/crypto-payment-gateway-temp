using System;

namespace Gateway.Domain.Entities;

public class OutboxEvent
{
    public Guid Id { get; private set; }
    public Guid AggregateId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public string DestinationUrl { get; private set; } = string.Empty;
    public string SecretKey { get; private set; } = string.Empty;
    public int AttemptCount { get; private set; }
    public bool IsProcessed { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? NextAttemptAtUtc { get; private set; }
    public string? LastError { get; private set; }

    private OutboxEvent() { }

    public OutboxEvent(Guid aggregateId, string eventType, string payload, string destinationUrl, string secretKey)
    {
        Id = Guid.NewGuid();
        AggregateId = aggregateId;
        EventType = eventType;
        Payload = payload;
        DestinationUrl = destinationUrl;
        SecretKey = secretKey;
        AttemptCount = 0;
        IsProcessed = false;
        CreatedAtUtc = DateTime.UtcNow;
        NextAttemptAtUtc = CreatedAtUtc;
    }

    public void RecordFailure(string error, int maxRetries = 5)
    {
        AttemptCount++;
        LastError = error;

        if (AttemptCount >= maxRetries)
        {
            // Abandon after reaching maximum attempts
            NextAttemptAtUtc = null;
        }
        else
        {
            // Exponential backoff: 10s, 40s, 90s, 160s...
            var delaySeconds = Math.Pow(AttemptCount, 2) * 10;
            NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
        }
    }

    public void RecordSuccess()
    {
        IsProcessed = true;
        NextAttemptAtUtc = null;
        LastError = null;
    }
    
    public void ScheduleManualRedelivery()
    {
        IsProcessed = false;
        NextAttemptAtUtc = DateTime.UtcNow;
        LastError = null;
    }
}