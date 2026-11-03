using System;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Api.Authorization;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public class DashboardTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_dash_db").WithUsername("postgres").WithPassword("postgres_test_password").Build();
    
    private GatewayDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        var opts = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(_dbContainer.GetConnectionString()).Options;
        _dbContext = new GatewayDbContext(opts);
        await _dbContext.Database.MigrateAsync();

        var activeMerchant = new Merchant(Guid.NewGuid(), "Active Dashboard Merchant", "hash1", "sec1", "whsec1");
        var inactiveMerchant = new Merchant(Guid.NewGuid(), "Inactive Dashboard Merchant", "hash2", "sec2", "whsec2");
        
        var prop = typeof(Merchant).GetProperty("IsActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        prop?.SetValue(inactiveMerchant, false);

        _dbContext.Merchants.AddRange(activeMerchant, inactiveMerchant);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    [Fact]
    public async Task DashboardOverview_WithActiveMerchantClaim_ShouldSucceed()
    {
        var handler = new MerchantIdHandler(_dbContext);
        var requirement = new MerchantIdRequirement();
        var activeMerchant = await _dbContext.Merchants.FirstAsync(m => m.Name == "Active Dashboard Merchant");

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("MerchantId", activeMerchant.Id.ToString()),
            new Claim(ClaimTypes.Role, "Owner"),
            new Claim(ClaimTypes.NameIdentifier, "user1")
        }, "Test"));

        var httpContext = new DefaultHttpContext { User = user };
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);
        
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
        httpContext.Items["ResolvedMerchantId"].Should().Be(activeMerchant.Id);
    }

    [Fact]
    public async Task DashboardOverview_WithInactiveMerchantClaim_ShouldForbid()
    {
        var handler = new MerchantIdHandler(_dbContext);
        var requirement = new MerchantIdRequirement();
        var inactiveMerchant = await _dbContext.Merchants.FirstAsync(m => m.Name == "Inactive Dashboard Merchant");

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("MerchantId", inactiveMerchant.Id.ToString()),
            new Claim(ClaimTypes.Role, "Owner"),
            new Claim(ClaimTypes.NameIdentifier, "user2")
        }, "Test"));

        var httpContext = new DefaultHttpContext { User = user };
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);
        
        await handler.HandleAsync(context);

        // ✅ CRITICAL: Should FAIL because the merchant is inactive
        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task DashboardOverview_SuperAdminWithoutMerchantClaim_ShouldForbidSpecificData()
    {
        var handler = new MerchantIdHandler(_dbContext);
        var requirement = new MerchantIdRequirement();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "SuperAdmin"),
            new Claim(ClaimTypes.NameIdentifier, "admin")
        }, "Test"));

        var httpContext = new DefaultHttpContext { User = user };
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);
        
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue(); // Handler succeeds for SuperAdmin
        httpContext.Items["ResolvedMerchantId"].Should().BeNull(); // But resolves to null, blocking merchant-specific data access
    }
}