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
        var identity = await base.GenerateClaimsAsync(user);

        // ✅ تزریق MerchantId به کوکی احراز هویت برای استفاده در MerchantIdHandler
        if (user.MerchantId.HasValue)
        {
            identity.AddClaim(new Claim("MerchantId", user.MerchantId.Value.ToString()));
        }

        return identity;
    }
}