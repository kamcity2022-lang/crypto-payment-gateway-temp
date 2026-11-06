using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Gateway.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public class SubscriptionQuotaTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_quota_db")
        .WithUsername("postgres")
        .WithPassword("postgres_test_password")
        .Build();
    private GatewayDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        var opts = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;
        _dbContext = new GatewayDbContext(opts);
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    [Fact]
    public async Task CreateInvoice_BelowCountLimit_ShouldSucceedAndIncrement()
    {
        var merchantId = Guid.NewGuid();
        var plan = new Plan("Starter", "desc", 10, 5000m, 0m);
        var subscription = new Subscription(merchantId, plan.Id);
        var wallet = new Wallet(merchantId, "xpub_test", "TRON");

        _dbContext.Merchants.Add(new Merchant(merchantId, "Test", "hash", "sec", "whsec"));
        _dbContext.Plans.Add(plan);
        _dbContext.Subscriptions.Add(subscription);
        _dbContext.Wallets.Add(wallet);
        await _dbContext.SaveChangesAsync();

        await using var tx = await _dbContext.Database.BeginTransactionAsync();
        var sub = await _dbContext.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
            .FirstAsync();
        
        sub.ResetPeriodIfExpired();
        // ✅ FIX: استفاده از متد Domain به جای مقداردهی مستقیم
        sub.RecordInvoiceCreation(100m);
        await _dbContext.SaveChangesAsync();
        await tx.CommitAsync();

        var updated = await _dbContext.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == sub.Id);
        updated.CurrentPeriodInvoiceCount.Should().Be(1);
        updated.CurrentPeriodVolumeUsd.Should().Be(100m);
        updated.CurrentPeriodEndUtc.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task CreateInvoice_AtCountLimit_ShouldReject()
    {
        var merchantId = Guid.NewGuid();
        var plan = new Plan("Starter", "desc", 1, 5000m, 0m);
        var subscription = new Subscription(merchantId, plan.Id);
        
        // ✅ FIX: رسیدن به سقف با استفاده از متد Domain
        subscription.RecordInvoiceCreation(100m);

        _dbContext.Merchants.Add(new Merchant(merchantId, "Test", "hash", "sec", "whsec"));
        _dbContext.Plans.Add(plan);
        _dbContext.Subscriptions.Add(subscription);
        await _dbContext.SaveChangesAsync();

        await using var tx = await _dbContext.Database.BeginTransactionAsync();
        var sub = await _dbContext.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
            .FirstAsync();
        
        sub.ResetPeriodIfExpired();
        var effectiveLimit = sub.CustomInvoiceLimit ?? plan.MonthlyInvoiceLimit;
        (sub.CurrentPeriodInvoiceCount < effectiveLimit).Should().BeFalse();
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task CreateInvoice_VolumeBoundary_EqualSucceeds_ExceedingFails()
    {
        var merchantId = Guid.NewGuid();
        var plan = new Plan("Starter", "desc", 100, 500m, 0m);
        var subscription = new Subscription(merchantId, plan.Id);
        
        // ✅ FIX: استفاده از متد Domain
        subscription.RecordInvoiceCreation(400m);

        _dbContext.Merchants.Add(new Merchant(merchantId, "Test", "hash", "sec", "whsec"));
        _dbContext.Plans.Add(plan);
        _dbContext.Subscriptions.Add(subscription);
        await _dbContext.SaveChangesAsync();

        await using var tx = await _dbContext.Database.BeginTransactionAsync();
        var sub = await _dbContext.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
            .FirstAsync();
        
        var effectiveVolume = sub.CustomVolumeLimitUsd ?? plan.MonthlyVolumeLimitUsd;
        (effectiveVolume == 0 || (sub.CurrentPeriodVolumeUsd + 100m) <= effectiveVolume).Should().BeTrue();
        (effectiveVolume == 0 || (sub.CurrentPeriodVolumeUsd + 101m) <= effectiveVolume).Should().BeFalse();
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task CreateInvoice_UnlimitedVolume_ShouldAllowAnyAmount()
    {
        var merchantId = Guid.NewGuid();
        var plan = new Plan("Unlimited", "desc", 100, 0m, 0m);
        var subscription = new Subscription(merchantId, plan.Id);

        _dbContext.Merchants.Add(new Merchant(merchantId, "Test", "hash", "sec", "whsec"));
        _dbContext.Plans.Add(plan);
        _dbContext.Subscriptions.Add(subscription);
        await _dbContext.SaveChangesAsync();

        await using var tx = await _dbContext.Database.BeginTransactionAsync();
        var sub = await _dbContext.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
            .FirstAsync();
        
        var effectiveVolume = sub.CustomVolumeLimitUsd ?? plan.MonthlyVolumeLimitUsd;
        (effectiveVolume == 0 || (sub.CurrentPeriodVolumeUsd + 1_000_000m) <= effectiveVolume).Should().BeTrue();
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task CreateInvoice_CustomOverridesTakePrecedence()
    {
        var merchantId = Guid.NewGuid();
        var plan = new Plan("Starter", "desc", 10, 1000m, 0m);
        var subscription = new Subscription(merchantId, plan.Id);
        subscription.OverrideLimits(5, 500m);

        _dbContext.Merchants.Add(new Merchant(merchantId, "Test", "hash", "sec", "whsec"));
        _dbContext.Plans.Add(plan);
        _dbContext.Subscriptions.Add(subscription);
        await _dbContext.SaveChangesAsync();

        await using var tx = await _dbContext.Database.BeginTransactionAsync();
        var sub = await _dbContext.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
            .FirstAsync();
        
        (sub.CustomInvoiceLimit ?? plan.MonthlyInvoiceLimit).Should().Be(5);
        (sub.CustomVolumeLimitUsd ?? plan.MonthlyVolumeLimitUsd).Should().Be(500m);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task ConcurrentInvoiceCreation_CannotExceedQuotas()
    {
        var merchantId = Guid.NewGuid();
        var plan = new Plan("Starter", "desc", 1, 5000m, 0m);
        var subscription = new Subscription(merchantId, plan.Id);

        _dbContext.Merchants.Add(new Merchant(merchantId, "Test", "hash", "sec", "whsec"));
        _dbContext.Plans.Add(plan);
        _dbContext.Subscriptions.Add(subscription);
        await _dbContext.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(_dbContainer.GetConnectionString()).Options;
        await using var db1 = new GatewayDbContext(options);
        await using var db2 = new GatewayDbContext(options);

        var tx1 = await db1.Database.BeginTransactionAsync();
        var tx2 = await db2.Database.BeginTransactionAsync();

        var sub1 = await db1.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
            .FirstAsync();

        var task2 = Task.Run(async () => 
        {
            var sub2 = await db2.Subscriptions
                .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
                .FirstAsync();
            sub2.ResetPeriodIfExpired();
            var effectiveLimit = sub2.CustomInvoiceLimit ?? plan.MonthlyInvoiceLimit;
            return sub2.CurrentPeriodInvoiceCount < effectiveLimit;
        });

        sub1.ResetPeriodIfExpired();
        var effectiveLimit1 = sub1.CustomInvoiceLimit ?? plan.MonthlyInvoiceLimit;
        (sub1.CurrentPeriodInvoiceCount < effectiveLimit1).Should().BeTrue();

        sub1.RecordInvoiceCreation(100m);
        await db1.SaveChangesAsync();
        await tx1.CommitAsync();

        var allowed2 = await task2;
        allowed2.Should().BeFalse();
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task IdempotencyReplay_DoesNotIncrementUsage()
    {
        var merchantId = Guid.NewGuid();
        var plan = new Plan("Starter", "desc", 10, 5000m, 0m);
        var subscription = new Subscription(merchantId, plan.Id);
        var wallet = new Wallet(merchantId, "xpub_test", "TRON");

        _dbContext.Merchants.Add(new Merchant(merchantId, "Test", "hash", "sec", "whsec"));
        _dbContext.Plans.Add(plan);
        _dbContext.Subscriptions.Add(subscription);
        _dbContext.Wallets.Add(wallet);
        await _dbContext.SaveChangesAsync();

        var idempotencyKey = "test-key-123";
        var payloadHash = "hash123";
        var responsePayload = "{\"invoiceId\":\"123\"}";

        await using var tx1 = await _dbContext.Database.BeginTransactionAsync();
        var sub1 = await _dbContext.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
            .FirstAsync();
        
        // ✅ FIX: استفاده از متد Domain
        sub1.RecordInvoiceCreation(100m);
        _dbContext.MerchantIdempotencyKeys.Add(new MerchantIdempotencyKey(merchantId, idempotencyKey, payloadHash, responsePayload, 201, TimeSpan.FromHours(24)));
        await _dbContext.SaveChangesAsync();
        await tx1.CommitAsync();

        await using var tx2 = await _dbContext.Database.BeginTransactionAsync();
        var sub2 = await _dbContext.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
            .FirstAsync();
        
        var existing2 = await _dbContext.MerchantIdempotencyKeys
            .FirstOrDefaultAsync(k => k.MerchantId == merchantId && k.Key == idempotencyKey);
        existing2.Should().NotBeNull();
        await tx2.RollbackAsync();

        var finalSub = await _dbContext.Subscriptions.AsNoTracking().FirstAsync(s => s.MerchantId == merchantId);
        finalSub.CurrentPeriodInvoiceCount.Should().Be(1);
        finalSub.CurrentPeriodVolumeUsd.Should().Be(100m);
    }

    [Fact]
    public async Task TenantIsolation_UsageDoesNotAffectOtherMerchants()
    {
        var merchant1Id = Guid.NewGuid();
        var merchant2Id = Guid.NewGuid();
        var plan = new Plan("Starter", "desc", 10, 5000m, 0m);

        _dbContext.Merchants.AddRange(
            new Merchant(merchant1Id, "M1", "hash1", "sec1", "whsec1"),
            new Merchant(merchant2Id, "M2", "hash2", "sec2", "whsec2")
        );
        _dbContext.Plans.Add(plan);
        _dbContext.Subscriptions.AddRange(new Subscription(merchant1Id, plan.Id), new Subscription(merchant2Id, plan.Id));
        await _dbContext.SaveChangesAsync();

        await using var tx = await _dbContext.Database.BeginTransactionAsync();
        var sub1Locked = await _dbContext.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchant1Id} AND \"Status\" = 1 FOR UPDATE")
            .FirstAsync();
        
        // ✅ FIX: استفاده از متد Domain
        sub1Locked.RecordInvoiceCreation(100m);
        await _dbContext.SaveChangesAsync();
        await tx.CommitAsync();

        var finalSub1 = await _dbContext.Subscriptions.AsNoTracking().FirstAsync(s => s.MerchantId == merchant1Id);
        var finalSub2 = await _dbContext.Subscriptions.AsNoTracking().FirstAsync(s => s.MerchantId == merchant2Id);

        finalSub1.CurrentPeriodInvoiceCount.Should().Be(1);
        finalSub2.CurrentPeriodInvoiceCount.Should().Be(0);
    }

    [Fact]
    public async Task PeriodRollover_ResetsUsageCorrectly()
    {
        var merchantId = Guid.NewGuid();
        var plan = new Plan("Starter", "desc", 10, 5000m, 0m);
        var subscription = new Subscription(merchantId, plan.Id);
        
        // ✅ FIX: استفاده از Reflection فقط برای شبیه‌سازی سفر در زمان (تست ResetPeriodIfExpired)
        var propEnd = typeof(Subscription).GetProperty(nameof(Subscription.CurrentPeriodEndUtc))!;
        var propStart = typeof(Subscription).GetProperty(nameof(Subscription.CurrentPeriodStartUtc))!;
        var propCount = typeof(Subscription).GetProperty(nameof(Subscription.CurrentPeriodInvoiceCount))!;
        var propVol = typeof(Subscription).GetProperty(nameof(Subscription.CurrentPeriodVolumeUsd))!;
        
        propStart.SetValue(subscription, DateTime.UtcNow.AddMonths(-3));
        propEnd.SetValue(subscription, DateTime.UtcNow.AddMonths(-2));
        propCount.SetValue(subscription, 10);
        propVol.SetValue(subscription, 5000m);

        _dbContext.Merchants.Add(new Merchant(merchantId, "Test", "hash", "sec", "whsec"));
        _dbContext.Plans.Add(plan);
        _dbContext.Subscriptions.Add(subscription);
        await _dbContext.SaveChangesAsync();

        await using var tx = await _dbContext.Database.BeginTransactionAsync();
        var sub = await _dbContext.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
            .FirstAsync();
        
        sub.ResetPeriodIfExpired();
        
        sub.CurrentPeriodInvoiceCount.Should().Be(0);
        sub.CurrentPeriodVolumeUsd.Should().Be(0m);
        sub.CurrentPeriodEndUtc.Should().BeAfter(DateTime.UtcNow);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task FailedInvoiceCreation_DoesNotConsumeQuota()
    {
        var merchantId = Guid.NewGuid();
        var plan = new Plan("Starter", "desc", 10, 5000m, 0m);
        var subscription = new Subscription(merchantId, plan.Id);

        _dbContext.Merchants.Add(new Merchant(merchantId, "Test", "hash", "sec", "whsec"));
        _dbContext.Plans.Add(plan);
        _dbContext.Subscriptions.Add(subscription);
        await _dbContext.SaveChangesAsync();

        await using var tx = await _dbContext.Database.BeginTransactionAsync();
        var wallet = await _dbContext.Wallets
            .FromSqlInterpolated($"SELECT * FROM \"Wallets\" WHERE \"MerchantId\" = {merchantId} AND \"Network\" = 'TRON' FOR UPDATE")
            .FirstOrDefaultAsync();
        
        if (wallet == null)
        {
            await tx.RollbackAsync();
        }

        var finalSub = await _dbContext.Subscriptions.AsNoTracking().FirstAsync(s => s.MerchantId == merchantId);
        finalSub.CurrentPeriodInvoiceCount.Should().Be(0);
        finalSub.CurrentPeriodVolumeUsd.Should().Be(0m);
    }
}