using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly GatewayDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;

    public AuthController(GatewayDbContext dbContext, UserManager<ApplicationUser> userManager)
    {
        _dbContext = dbContext;
        _userManager = userManager;
    }

    [HttpPost("activate")]
    public async Task<IActionResult> ActivateAccount([FromBody] ActivateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password) || string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { error = "Email, password, and token are required." });
        }

        var hashedToken = HashToken(request.Token);
        var tokenRecord = await _dbContext.MerchantClaimTokens
            .FirstOrDefaultAsync(t => t.HashedToken == hashedToken, ct);

        if (tokenRecord == null || tokenRecord.IsUsed || tokenRecord.ExpiresAtUtc < DateTime.UtcNow)
        {
            return BadRequest(new { error = "Invalid, expired, or already used activation token." });
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            MerchantId = tokenRecord.MerchantId,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return BadRequest(new { error = "User creation failed.", details = result.Errors });
        }

        tokenRecord.MarkAsUsed();
        await _dbContext.SaveChangesAsync(ct);

        return Ok(new { message = "Account activated successfully. You can now log in." });
    }

    [HttpPost("admin/merchants/{merchantId:guid}/invite")]
    public async Task<IActionResult> GenerateInvite(Guid merchantId, CancellationToken ct)
    {
        // Note: Full RBAC/Authorization for this endpoint will be enforced in Task 2.
        var merchant = await _dbContext.Merchants.FindAsync(new object[] { merchantId }, ct);
        if (merchant == null)
        {
            return NotFound(new { error = "Merchant not found." });
        }

        var plainToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var tokenRecord = new MerchantClaimToken(merchantId, plainToken, TimeSpan.FromDays(7));
        
        _dbContext.MerchantClaimTokens.Add(tokenRecord);
        await _dbContext.SaveChangesAsync(ct);

        return Ok(new { 
            message = "Invite token generated securely. Provide this to the user out-of-band (e.g., verified email).",
            inviteToken = plainToken 
        });
    }

    private static string HashToken(string token)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
}

public class ActivateRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}