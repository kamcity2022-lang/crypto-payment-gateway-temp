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
    private readonly GatewayDbContext _dbContext;

    // ✅ CRITICAL FIX: GatewayDbContext is now a required dependency.
    public MerchantIdHandler(GatewayDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
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

        if (userMerchantId == null && !isSuperAdmin) return;

        var targetMerchantIdStr = httpContext.Request.RouteValues[requirement.RouteKey]?.ToString()
            ?? httpContext.Request.Query[requirement.RouteKey].FirstOrDefault();

        // SuperAdmin behavior remains unchanged and independent of merchant status
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

        // ✅ CRITICAL FIX: Verify merchant is active from database before granting access
        if (string.IsNullOrEmpty(targetMerchantIdStr))
        {
            var merchant = await _dbContext.Merchants.AsNoTracking().FirstOrDefaultAsync(m => m.Id == userMerchantId);
            if (merchant == null || !merchant.IsActive) return; // Fail authorization if inactive/not found

            httpContext.Items["ResolvedMerchantId"] = userMerchantId;
            context.Succeed(requirement);
            return;
        }

        if (Guid.TryParse(targetMerchantIdStr, out var targetMerchantId) && userMerchantId == targetMerchantId)
        {
            var merchant = await _dbContext.Merchants.AsNoTracking().FirstOrDefaultAsync(m => m.Id == userMerchantId);
            if (merchant == null || !merchant.IsActive) return; // Fail authorization if inactive/not found

            httpContext.Items["ResolvedMerchantId"] = userMerchantId;
            context.Succeed(requirement);
        }
    }
}