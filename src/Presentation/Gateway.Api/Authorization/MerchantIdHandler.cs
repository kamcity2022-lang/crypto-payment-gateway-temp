using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Gateway.Api.Authorization;

public class MerchantIdHandler : AuthorizationHandler<MerchantIdRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        MerchantIdRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext) return Task.CompletedTask;

        var user = context.User;
        if (!user.Identity?.IsAuthenticated ?? true) return Task.CompletedTask;

        var isSuperAdmin = user.IsInRole("SuperAdmin");

        // ✅ فقط از Claimهای انسانی خوانده می‌شود. هیچ Fallback به HMAC وجود ندارد.
        var userMerchantIdClaim = user.FindFirstValue("MerchantId");
        Guid? userMerchantId = null;
        if (!string.IsNullOrEmpty(userMerchantIdClaim) && Guid.TryParse(userMerchantIdClaim, out var parsedGuid))
        {
            userMerchantId = parsedGuid;
        }

        // ✅ اگر MerchantId نداشت و SuperAdmin هم نبود، رد می‌شود
        if (userMerchantId == null && !isSuperAdmin)
        {
            return Task.CompletedTask;
        }

        var targetMerchantIdStr = httpContext.Request.RouteValues[requirement.RouteKey]?.ToString()
                                  ?? httpContext.Request.Query[requirement.RouteKey].FirstOrDefault();

        if (isSuperAdmin)
        {
            if (!string.IsNullOrEmpty(targetMerchantIdStr) && Guid.TryParse(targetMerchantIdStr, out var targetGuid))
            {
                httpContext.Items["ResolvedMerchantId"] = targetGuid;
            }
            else
            {
                httpContext.Items["ResolvedMerchantId"] = null; // ✅ SuperAdmin بدون context مشخص بلاک می‌شود
            }

            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(targetMerchantIdStr))
        {
            httpContext.Items["ResolvedMerchantId"] = userMerchantId;
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // ✅ عدم اعتماد به ورودی کلاینت: باید دقیقاً با MerchantId کاربر یکی باشد
        if (Guid.TryParse(targetMerchantIdStr, out var targetMerchantId) && userMerchantId == targetMerchantId)
        {
            httpContext.Items["ResolvedMerchantId"] = userMerchantId;
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}