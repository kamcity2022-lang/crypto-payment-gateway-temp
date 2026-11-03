using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Authorization;

public class MerchantIdHandler : AuthorizationHandler<MerchantIdRequirement>
{
    private readonly GatewayDbContext? _dbContext;

    // پارامتر اختیاری است تا تست‌های واحد بتوانند بدون دیتابیس واقعی اجرا شوند
    public MerchantIdHandler(GatewayDbContext? dbContext = null)
    {
        _dbContext = dbContext;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, MerchantIdRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext) return;

        var user = context.User;
        if (!user.Identity?.IsAuthenticated ?? true) return;

        var isSuperAdmin = user.IsInRole("SuperAdmin");
        var userMerchantIdClaim = user.FindFirstValue("MerchantId");
        Guid? userMerchantId = null;
        if (!string.IsNullOrEmpty(userMerchantIdClaim) && Guid.TryParse(userMerchantIdClaim, out var parsedGuid))
        {
            userMerchantId = parsedGuid;
        }

        if (userMerchantId == null && !isSuperAdmin)
        {
            return;
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
                httpContext.Items["ResolvedMerchantId"] = null;
            }
            context.Succeed(requirement);
            return;
        }

        if (string.IsNullOrEmpty(targetMerchantIdStr))
        {
            if (_dbContext != null)
            {
                var merchant = await _dbContext.Merchants.AsNoTracking().FirstOrDefaultAsync(m => m.Id == userMerchantId);
                if (merchant == null || !merchant.IsActive)
                {
                    return;
                }
            }

            httpContext.Items["ResolvedMerchantId"] = userMerchantId;
            context.Succeed(requirement);
            return;
        }

        if (Guid.TryParse(targetMerchantIdStr, out var targetMerchantId) && userMerchantId == targetMerchantId)
        {
            if (_dbContext != null)
            {
                var merchant = await _dbContext.Merchants.AsNoTracking().FirstOrDefaultAsync(m => m.Id == userMerchantId);
                if (merchant == null || !merchant.IsActive)
                {
                    return;
                }
            }

            httpContext.Items["ResolvedMerchantId"] = userMerchantId;
            context.Succeed(requirement);
        }
    }
}