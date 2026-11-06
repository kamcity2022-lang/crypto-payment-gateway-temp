using System;
using Microsoft.AspNetCore.Identity;

namespace Gateway.Domain.Entities;

public class ApplicationUser : IdentityUser
{
    public Guid? MerchantId { get; set; }
    public virtual Merchant? Merchant { get; set; }
    public bool IsSuperAdmin { get; set; }
    public bool IsDisabled { get; set; }
}