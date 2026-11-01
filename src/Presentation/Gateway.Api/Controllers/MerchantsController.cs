using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Api.Authentication;
using Gateway.Application.Contracts;
using Gateway.Application.DTOs;
using Gateway.Blockchain.Tron;
using Gateway.Domain.Entities;
using Gateway.Infrastructure.Security;
using Gateway.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/merchants")]
public class MerchantsController : ControllerBase
{
    private readonly GatewayDbContext _dbContext;
    private readonly ISecretProtector _secretProtector;

    public MerchantsController(GatewayDbContext dbContext, ISecretProtector secretProtector)
    {
        _dbContext = dbContext;
        _secretProtector = secretProtector;
    }

    [HttpPost("register")]
    public async Task<IActionResult> RegisterMerchant([FromBody] RegisterMerchantRequest request, CancellationToken ct)
    {
        if (!SsrfValidator.IsDestinationSafe(request.WebhookUrl))
        {
            return BadRequest(new { error = "WebhookUrl is rejected by security validation rules." });
        }

        // Validate that TronAccountXPub is structurally valid by testing address derivation at index 0
        try
        {
            var derivationService = new TronAddressDerivationService(request.TronAccountXPub);
            var testAddress = derivationService.DeriveAddress(0);
            if (!testAddress.StartsWith("T", StringComparison.Ordinal) || testAddress.Length != 34)
            {
                return BadRequest(new { error = "Invalid TronAccountXPub: Derived address does not match standard TRON specifications." });
            }
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Invalid TronAccountXPub format: {ex.Message}" });
        }

        // Generate cryptographically secure API credentials
        var rawApiKey = $"cpg_live_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
        var rawApiSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var rawWebhookSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        using var sha = SHA256.Create();
        var apiKeyHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(rawApiKey))).ToLowerInvariant();

        var encryptedApiSecret = _secretProtector.Encrypt(rawApiSecret);
        var encryptedWebhookSecret = _secretProtector.Encrypt(rawWebhookSecret);

        var merchant = new Merchant(
            id: Guid.NewGuid(),
            name: request.Name.Trim(),
            apiKeyHash: apiKeyHash,
            encryptedApiKeySecret: encryptedApiSecret,
            encryptedWebhookSecret: encryptedWebhookSecret
        );

        var wallet = new Wallet(
            merchantId: merchant.Id,
            accountXPub: request.TronAccountXPub.Trim(),
            network: "TRON"
        );

        _dbContext.Merchants.Add(merchant);
        _dbContext.Wallets.Add(wallet);

        await _dbContext.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetProfile), new { }, new RegisterMerchantResponse
        {
            MerchantId = merchant.Id,
            Name = merchant.Name,
            ApiKey = rawApiKey,
            ApiSecret = rawApiSecret,
            WebhookSecret = rawWebhookSecret,
            TronAccountXPub = wallet.AccountXPub,
            CreatedAtUtc = merchant.CreatedAtUtc
        });
    }

    [HttpGet("me")]
    [RequireMerchantSignature]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        var merchant = await _dbContext.Merchants
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == merchantId, ct);

        if (merchant == null)
            return NotFound(new { error = "Merchant profile not found." });

        var wallets = await _dbContext.Wallets
            .AsNoTracking()
            .Where(w => w.MerchantId == merchantId)
            .Select(w => new MerchantWalletDto
            {
                WalletId = w.Id,
                Network = w.Network,
                TotalAddressesAllocated = w.LastDerivationIndex,
                CreatedAtUtc = w.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Ok(new MerchantProfileDto
        {
            MerchantId = merchant.Id,
            Name = merchant.Name,
            IsActive = merchant.IsActive,
            CreatedAtUtc = merchant.CreatedAtUtc,
            Wallets = wallets
        });
    }
}