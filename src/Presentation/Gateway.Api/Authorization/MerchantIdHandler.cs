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

        // ✅ FIX: Reliable server-issued role check for SuperAdmin
        var isSuperAdmin = user.IsInRole("SuperAdmin");

        // 1. Try to get MerchantId from human-user claims
        var userMerchantIdClaim = user.FindFirstValue("MerchantId");
        Guid? userMerchantId = null;
        if (!string.IsNullOrEmpty(userMerchantIdClaim) && Guid.TryParse(userMerchantIdClaim, out var parsedGuid))
        {
            userMerchantId = parsedGuid;
        }

        // 2. Fallback to HMAC-authenticated trusted path
        if (userMerchantId == null && httpContext.Items.TryGetValue("MerchantId", out var itemMerchantId) && itemMerchantId is Guid hmacMerchantId)
        {
            userMerchantId = hmacMerchantId;
        }

        // ✅ FIX: Missing tenant context denies access (unless SuperAdmin)
        if (userMerchantId == null && !isSuperAdmin)
        {
            return Task.CompletedTask; 
        }

        var targetMerchantIdStr = httpContext.Request.RouteValues[requirement.RouteKey]?.ToString() 
                            ?? httpContext.Request.Query[requirement.RouteKey].FirstOrDefault();

        if (isSuperAdmin)
        {
            // ✅ FIX: Explicit cross-tenant access resolution
            if (!string.IsNullOrEmpty(targetMerchantIdStr) && Guid.TryParse(targetMerchantIdStr, out var targetGuid))
            {
                httpContext.Items["ResolvedMerchantId"] = targetGuid;
            }
            else
            {
                httpContext.Items["ResolvedMerchantId"] = null;
            }
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(targetMerchantIdStr))
        {
            // Implicitly accessing own resources
            httpContext.Items["ResolvedMerchantId"] = userMerchantId;
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // ✅ FIX: Do not trust client-supplied MerchantId. Must match authenticated user's MerchantId.
        if (Guid.TryParse(targetMerchantIdStr, out var targetMerchantId) && userMerchantId == targetMerchantId)
        {
            httpContext.Items["ResolvedMerchantId"] = userMerchantId;
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}