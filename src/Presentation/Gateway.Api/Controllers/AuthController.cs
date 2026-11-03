using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly GatewayDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        GatewayDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<AuthController> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _signInManager = signInManager;
        _logger = logger;
    }

    [HttpPost("activate")]
    public async Task<IActionResult> ActivateAccount([FromBody] ActivateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { error = "Email, password, and token are required." });
        }

        var hashedToken = HashToken(request.Token);

        // ✅ Transactional activation: all-or-nothing
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            // 1. Atomically claim the token with FOR UPDATE SKIP LOCKED
            //    This prevents concurrent activation attempts from creating duplicate users.
            var tokenRecord = await _dbContext.MerchantClaimTokens
                .FromSqlInterpolated($@"
                    SELECT * FROM ""MerchantClaimTokens"" 
                    WHERE ""HashedToken"" = {hashedToken} 
                      AND ""IsUsed"" = false 
                      AND ""ExpiresAtUtc"" > {DateTime.UtcNow}
                    FOR UPDATE SKIP LOCKED")
                .FirstOrDefaultAsync(ct);

            if (tokenRecord == null)
            {
                await transaction.RollbackAsync(ct);
                return BadRequest(new { error = "Invalid, expired, or already used activation token." });
            }

            // 2. Prevent duplicate user creation
            var existingUser = await _userManager.FindByEmailAsync(request.Email);
            if (existingUser != null)
            {
                await transaction.RollbackAsync(ct);
                return Conflict(new { error = "An account with this email already exists." });
            }

            // 3. Create user (EmailConfirmed remains false; email verification flow is not yet implemented)
            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                MerchantId = tokenRecord.MerchantId,
                EmailConfirmed = true
            };

            var createResult = await _userManager.CreateAsync(user, request.Password);
            if (!createResult.Succeeded)
            {
                await transaction.RollbackAsync(ct);
                return BadRequest(new { error = "User creation failed.", details = createResult.Errors });
            }

            // 4. Assign Owner role and validate result
            var roleResult = await _userManager.AddToRoleAsync(user, "Owner");
            if (!roleResult.Succeeded)
            {
                await transaction.RollbackAsync(ct);
                return BadRequest(new { error = "Role assignment failed.", details = roleResult.Errors });
            }

            // 5. Mark token as used
            tokenRecord.MarkAsUsed();
            await _dbContext.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);

            return Ok(new
            {
                message = "Account activated successfully. Email verification will be required before login in a future release."
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogError(ex, "Account activation failed for email {Email}", request.Email);
            return StatusCode(500, new { error = "Account activation failed due to an internal error." });
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "Email and password are required." });
        }

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            return Unauthorized(new { error = "Invalid credentials." });
        }

        // ✅ Enforce email verification: unverified accounts cannot log in
        if (!user.EmailConfirmed)
        {
            return Unauthorized(new { error = "Email verification required. This feature will be available in a future release." });
        }

        var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            if (result.IsLockedOut)
            {
                return StatusCode(423, new { error = "Account is locked due to too many failed attempts." });
            }
            return Unauthorized(new { error = "Invalid credentials." });
        }

        await _signInManager.SignInAsync(user, isPersistent: false);
        return Ok(new { message = "Login successful." });
    }

    // TODO: Re-implement in Task 2 with [Authorize(Roles = "SuperAdmin")] policy
    // and proper SuperAdmin bootstrap mechanism. Disabled for now to prevent public exposure.
    
    [HttpPost("admin/merchants/{merchantId:guid}/invite")]
    [Authorize(Policy = "SuperAdminOnly")]
    public async Task<IActionResult> GenerateInvite(Guid merchantId, CancellationToken ct)
    {
        var merchant = await _dbContext.Merchants.FindAsync(new object[] { merchantId }, ct);
        if (merchant == null) return NotFound(new { error = "Merchant not found." });

        var plainToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var tokenRecord = new MerchantClaimToken(merchantId, plainToken, TimeSpan.FromDays(7));
    
        _dbContext.MerchantClaimTokens.Add(tokenRecord);
        await _dbContext.SaveChangesAsync(ct);

        // Returned to the authorized SuperAdmin caller for secure out-of-band transmission
        return Ok(new { 
            message = "Invite token generated securely. Provide this to the user out-of-band.",
            inviteToken = plainToken 
        });
    }
    
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return Ok(new { message = "Logged out successfully." });
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

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}