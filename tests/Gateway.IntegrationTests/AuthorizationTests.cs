using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Gateway.IntegrationTests;

public class AuthorizationTests
{
    [Fact]
    public async Task HandleRequirementAsync_SuperAdmin_ShouldSucceed_AndResolveTargetMerchantId()
    {
        var handler = new MerchantIdHandler();
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
    public async Task HandleRequirementAsync_MerchantUser_OtherResource_ShouldFail()
    {
        var handler = new MerchantIdHandler();
        var requirement = new MerchantIdRequirement();
        var userMerchantId = Guid.NewGuid();
        var targetMerchantId = Guid.NewGuid();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("MerchantId", userMerchantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, "user1")
        }, "Test"));

        var httpContext = new DefaultHttpContext { User = user };
        httpContext.Request.RouteValues["merchantId"] = targetMerchantId.ToString();

        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRequirementAsync_HmacOnlyIdentity_ShouldFail()
    {
        // ✅ FIX: HMAC-only identity (no MerchantId claim) should NOT satisfy human-user policies
        var handler = new MerchantIdHandler();
        var requirement = new MerchantIdRequirement();
        var merchantId = Guid.NewGuid();

        // HMAC requests have no human-user claims, only HttpContext.Items["MerchantId"]
        var user = new ClaimsPrincipal(new ClaimsIdentity("HmacAuthentication"));
        var httpContext = new DefaultHttpContext { User = user };
        httpContext.Items["MerchantId"] = merchantId; // Set by MerchantSignatureFilter

        var context = new AuthorizationHandlerContext(new[] { requirement }, user, httpContext);
        await handler.HandleAsync(context);

        // ✅ FIX: Should FAIL because MerchantIdHandler only authorizes human users
        context.HasSucceeded.Should().BeFalse();
        httpContext.Items.ContainsKey("ResolvedMerchantId").Should().BeFalse();
    }

    [Fact]
    public void RegisterMerchantEndpoint_RequiresSuperAdminAuthorization()
    {
        // ✅ FIX: Verify public merchant registration is blocked
        var method = typeof(Gateway.Api.Controllers.MerchantsController).GetMethod("RegisterMerchant");
        var authorizeAttribute = method.GetCustomAttributes(typeof(AuthorizeAttribute), false).FirstOrDefault() as AuthorizeAttribute;

        authorizeAttribute.Should().NotBeNull();
        authorizeAttribute.Policy.Should().Be("SuperAdminOnly");
    }
}