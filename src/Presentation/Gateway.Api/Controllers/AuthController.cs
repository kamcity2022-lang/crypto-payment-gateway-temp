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
        var now = DateTime.UtcNow;

        // ATOMIC single-use enforcement: Only one concurrent request can succeed in marking the token as used.
        var rowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"MerchantClaimTokens\" SET \"IsUsed\" = true WHERE \"HashedToken\" = {hashedToken} AND \"IsUsed\" = false AND \"ExpiresAtUtc\" > {now}", ct);

        if (rowsAffected == 0)
        {
            return BadRequest(new { error = "Invalid, expired, or already used activation token." });
        }

        // Fetch the record to get the MerchantId now that we own the token
        var tokenRecord = await _dbContext.MerchantClaimTokens
            .FirstOrDefaultAsync(t => t.HashedToken == hashedToken, ct);

        if (tokenRecord == null)
        {
            return BadRequest(new { error = "Token record not found." });
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            MerchantId = tokenRecord.MerchantId,
            EmailConfirmed = false // Do NOT auto-confirm; requires actual email verification flow
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            // Rollback the token usage if user creation fails, allowing retry
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"MerchantClaimTokens\" SET \"IsUsed\" = false WHERE \"Id\" = {tokenRecord.Id}", ct);
            
            return BadRequest(new { error = "User creation failed.", details = result.Errors });
        }

        // Assign the standard Owner role
        await _userManager.AddToRoleAsync(user, "Owner");

        return Ok(new { message = "Account activated successfully. Please verify your email to unlock full access." });
    }

    // TODO: Re-implement in Task 2 with secure [Authorize(Roles = "SuperAdmin")] policy 
    // and proper SuperAdmin bootstrap mechanism. Disabled for now to prevent public exposure.

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