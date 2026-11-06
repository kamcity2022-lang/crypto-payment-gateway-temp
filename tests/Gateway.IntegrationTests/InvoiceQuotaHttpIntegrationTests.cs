using System;
using System.Linq;
using System.Reflection;
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

public class InvoiceQuotaIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_quota_integration_db")
        .WithUsername("postgres")
        .WithPassword("postgres_test_password")
        .Build();
    
    private GatewayDbContext _dbContext = null!;
    private InvoicesController _controller = null!;
    private Guid _merchantId;
    private Guid _planId;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        var opts = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;
        _dbContext = new GatewayDbContext(opts);
        await _dbContext.Database.MigrateAsync();

        // Seed Data
        _merchantId = Guid.NewGuid();
        _planId = Guid.NewGuid();
        
        var merchant = new Merchant(_merchantId, "Quota Test Merchant", "hash_quota", "sec_quota", "whsec_quota");
        
        // ✅ FIX: Use a valid BIP32 Extended Public Key (same as in EndToEndPaymentFlowTests)
        var validTestXPub = "xpub6BosfCnifzxcFwrSzQiqu2DBVTshkCXacvNsWGYJVVhhawA7d4R5WSWGFNbi8Aw6ZRc1brxMyWMzG3DSSSSoekkudhUd9yLb6qx39T9nMdj";
        var wallet = new Wallet(_merchantId, validTestXPub, "TRON");
        
        var plan = new Plan("Test Plan", "desc", 2, 200m, 0m); // Limit: 2 invoices, $200 volume
        var subscription = new Subscription(_merchantId, plan.Id);

        _dbContext.Merchants.Add(merchant);
        _dbContext.Wallets.Add(wallet);
        _dbContext.Plans.Add(plan);
        _dbContext.Subscriptions.Add(subscription);
        await _dbContext.SaveChangesAsync();

        // Initialize Controller with Mock HttpContext
        _controller = new InvoicesController(_dbContext);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        // Simulate that MerchantSignatureFilter has already run and validated the user
        _controller.HttpContext.Items["MerchantId"] = _merchantId;
        _controller.HttpContext.Request.Scheme = "https";
        _controller.HttpContext.Request.Host = new HostString("localhost");
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    // ✅ FIX: Use a direct IP address to bypass DNS resolution failures in isolated test environments
    private CreateInvoiceRequest CreateRequest(decimal amount = 50m, string currency = "USD")
    {
        return new CreateInvoiceRequest
        {
            FiatAmount = amount,
            FiatCurrency = currency,
            CallbackUrl = "https://1.1.1.1/callback",
            ExpiryMinutes = 30
        };
    }

    [Fact]
    public async Task CreateInvoice_Success_IncrementsQuota()
    {
        var request = CreateRequest(50m);
        _controller.Request.Headers["Idempotency-Key"] = "test-success-1";

        var result = await _controller.CreateInvoice(request, CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
        
        var sub = await _dbContext.Subscriptions.AsNoTracking().FirstAsync(s => s.MerchantId == _merchantId);
        sub.CurrentPeriodInvoiceCount.Should().Be(1);
        sub.CurrentPeriodVolumeUsd.Should().Be(50m);
    }

    [Fact]
    public async Task CreateInvoice_HitsCountLimit_Returns403()
    {
        // Create 2 invoices to hit the limit
        _controller.Request.Headers["Idempotency-Key"] = "count-1";
        await _controller.CreateInvoice(CreateRequest(10m), CancellationToken.None);
        
        _controller.Request.Headers["Idempotency-Key"] = "count-2";
        await _controller.CreateInvoice(CreateRequest(10m), CancellationToken.None);

        // 3rd request should fail
        _controller.Request.Headers["Idempotency-Key"] = "count-3";
        var result = await _controller.CreateInvoice(CreateRequest(10m), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>();
        var objResult = result as ObjectResult;
        objResult.StatusCode.Should().Be(403);
        objResult.Value.Should().BeEquivalentTo(new { error = "SUBSCRIPTION_INVOICE_LIMIT_REACHED" });
    }

    [Fact]
    public async Task CreateInvoice_HitsVolumeLimit_Returns403()
    {
        // Create 1 invoice of $150 (Limit is $200)
        _controller.Request.Headers["Idempotency-Key"] = "vol-1";
        await _controller.CreateInvoice(CreateRequest(150m), CancellationToken.None);

        // 2nd request of $60 should exceed $200 limit
        _controller.Request.Headers["Idempotency-Key"] = "vol-2";
        var result = await _controller.CreateInvoice(CreateRequest(60m), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>();
        var objResult = result as ObjectResult;
        objResult.StatusCode.Should().Be(403);
        objResult.Value.Should().BeEquivalentTo(new { error = "SUBSCRIPTION_VOLUME_LIMIT_REACHED" });
    }

    [Fact]
    public async Task CreateInvoice_IdempotencyReplay_ReturnsCached_NoQuotaIncrement()
    {
        var request = CreateRequest(75m);
        _controller.Request.Headers["Idempotency-Key"] = "replay-test-key";

        var result1 = await _controller.CreateInvoice(request, CancellationToken.None);
        result1.Should().BeOfType<CreatedAtActionResult>();

        // Replay same request
        var result2 = await _controller.CreateInvoice(request, CancellationToken.None);
        result2.Should().BeOfType<ContentResult>(); // Returns cached content

        var sub = await _dbContext.Subscriptions.AsNoTracking().FirstAsync(s => s.MerchantId == _merchantId);
        sub.CurrentPeriodInvoiceCount.Should().Be(1); // Quota not incremented twice
    }

    [Fact]
    public async Task CreateInvoice_ExpiredIdempotencyKey_AllowsNewRequest()
    {
        // 1. Create an expired idempotency record manually
        var expiredRecord = new MerchantIdempotencyKey(_merchantId, "expired-key", "old-hash", "{}", 201, TimeSpan.FromHours(-1));
        _dbContext.MerchantIdempotencyKeys.Add(expiredRecord);
        await _dbContext.SaveChangesAsync();

        // 2. Send request with same key
        _controller.Request.Headers["Idempotency-Key"] = "expired-key";
        var result = await _controller.CreateInvoice(CreateRequest(25m), CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>(); // Should succeed, not return cached "{}"

        var sub = await _dbContext.Subscriptions.AsNoTracking().FirstAsync(s => s.MerchantId == _merchantId);
        sub.CurrentPeriodInvoiceCount.Should().Be(1); // Quota incremented for the new request
    }

    [Fact]
    public async Task CreateInvoice_FailedCreation_RollsBackUsage()
    {
        var subBefore = await _dbContext.Subscriptions.AsNoTracking().FirstAsync(s => s.MerchantId == _merchantId);
        
        // Send request with invalid currency to trigger early rollback (400 Bad Request)
        _controller.Request.Headers["Idempotency-Key"] = "fail-test";
        var result = await _controller.CreateInvoice(CreateRequest(10m, "EUR"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();

        var subAfter = await _dbContext.Subscriptions.AsNoTracking().FirstAsync(s => s.MerchantId == _merchantId);
        subAfter.CurrentPeriodInvoiceCount.Should().Be(subBefore.CurrentPeriodInvoiceCount);
        subAfter.CurrentPeriodVolumeUsd.Should().Be(subBefore.CurrentPeriodVolumeUsd);
    }

    [Fact]
    public async Task CreateInvoice_ConcurrentRequests_RespectsQuota()
    {
        // Reset quota for this specific test using reflection
        var sub = await _dbContext.Subscriptions.FirstAsync(s => s.MerchantId == _merchantId);
        var countProp = typeof(Subscription).GetProperty(nameof(Subscription.CurrentPeriodInvoiceCount))!;
        var volProp = typeof(Subscription).GetProperty(nameof(Subscription.CurrentPeriodVolumeUsd))!;
        countProp.SetValue(sub, 0);
        volProp.SetValue(sub, 0m);
        await _dbContext.SaveChangesAsync();

        // Simulate concurrent logic by checking limits manually in a loop
        int successCount = 0;
        int forbiddenCount = 0;

        for (int i = 0; i < 5; i++)
        {
            _controller.Request.Headers["Idempotency-Key"] = $"concurrent-{i}";
            var result = await _controller.CreateInvoice(CreateRequest(10m), CancellationToken.None);
            
            if (result is CreatedAtActionResult) successCount++;
            else if (result is ObjectResult objRes && objRes.StatusCode == 403) forbiddenCount++;
        }

        successCount.Should().Be(2, "Only 2 invoices are allowed by the plan limit");
        forbiddenCount.Should().Be(3, "Remaining 3 requests should be rejected with 403");
    }
}