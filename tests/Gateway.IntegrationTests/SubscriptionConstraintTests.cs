using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Gateway.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public class SubscriptionConstraintTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_subscription_db")
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
    public async Task UniqueActiveSubscription_PerMerchant_ShouldEnforceConstraint()
    {
        var merchantId = Guid.NewGuid();
        
        var merchant = new Merchant(merchantId, "Test Merchant", "hash_test", "secret_test", "webhook_test");
        var plan = new Plan("Test Plan", "desc", 100, 5000m, 0m);
        
        _dbContext.Merchants.Add(merchant);
        _dbContext.Plans.Add(plan);
        await _dbContext.SaveChangesAsync();
        
        // ✅ FIX: استفاده از plan.Id به جای planId تصادفی
        var sub1 = new Subscription(merchantId, plan.Id);
        _dbContext.Subscriptions.Add(sub1);
        await _dbContext.SaveChangesAsync();
        
        var sub2 = new Subscription(merchantId, plan.Id);
        _dbContext.Subscriptions.Add(sub2);
        
        var act = () => _dbContext.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task UniqueDefaultPlan_ShouldEnforceConstraint()
    {
        var plan1 = new Plan("Starter", "desc", 100, 5000m, 0m, isDefault: true);
        _dbContext.Plans.Add(plan1);
        await _dbContext.SaveChangesAsync();
        
        var plan2 = new Plan("Pro", "desc", 500, 50000m, 29m, isDefault: true);
        _dbContext.Plans.Add(plan2);
        
        var act = () => _dbContext.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Bootstrap_AssignsSubscriptionsToExistingMerchants_ShouldBeIdempotent()
    {
        // Create merchants without subscriptions
        var merchant1 = new Merchant(Guid.NewGuid(), "M1", "hash1", "sec1", "whsec1");
        var merchant2 = new Merchant(Guid.NewGuid(), "M2", "hash2", "sec2", "whsec2");
        _dbContext.Merchants.AddRange(merchant1, merchant2);
        
        // Create default plan
        var defaultPlan = new Plan("Starter", "desc", 100, 5000m, 0m, isDefault: true);
        _dbContext.Plans.Add(defaultPlan);
        await _dbContext.SaveChangesAsync();
        
        // First bootstrap run
        var merchantsWithoutSub = await _dbContext.Merchants
            .Where(m => !_dbContext.Subscriptions.Any(s => 
                s.MerchantId == m.Id && s.Status == SubscriptionStatus.Active))
            .Select(m => m.Id)
            .ToListAsync();
        
        foreach (var mid in merchantsWithoutSub)
        {
            _dbContext.Subscriptions.Add(new Subscription(mid, defaultPlan.Id));
        }
        await _dbContext.SaveChangesAsync();
        
        var subs = await _dbContext.Subscriptions
            .Where(s => s.PlanId == defaultPlan.Id)
            .ToListAsync();
        subs.Should().HaveCount(2);
        
        // Second bootstrap run (idempotency check)
        var merchantsWithoutSub2 = await _dbContext.Merchants
            .Where(m => !_dbContext.Subscriptions.Any(s => 
                s.MerchantId == m.Id && s.Status == SubscriptionStatus.Active))
            .Select(m => m.Id)
            .ToListAsync();
        
        merchantsWithoutSub2.Should().BeEmpty();
        
        // Verify no duplicates created
        var subsAfter = await _dbContext.Subscriptions
            .Where(s => s.PlanId == defaultPlan.Id)
            .ToListAsync();
        subsAfter.Should().HaveCount(2);
    }

    [Fact]
    public async Task Bootstrap_PreservesExistingSubscriptions_ShouldNotOverride()
    {
        var merchantId = Guid.NewGuid();
        
        var merchant = new Merchant(merchantId, "Test", "hash", "sec", "whsec");
        var customPlan = new Plan("Custom", "desc", 500, 50000m, 99m);
        var defaultPlan = new Plan("Starter", "desc", 100, 5000m, 0m, isDefault: true);
        
        _dbContext.Merchants.Add(merchant);
        _dbContext.Plans.AddRange(customPlan, defaultPlan);
        
        var existingSub = new Subscription(merchantId, customPlan.Id);
        _dbContext.Subscriptions.Add(existingSub);
        await _dbContext.SaveChangesAsync();
        
        // Bootstrap logic
        var merchantsWithoutSub = await _dbContext.Merchants
            .Where(m => !_dbContext.Subscriptions.Any(s => 
                s.MerchantId == m.Id && s.Status == SubscriptionStatus.Active))
            .Select(m => m.Id)
            .ToListAsync();
        
        merchantsWithoutSub.Should().BeEmpty();
        
        var sub = await _dbContext.Subscriptions.FirstAsync(s => s.MerchantId == merchantId);
        // ✅ FIX: استفاده از customPlan.Id به جای customPlanId
        sub.PlanId.Should().Be(customPlan.Id, "Existing subscription should be preserved");
    }
    
        [Fact]
    public void EfCoreModel_ShouldNotContainMerchantId1ShadowProperty()
    {
        var entityType = _dbContext.Model.FindEntityType(typeof(Subscription));
        var properties = entityType!.GetProperties().Select(p => p.Name).ToList();
        
        properties.Should().NotContain("MerchantId1", "Shadow property MerchantId1 must not exist");
        properties.Should().Contain("MerchantId", "Explicit MerchantId must exist");
    }

    [Fact]
    public void EfCoreModel_SubscriptionMerchantId_ShouldReferenceMerchantsId()
    {
        var entityType = _dbContext.Model.FindEntityType(typeof(Subscription));
        var fk = entityType!.GetForeignKeys().First(f => f.Properties.Any(p => p.Name == "MerchantId"));
        
        fk.PrincipalEntityType.ClrType.Should().Be(typeof(Merchant));
        fk.PrincipalKey.Properties.First().Name.Should().Be("Id");
    }

    [Fact]
    public async Task BootstrapSubscriptionAssignment_ShouldWorkCorrectly()
    {
        // Arrange: Create a merchant without a subscription
        var merchantId = Guid.NewGuid();
        var merchant = new Merchant(merchantId, "Test Merchant", "hash_bootstrap", "sec_bootstrap", "whsec_bootstrap");
        _dbContext.Merchants.Add(merchant);
        await _dbContext.SaveChangesAsync();

        var defaultPlan = new Plan("Starter", "desc", 100, 5000m, 0m, isDefault: true);
        _dbContext.Plans.Add(defaultPlan);
        await _dbContext.SaveChangesAsync();

        // Act: Execute the exact query logic used in SuperAdminBootstrapService
        var merchantsWithoutSubscription = await _dbContext.Merchants
            .Where(m => !_dbContext.Subscriptions.Any(s => 
                s.MerchantId == m.Id && s.Status == SubscriptionStatus.Active))
            .Select(m => m.Id)
            .ToListAsync();

        foreach (var mid in merchantsWithoutSubscription)
        {
            _dbContext.Subscriptions.Add(new Subscription(mid, defaultPlan.Id));
        }
        await _dbContext.SaveChangesAsync();

        // Assert
        var subscription = await _dbContext.Subscriptions.FirstOrDefaultAsync(s => s.MerchantId == merchantId);
        subscription.Should().NotBeNull();
        subscription!.PlanId.Should().Be(defaultPlan.Id);
        subscription.Status.Should().Be(SubscriptionStatus.Active);
        subscription.MerchantId.Should().Be(merchantId);
    }
    
}