using System;

namespace Gateway.Domain.Entities;

public class OutboxEvent
{
    public Guid Id { get; private set; }
    public Guid AggregateId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public string DestinationUrl { get; private set; } = string.Empty;
    public string EncryptedSecretKey { get; private set; } = string.Empty;
    public int AttemptCount { get; private set; }
    public bool IsProcessed { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? NextAttemptAtUtc { get; private set; }
    public DateTime? LockedUntilUtc { get; private set; }
    public string? LastError { get; private set; }

    private OutboxEvent() { }

    public OutboxEvent(Guid aggregateId, string eventType, string payload, string destinationUrl, string encryptedSecretKey)
    {
        Id = Guid.NewGuid();
        AggregateId = aggregateId;
        EventType = eventType;
        Payload = payload;
        DestinationUrl = destinationUrl;
        EncryptedSecretKey = encryptedSecretKey;
        AttemptCount = 0;
        IsProcessed = false;
        CreatedAtUtc = DateTime.UtcNow;
        NextAttemptAtUtc = CreatedAtUtc;
        LockedUntilUtc = null;
    }

    public void AcquireLease(TimeSpan duration)
    {
        LockedUntilUtc = DateTime.UtcNow.Add(duration);
    }

    public void RecordFailure(string error, int maxRetries = 5)
    {
        AttemptCount++;
        LastError = error;
        LockedUntilUtc = null;

        if (AttemptCount >= maxRetries)
        {
            NextAttemptAtUtc = null;
        }
        else
        {
            var delaySeconds = Math.Pow(AttemptCount, 2) * 10;
            NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
        }
    }

    public void RecordSuccess()
    {
        IsProcessed = true;
        NextAttemptAtUtc = null;
        LockedUntilUtc = null;
        LastError = null;
    }

    public void ScheduleManualRedelivery()
    {
        if (LockedUntilUtc.HasValue && LockedUntilUtc.Value > DateTime.UtcNow)
        {
            throw new InvalidOperationException("Cannot reschedule delivery while an active processing lease is held.");
        }

        IsProcessed = false;
        NextAttemptAtUtc = DateTime.UtcNow;
        LockedUntilUtc = null;
        LastError = null;
    }
}