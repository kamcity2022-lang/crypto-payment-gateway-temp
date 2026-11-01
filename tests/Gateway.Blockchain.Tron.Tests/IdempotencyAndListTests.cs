using System;
using FluentAssertions;
using Gateway.Domain.Entities;
using Xunit;

namespace Gateway.Domain.Tests;

public class IdempotencyAndListTests
{
    [Fact]
    public void MerchantIdempotencyKey_ShouldInitializeCorrectly()
    {
        var merchantId = Guid.NewGuid();
        var key = "test-idempotency-key-123";
        var hash = "abc123hash";
        var payload = "{\"status\":\"success\"}";

        var idempotency = new MerchantIdempotencyKey(
            merchantId,
            key,
            hash,
            payload,
            201,
            TimeSpan.FromHours(1)
        );

        idempotency.MerchantId.Should().Be(merchantId);
        idempotency.Key.Should().Be(key);
        idempotency.RequestHash.Should().Be(hash);
        idempotency.ResponseStatusCode.Should().Be(201);
        idempotency.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public void MerchantIdempotencyKey_WithEmptyKey_ShouldThrowArgumentException()
    {
        var act = () => new MerchantIdempotencyKey(
            Guid.NewGuid(),
            "",
            "hash",
            "{}",
            201,
            TimeSpan.FromMinutes(10)
        );

        act.Should().Throw<ArgumentException>();
    }
}