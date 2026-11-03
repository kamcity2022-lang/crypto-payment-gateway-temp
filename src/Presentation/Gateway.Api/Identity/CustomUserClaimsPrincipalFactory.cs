using System.Security.Claims;
using System.Threading.Tasks;
using Gateway.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Identity;

public class CustomUserClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    public CustomUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, roleManager, optionsAccessor)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        // تولید Claimهای پیش‌فرض (شامل Roleها و UserName)
        var identity = await base.GenerateClaimsAsync(user);

        // ✅ FIX: اضافه کردن صریح MerchantId به Claimها برای استفاده در MerchantIdHandler
        if (user.MerchantId.HasValue)
        {
            identity.AddClaim(new Claim("MerchantId", user.MerchantId.Value.ToString()));
        }

        return identity;
    }
}