using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Gateway.Api.Authorization;

public class MerchantIdHandler : AuthorizationHandler<MerchantIdRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MerchantIdRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext) return Task.CompletedTask;

        var user = context.User;
        if (!user.Identity?.IsAuthenticated ?? true) return Task.CompletedTask;

        var isSuperAdmin = user.HasClaim(c => c.Type == "IsSuperAdmin" && c.Value == "True");
        
        var userMerchantId = user.FindFirstValue("MerchantId");
        // Fallback to HMAC context if Identity claim is missing
        if (string.IsNullOrEmpty(userMerchantId) && httpContext.Items.TryGetValue("MerchantId", out var itemMerchantId))
        {
            userMerchantId = itemMerchantId.ToString();
        }

        if (string.IsNullOrEmpty(userMerchantId) && !isSuperAdmin)
        {
            return Task.CompletedTask; // Deny by default
        }

        var targetMerchantId = httpContext.Request.RouteValues[requirement.RouteKey]?.ToString() 
                            ?? httpContext.Request.Query[requirement.RouteKey].FirstOrDefault();

        if (isSuperAdmin)
        {
            httpContext.Items["ResolvedMerchantId"] = string.IsNullOrEmpty(targetMerchantId) ? null : targetMerchantId;
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(targetMerchantId))
        {
            // Implicitly accessing own resources
            httpContext.Items["ResolvedMerchantId"] = userMerchantId;
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (userMerchantId == targetMerchantId)
        {
            httpContext.Items["ResolvedMerchantId"] = userMerchantId;
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}