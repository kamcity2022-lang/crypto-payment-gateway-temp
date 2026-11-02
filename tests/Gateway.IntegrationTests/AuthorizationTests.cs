using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Api.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Gateway.IntegrationTests;

public class AuthorizationTests
{
    [Fact]
    public async Task HandleRequirementAsync_SuperAdmin_ShouldSucceed_AndResolveNullTarget()
    {
        var handler = new MerchantIdHandler();
        var requirement = new MerchantIdRequirement();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("IsSuperAdmin", "True"),
            new Claim(ClaimTypes.NameIdentifier, "admin")
        }, "Test"));
        
        var httpContext = new DefaultHttpContext { User = user };
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
        httpContext.Items["ResolvedMerchantId"].Should().BeNull();
    }

    [Fact]
    public async Task HandleRequirementAsync_MerchantUser_OwnResource_ShouldSucceed_AndResolveMerchantId()
    {
        var handler = new MerchantIdHandler();
        var requirement = new MerchantIdRequirement();
        var merchantId = Guid.NewGuid().ToString();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("MerchantId", merchantId),
            new Claim(ClaimTypes.NameIdentifier, "user1")
        }, "Test"));
        
        var httpContext = new DefaultHttpContext { User = user };
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
        httpContext.Items["ResolvedMerchantId"].Should().Be(merchantId);
    }

    [Fact]
    public async Task HandleRequirementAsync_MerchantUser_OtherResource_ShouldFail()
    {
        var handler = new MerchantIdHandler();
        var requirement = new MerchantIdRequirement();
        var userMerchantId = Guid.NewGuid().ToString();
        var targetMerchantId = Guid.NewGuid().ToString();
        
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("MerchantId", userMerchantId),
            new Claim(ClaimTypes.NameIdentifier, "user1")
        }, "Test"));
        
        var httpContext = new DefaultHttpContext { User = user };
        httpContext.Request.RouteValues["merchantId"] = targetMerchantId;
        
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRequirementAsync_HmacUser_ShouldSucceed_AndResolveMerchantId()
    {
        var handler = new MerchantIdHandler();
        var requirement = new MerchantIdRequirement();
        var merchantId = Guid.NewGuid().ToString();
        
        var user = new ClaimsPrincipal(new ClaimsIdentity("HmacAuthentication"));
        var httpContext = new DefaultHttpContext { User = user };
        httpContext.Items["MerchantId"] = merchantId;
        
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
        httpContext.Items["ResolvedMerchantId"].Should().Be(merchantId);
    }
}