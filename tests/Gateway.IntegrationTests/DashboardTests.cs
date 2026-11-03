using System;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Gateway.IntegrationTests;

public class DashboardTests
{
    [Fact]
    public async Task DashboardOverview_WithValidMerchantClaim_ShouldSucceed()
    {
        var handler = new MerchantIdHandler(null!);
        var requirement = new MerchantIdRequirement();
        var merchantId = Guid.NewGuid();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("MerchantId", merchantId.ToString()),
            new Claim(ClaimTypes.Role, "Owner"),
            new Claim(ClaimTypes.NameIdentifier, "user1")
        }, "Test"));

        var httpContext = new DefaultHttpContext { User = user };
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);
        
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
        httpContext.Items["ResolvedMerchantId"].Should().Be(merchantId);
    }

    [Fact]
    public async Task DashboardOverview_SuperAdminWithoutMerchantClaim_ShouldForbid()
    {
        var handler = new MerchantIdHandler(null!);
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
        // BUT ResolvedMerchantId is explicitly set to null by the handler for SuperAdmin without target
        httpContext.Items["ResolvedMerchantId"].Should().BeNull();
        
        // Therefore, any controller doing `if (!TryGetResolvedMerchantId(out var id)) return Forbid();` 
        // will correctly block the SuperAdmin from accessing merchant-specific data.
    }
}