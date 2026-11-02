using Microsoft.AspNetCore.Authorization;

namespace Gateway.Api.Authorization;

public class MerchantIdRequirement : IAuthorizationRequirement 
{
    public string RouteKey { get; }
    public MerchantIdRequirement(string routeKey = "merchantId") 
    {
        RouteKey = routeKey;
    }
}