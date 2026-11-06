using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Api.Controllers;
using Gateway.Application.DTOs;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Gateway.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

// These are controller + real PostgreSQL integration tests, not HTTP/HMAC
// tests. Every invocation gets its own scoped-like DbContext and controller.
public class InvoiceQuotaIntegrationTests : IAsyncLifetime
{
    private const string ValidAccountXPub =
        "xpub6BosfCnifzxcFwrSzQiqu2DBVTshkCXacvNsWGYJVVhhawA7d4R5WSWGFNbi8Aw6ZRc1brxMyWMzG3DSSSSoekkudhUd9yLb6qx39T9nMdj";

    // Literal public IP avoids external DNS and is permitted by SsrfValidator.
    private const string SafeCallbackUrl = "https://1.1.1.1/callback";

    // Separate PUBLIC account xpub: distinct merchants must not allocate the same address.
    private const string OtherAccountXPub =
        "xpub6BnXfs8uoXsCzAVmorN7UqyoveKnxqtvvf6BXT9NBpeHxvFBiYwb5VZxN4WkqmJrrxdFn8PrYBnJbuKGZyGPVoyYruJyCuNT8y28av4EpL2";

    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_quota_integration_db")
        .WithUsername("postgres")
        .WithPassword("postgres_test_password")
        .Build();

    private GatewayDbContext _seedDb = null!;
    private Guid _merchantId;
    private Guid _planId;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        _seedDb = new GatewayDbContext(CreateOptions());
        await _seedDb.Database.MigrateAsync();

        _merchantId = Guid.NewGuid();
        var merchant = new Merchant(
            _merchantId,
            "Quota Test Merchant",
            "hash_" + Guid.NewGuid().ToString("N"),
            "test-api-secret",
            "test-webhook-secret");
        var wallet = new Wallet(_merchantId, ValidAccountXPub, "TRON");
        var plan = new Plan("Test Plan", "Test quota plan", 2, 200m, 0m);
        var subscription = new Subscription(_merchantId, plan.Id);
        _planId = plan.Id;

        _seedDb.Merchants.Add(merchant);
        _seedDb.Wallets.Add(wallet);
        _seedDb.Plans.Add(plan);
        _seedDb.Subscriptions.Add(subscription);
        await _seedDb.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_seedDb is not null)
        {
            await _seedDb.DisposeAsync();
        }
        await _dbContainer.DisposeAsync();
    }

    private DbContextOptions<GatewayDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;

    private static CreateInvoiceRequest CreateRequest(decimal amount = 50m) => new()
    {
        FiatAmount = amount,
        FiatCurrency = "USD",
        CallbackUrl = SafeCallbackUrl,
        ExpiryMinutes = 30
    };

    private async Task<IActionResult> SendAsync(
        CreateInvoiceRequest request,
        string? idempotencyKey = null,
        Guid? merchantId = null)
    {
        await using var db = new GatewayDbContext(CreateOptions());
        var controller = new InvoicesController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        // Authentication itself isn't under test; the action filter normally
        // places this trusted ID here after validating the HMAC signature.
        controller.HttpContext.Items["MerchantId"] = merchantId ?? _merchantId;
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("localhost");
        if (idempotencyKey is not null)
        {
            controller.Request.Headers["Idempotency-Key"] = idempotencyKey;
        }

        return await controller.CreateInvoice(request, CancellationToken.None);
    }

    private async Task<(int Count, decimal Volume, int InvoiceCount)> ReadUsageAsync(Guid? merchantId = null)
    {
        var id = merchantId ?? _merchantId;
        await using var db = new GatewayDbContext(CreateOptions());
        var subscription = await db.Subscriptions.AsNoTracking()
            .SingleAsync(s => s.MerchantId == id);
        var invoiceCount = await db.Invoices.AsNoTracking()
            .CountAsync(i => i.MerchantId == id);

        return (subscription.CurrentPeriodInvoiceCount,
                subscription.CurrentPeriodVolumeUsd,
                invoiceCount);
    }

    private static void ShouldHaveError(IActionResult result, int statusCode, string code)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(statusCode);
        JsonSerializer.Serialize(objectResult.Value).Should().Contain($"\"error\":\"{code}\"");
    }

    [Fact]
    public async Task CreateInvoice_Success_IncrementsQuotaAndPersistsInvoice()
    {
        var result = await SendAsync(CreateRequest(50m), "success-1");
        result.Should().BeOfType<CreatedAtActionResult>();

        var usage = await ReadUsageAsync();
        usage.Count.Should().Be(1);
        usage.Volume.Should().Be(50m);
        usage.InvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateInvoice_CountLimit_Returns403WithoutIncrement()
    {
        (await SendAsync(CreateRequest(10m), "count-1"))
            .Should().BeOfType<CreatedAtActionResult>();
        (await SendAsync(CreateRequest(10m), "count-2"))
            .Should().BeOfType<CreatedAtActionResult>();

        var rejected = await SendAsync(CreateRequest(10m), "count-3");
        ShouldHaveError(rejected, 403, "SUBSCRIPTION_INVOICE_LIMIT_REACHED");

        var usage = await ReadUsageAsync();
        usage.Count.Should().Be(2);
        usage.Volume.Should().Be(20m);
        usage.InvoiceCount.Should().Be(2);
    }

    [Fact]
    public async Task CreateInvoice_VolumeLimit_Returns403WithoutIncrement()
    {
        (await SendAsync(CreateRequest(150m), "volume-1"))
            .Should().BeOfType<CreatedAtActionResult>();

        var rejected = await SendAsync(CreateRequest(60m), "volume-2");
        ShouldHaveError(rejected, 403, "SUBSCRIPTION_VOLUME_LIMIT_REACHED");

        var usage = await ReadUsageAsync();
        usage.Count.Should().Be(1);
        usage.Volume.Should().Be(150m);
        usage.InvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateInvoice_ExactlyAtVolumeLimit_Succeeds()
    {
        (await SendAsync(CreateRequest(150m), "volume-equal-1"))
            .Should().BeOfType<CreatedAtActionResult>();
        (await SendAsync(CreateRequest(50m), "volume-equal-2"))
            .Should().BeOfType<CreatedAtActionResult>();

        var usage = await ReadUsageAsync();
        usage.Count.Should().Be(2);
        usage.Volume.Should().Be(200m);
        usage.InvoiceCount.Should().Be(2);
    }

    [Fact]
    public async Task CreateInvoice_CustomOverride_TakesPrecedence()
    {
        var subscription = await _seedDb.Subscriptions.SingleAsync(s => s.MerchantId == _merchantId);
        subscription.OverrideLimits(1, 25m);
        await _seedDb.SaveChangesAsync();

        (await SendAsync(CreateRequest(25m), "override-1"))
            .Should().BeOfType<CreatedAtActionResult>();
        ShouldHaveError(await SendAsync(CreateRequest(1m), "override-2"),
            403, "SUBSCRIPTION_INVOICE_LIMIT_REACHED");

        var usage = await ReadUsageAsync();
        usage.Count.Should().Be(1);
        usage.Volume.Should().Be(25m);
        usage.InvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateInvoice_SameKeyReplay_ReturnsOriginal201AndNoAdditionalUsage()
    {
        var request = CreateRequest(50m);
        var first = await SendAsync(request, "replay-key");
        var created = first.Should().BeOfType<CreatedAtActionResult>().Subject;
        var original = created.Value.Should().BeOfType<InvoiceResponseDto>().Subject;

        var replay = await SendAsync(request, "replay-key");
        var cached = replay.Should().BeOfType<ContentResult>().Subject;
        cached.StatusCode.Should().Be(201);
        var replayed = JsonSerializer.Deserialize<InvoiceResponseDto>(cached.Content!);
        replayed.Should().NotBeNull();
        replayed!.InvoiceId.Should().Be(original.InvoiceId);

        var usage = await ReadUsageAsync();
        usage.Count.Should().Be(1);
        usage.Volume.Should().Be(50m);
        usage.InvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateInvoice_ReplaySurvivesSubscriptionDeactivation()
    {
        var request = CreateRequest(40m);
        (await SendAsync(request, "replay-disabled"))
            .Should().BeOfType<CreatedAtActionResult>();

        // Existing successful requests should remain replayable even after
        // this merchant no longer has an active subscription.
        await _seedDb.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Subscriptions\" SET \"Status\" = {(int)SubscriptionStatus.Cancelled} WHERE \"MerchantId\" = {_merchantId}");

        var replay = await SendAsync(request, "replay-disabled");
        replay.Should().BeOfType<ContentResult>()
            .Which.StatusCode.Should().Be(201);

        ShouldHaveError(await SendAsync(CreateRequest(5m), "new-disabled"),
            403, "SUBSCRIPTION_NOT_ACTIVE");
    }

    [Fact]
    public async Task CreateInvoice_SameKeyDifferentPayload_Returns409()
    {
        (await SendAsync(CreateRequest(20m), "same-key"))
            .Should().BeOfType<CreatedAtActionResult>();
        var conflict = await SendAsync(CreateRequest(21m), "same-key");
        conflict.Should().BeOfType<ConflictObjectResult>();

        var usage = await ReadUsageAsync();
        usage.Count.Should().Be(1);
        usage.InvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateInvoice_ExpiredKeyCanBeReusedAtomically()
    {
        _seedDb.MerchantIdempotencyKeys.Add(new MerchantIdempotencyKey(
            _merchantId, "expired-key", "old-request-hash", "{}", 201,
            TimeSpan.FromHours(-1)));
        await _seedDb.SaveChangesAsync();

        var created = await SendAsync(CreateRequest(25m), "expired-key");
        created.Should().BeOfType<CreatedAtActionResult>();

        await using var verifyDb = new GatewayDbContext(CreateOptions());
        var reused = await verifyDb.MerchantIdempotencyKeys.AsNoTracking()
            .SingleAsync(k => k.MerchantId == _merchantId && k.Key == "expired-key");
        reused.RequestHash.Should().NotBe("old-request-hash");
        reused.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
        reused.ResponseStatusCode.Should().Be(201);

        var usage = await ReadUsageAsync();
        usage.Count.Should().Be(1);
        usage.InvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateInvoice_ConcurrentDistinctKeys_NeverExceedsQuota()
    {
        // Each request has its own DbContext. Task.WhenAll truly overlaps
        // execution; the PostgreSQL row lock serializes the writes.
        var tasks = Enumerable.Range(0, 5)
            .Select(i => SendAsync(CreateRequest(10m), $"concurrent-{i}"));
        var results = await Task.WhenAll(tasks);

        results.Count(r => r is CreatedAtActionResult).Should().Be(2);
        results.Count(r => r is ObjectResult { StatusCode: 403 }).Should().Be(3);
        foreach (var rejected in results.Where(r => r is ObjectResult { StatusCode: 403 }))
        {
            ShouldHaveError(rejected, 403, "SUBSCRIPTION_INVOICE_LIMIT_REACHED");
        }

        var usage = await ReadUsageAsync();
        usage.Count.Should().Be(2);
        usage.Volume.Should().Be(20m);
        usage.InvoiceCount.Should().Be(2);
    }

    [Fact]
    public async Task CreateInvoice_ConcurrentIdenticalKey_CreatesOnlyOneInvoice()
    {
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => SendAsync(CreateRequest(50m), "same-concurrent-key"));
        var results = await Task.WhenAll(tasks);

        results.Count(r => r is CreatedAtActionResult).Should().Be(1);
        results.Count(r => r is ContentResult { StatusCode: 201 }).Should().Be(4);

        var usage = await ReadUsageAsync();
        usage.Count.Should().Be(1);
        usage.Volume.Should().Be(50m);
        usage.InvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateInvoice_FailureAfterUsageIncrement_RollsBackEverything()
    {
        // A broken xpub forces an exception *after* RecordInvoiceCreation.
        // No invoice, allocated address or quota update may be committed.
        await _seedDb.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Wallets\" SET \"AccountXPub\" = {"invalid-xpub"} WHERE \"MerchantId\" = {_merchantId}");

        Func<Task> action = async () =>
        {
            await SendAsync(CreateRequest(25m), "failing-key");
        };
        await action.Should().ThrowAsync<Exception>();

        var usage = await ReadUsageAsync();
        usage.Count.Should().Be(0);
        usage.Volume.Should().Be(0m);
        usage.InvoiceCount.Should().Be(0);

        await using var verifyDb = new GatewayDbContext(CreateOptions());
        (await verifyDb.MerchantIdempotencyKeys.CountAsync(k =>
            k.MerchantId == _merchantId && k.Key == "failing-key")).Should().Be(0);
    }

    [Fact]
    public async Task CreateInvoice_TenantUsageIsIsolated()
    {
        var otherMerchantId = Guid.NewGuid();
        _seedDb.Merchants.Add(new Merchant(otherMerchantId, "Other Merchant",
            "hash_" + Guid.NewGuid().ToString("N"), "other-secret", "other-webhook"));
        _seedDb.Wallets.Add(new Wallet(otherMerchantId, OtherAccountXPub, "TRON"));
        _seedDb.Subscriptions.Add(new Subscription(otherMerchantId, _planId));
        await _seedDb.SaveChangesAsync();

        (await SendAsync(CreateRequest(20m), "tenant-one"))
            .Should().BeOfType<CreatedAtActionResult>();
        (await SendAsync(CreateRequest(60m), "tenant-two", otherMerchantId))
            .Should().BeOfType<CreatedAtActionResult>();

        var first = await ReadUsageAsync();
        var second = await ReadUsageAsync(otherMerchantId);
        first.Count.Should().Be(1);
        first.Volume.Should().Be(20m);
        first.InvoiceCount.Should().Be(1);
        second.Count.Should().Be(1);
        second.Volume.Should().Be(60m);
        second.InvoiceCount.Should().Be(1);
    }
}
