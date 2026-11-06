using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

public sealed class InviteTeamMemberRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
    [Required]
    public string Role { get; set; } = string.Empty;
}

public sealed class UpdateTeamRoleRequest
{
    [Required]
    public string Role { get; set; } = string.Empty;
}

public sealed class UpdateTeamStatusRequest
{
    public bool Disabled { get; set; }
}

[ApiController]
[Route("api/v1/dashboard/team")]
[Authorize(Policy = "MerchantOwner")]
public sealed class MerchantTeamController : ControllerBase
{
    private readonly GatewayDbContext _db;
    private readonly UserManager<ApplicationUser> _users;

    public MerchantTeamController(GatewayDbContext db, UserManager<ApplicationUser> users)
    {
        _db = db;
        _users = users;
    }

    private bool TryMerchant(out Guid id)
    {
        id = Guid.Empty;
        return HttpContext.Items.TryGetValue("ResolvedMerchantId", out var value) &&
            value is Guid merchantId && (id = merchantId) != Guid.Empty;
    }

    private static bool IsDelegatedRole(string role) =>
        role is "Admin" or "Developer" or "Viewer";

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();

        var members = await _db.Users.AsNoTracking()
            .Where(u => u.MerchantId == merchantId)
            .OrderBy(u => u.Email)
            .Select(u => new { u.Id, u.Email, u.EmailConfirmed, u.IsDisabled })
            .ToListAsync(ct);

        var result = new System.Collections.Generic.List<object>();
        foreach (var u in members)
        {
            // Identity owns the role mapping; never trust role claims supplied
            // by the management client.
            var user = await _users.FindByIdAsync(u.Id);
            if (user is null) continue;
            result.Add(new
            {
                u.Id, u.Email, u.EmailConfirmed, u.IsDisabled,
                Roles = await _users.GetRolesAsync(user)
            });
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(result);
    }

    [HttpPost("invite")]
    public async Task<IActionResult> Invite([FromBody] InviteTeamMemberRequest request, CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();
        if (!IsDelegatedRole(request.Role))
            return BadRequest(new { error = "ROLE_NOT_ALLOWED" });
        var email = request.Email.Trim().ToLowerInvariant();
        if (await _users.FindByEmailAsync(email) is not null)
            return Conflict(new { error = "USER_ALREADY_EXISTS" });

        // Prevent multiple outstanding invites for the same email/merchant.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({email})::bigint)", ct);
        var existing = await _db.MerchantClaimTokens.AnyAsync(t =>
            t.MerchantId == merchantId && t.InvitedEmail == email &&
            !t.IsUsed && t.ExpiresAtUtc > DateTime.UtcNow, ct);
        if (existing) return Conflict(new { error = "ACTIVE_INVITATION_EXISTS" });

        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _db.MerchantClaimTokens.Add(new MerchantClaimToken(
            merchantId, raw, TimeSpan.FromDays(7), email, request.Role));
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        Response.Headers.CacheControl = "no-store";
        // Until an email sender exists this is an owner-delivered,
        // one-time invitation token (do not expose it in list responses).
        return Ok(new { email, role = request.Role, inviteToken = raw });
    }

    [HttpPut("{userId}/role")]
    public async Task<IActionResult> ChangeRole(
        string userId, [FromBody] UpdateTeamRoleRequest request, CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();
        if (!IsDelegatedRole(request.Role))
            return BadRequest(new { error = "ROLE_NOT_ALLOWED" });
        var user = await _users.FindByIdAsync(userId);
        if (user is null || user.MerchantId != merchantId)
            return NotFound(new { error = "TEAM_MEMBER_NOT_FOUND" });
        var roles = await _users.GetRolesAsync(user);
        if (roles.Contains("Owner") || roles.Contains("SuperAdmin"))
            return Conflict(new { error = "OWNER_ROLE_IMMUTABLE" });

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var prior = roles.Where(IsDelegatedRole).ToArray();
        var removed = await _users.RemoveFromRolesAsync(user, prior);
        if (!removed.Succeeded) return Conflict(new { error = "ROLE_CHANGE_FAILED" });
        var added = await _users.AddToRoleAsync(user, request.Role);
        if (!added.Succeeded) return Conflict(new { error = "ROLE_CHANGE_FAILED" });
        await _users.UpdateSecurityStampAsync(user);
        await tx.CommitAsync(ct);
        return Ok(new { userId = user.Id, role = request.Role });
    }

    [HttpPut("{userId}/status")]
    public async Task<IActionResult> SetStatus(
        string userId, [FromBody] UpdateTeamStatusRequest request, CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();
        var user = await _users.FindByIdAsync(userId);
        if (user is null || user.MerchantId != merchantId)
            return NotFound(new { error = "TEAM_MEMBER_NOT_FOUND" });
        var roles = await _users.GetRolesAsync(user);
        if (roles.Contains("Owner") || roles.Contains("SuperAdmin"))
            return Conflict(new { error = "OWNER_STATUS_IMMUTABLE" });
        if (string.Equals(User.FindFirstValue(ClaimTypes.NameIdentifier), user.Id,
                StringComparison.Ordinal))
            return Conflict(new { error = "CANNOT_DISABLE_SELF" });

        user.IsDisabled = request.Disabled;
        var updated = await _users.UpdateAsync(user);
        if (!updated.Succeeded) return Conflict(new { error = "USER_UPDATE_FAILED" });
        await _users.UpdateSecurityStampAsync(user);
        return Ok(new { userId = user.Id, disabled = user.IsDisabled });
    }
}
