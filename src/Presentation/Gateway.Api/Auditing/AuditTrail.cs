using System;
using System.Security.Claims;
using Gateway.Domain.Entities;
using Gateway.Persistence;

namespace Gateway.Api.Auditing;

// Only curated operation names and stable target IDs are stored. Never pass
// user-supplied request bodies, URLs, passwords, API secrets or invite tokens.
public static class AuditTrail
{
    public static void Record(GatewayDbContext db, ClaimsPrincipal actor,
        Guid? merchantId, string action, string targetType, string targetId)
    {
        var userId = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        db.AuditEntries.Add(new AuditEntry(merchantId, userId, action, targetType, targetId));
    }
}
