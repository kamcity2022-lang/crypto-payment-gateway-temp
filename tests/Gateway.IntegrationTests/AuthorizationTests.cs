using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Api.Authorization;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public class AuthorizationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_authz_db").WithUsername("postgres").WithPassword("postgres_test_password").Build();
    
    private GatewayDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        var opts = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(_dbContainer.GetConnectionString()).Options;
        _dbContext = new GatewayDbContext(opts);
        await _dbContext.Database.MigrateAsync();

        // Seed an active and an inactive merchant for testing
        var activeMerchant = new Merchant(Guid.NewGuid(), "Active Merchant", "hash1", "sec1", "whsec1");
        var inactiveMerchant = new Merchant(Guid.NewGuid(), "Inactive Merchant", "hash2", "sec2", "whsec2");
        
        // Use reflection to set IsActive to false for the second merchant
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
    public async Task HandleRequirementAsync_SuperAdmin_ShouldSucceed_AndResolveTargetMerchantId()
    {
        var handler = new MerchantIdHandler(_dbContext);
        var requirement = new MerchantIdRequirement();
        var targetMerchantId = Guid.NewGuid();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "SuperAdmin"),
            new Claim(ClaimTypes.NameIdentifier, "admin")
        }, "Test"));

        var httpContext = new DefaultHttpContext { User = user };
        httpContext.Request.RouteValues["merchantId"] = targetMerchantId.ToString();

        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
        httpContext.Items["ResolvedMerchantId"].Should().Be(targetMerchantId);
    }

    [Fact]
    public async Task HandleRequirementAsync_ActiveMerchantUser_ShouldSucceed()
    {
        var handler = new MerchantIdHandler(_dbContext);
        var requirement = new MerchantIdRequirement();
        var activeMerchant = await _dbContext.Merchants.FirstAsync(m => m.Name == "Active Merchant");

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("MerchantId", activeMerchant.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, "user1")
        }, "Test"));

        var httpContext = new DefaultHttpContext { User = user };
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
        httpContext.Items["ResolvedMerchantId"].Should().Be(activeMerchant.Id);
    }

    [Fact]
    public async Task HandleRequirementAsync_InactiveMerchantUser_ShouldFail()
    {
        var handler = new MerchantIdHandler(_dbContext);
        var requirement = new MerchantIdRequirement();
        var inactiveMerchant = await _dbContext.Merchants.FirstAsync(m => m.Name == "Inactive Merchant");

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("MerchantId", inactiveMerchant.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, "user2")
        }, "Test"));

        var httpContext = new DefaultHttpContext { User = user };
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);
        await handler.HandleAsync(context);

        // ✅ CRITICAL: Should FAIL because the merchant is inactive in the database
        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public void RegisterMerchantEndpoint_RequiresSuperAdminAuthorization()
    {
        var method = typeof(Gateway.Api.Controllers.MerchantsController).GetMethod("RegisterMerchant");
        var authorizeAttribute = method.GetCustomAttributes(typeof(AuthorizeAttribute), false).FirstOrDefault() as AuthorizeAttribute;

        authorizeAttribute.Should().NotBeNull();
        authorizeAttribute.Policy.Should().Be("SuperAdminOnly");
    }
}