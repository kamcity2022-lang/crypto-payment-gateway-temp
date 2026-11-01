using System;
using FluentAssertions;
using Gateway.Domain.Entities;
using Xunit;

namespace Gateway.Domain.Tests;

public class WebhookManagementTests
{
    [Fact]
    public void ScheduleManualRedelivery_ShouldResetProcessedStateAndScheduleImmediately()
    {
        var invoiceId = Guid.NewGuid();
        var outboxEvent = new OutboxEvent(
            aggregateId: invoiceId,
            eventType: "invoice.paid",
            payload: "{}",
            destinationUrl: "https://merchant.com/webhook",
            encryptedSecretKey: "enc-key"
        );

        // شبیه‌سازی وضعیت شکست در حداکثر تلاش‌ها
        outboxEvent.RecordFailure("Destination unreachable", maxRetries: 1);
        outboxEvent.NextAttemptAtUtc.Should().BeNull();
        outboxEvent.LastError.Should().NotBeNull();

        // اجرای درخواست ارسال مجدد دستی
        outboxEvent.ScheduleManualRedelivery();

        outboxEvent.IsProcessed.Should().BeFalse();
        outboxEvent.NextAttemptAtUtc.Should().NotBeNull();
        outboxEvent.NextAttemptAtUtc.Should().BeOnOrBefore(DateTime.UtcNow.AddSeconds(1));
        outboxEvent.LastError.Should().BeNull();
    }
}