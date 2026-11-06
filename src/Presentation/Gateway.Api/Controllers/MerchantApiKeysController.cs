using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Api.Auditing;
using Gateway.Application.Contracts;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

public sealed class CreateMerchantApiKeyRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;
}

public sealed class MerchantApiKeyInfoDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? RevokedAtUtc { get; init; }

    public static MerchantApiKeyInfoDto From(MerchantApiKey key) => new()
    {
        Id = key.Id, Name = key.Name, CreatedAtUtc = key.CreatedAtUtc,
        RevokedAtUtc = key.RevokedAtUtc
    };
}

[ApiController]
[Route("api/v1/dashboard/api-keys")]
[Authorize(Policy = "MerchantDeveloper")]
public sealed class MerchantApiKeysController : ControllerBase
{
    private readonly GatewayDbContext _db;
    private readonly ISecretProtector _protector;

    public MerchantApiKeysController(GatewayDbContext db, ISecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    private bool TryMerchant(out Guid merchantId)
    {
        merchantId = Guid.Empty;
        return HttpContext.Items.TryGetValue("ResolvedMerchantId", out var value) &&
            value is Guid id && (merchantId = id) != Guid.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();
        Response.Headers.CacheControl = "no-store";
        return Ok(await _db.MerchantApiKeys.AsNoTracking()
            .Where(k => k.MerchantId == merchantId)
            .OrderByDescending(k => k.CreatedAtUtc)
            .Select(k => new MerchantApiKeyInfoDto
            {
                Id = k.Id, Name = k.Name, CreatedAtUtc = k.CreatedAtUtc,
                RevokedAtUtc = k.RevokedAtUtc
            }).ToListAsync(ct));
    }

    [HttpPost]
    [Authorize(Policy = "MerchantAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateMerchantApiKeyRequest request, CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "KEY_NAME_REQUIRED" });

        var rawKey = "cpg_live_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var rawSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

        var key = new MerchantApiKey(merchantId, request.Name, hash, _protector.Encrypt(rawSecret));
        _db.MerchantApiKeys.Add(key);
        AuditTrail.Record(_db, User, merchantId, "api_key.created", "MerchantApiKey", key.Id.ToString());
        await _db.SaveChangesAsync(ct);

        Response.Headers.CacheControl = "no-store";
        return CreatedAtAction(nameof(List), new
        {
            id = key.Id,
            name = key.Name,
            createdAtUtc = key.CreatedAtUtc,
            apiKey = rawKey,
            apiSecret = rawSecret
        });
    }

    [HttpPost("{keyId:guid}/rotate")]
    [Authorize(Policy = "MerchantAdmin")]
    public async Task<IActionResult> Rotate(Guid keyId, CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var key = await _db.MerchantApiKeys
            .FromSqlInterpolated($@"SELECT * FROM ""MerchantApiKeys""
                WHERE ""Id"" = {keyId} AND ""MerchantId"" = {merchantId}
                FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (key is null) return NotFound(new { error = "KEY_NOT_FOUND" });
        if (key.RevokedAtUtc is not null) return Conflict(new { error = "KEY_ALREADY_REVOKED" });

        var rawKey = "cpg_live_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var rawSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

        key.Revoke();
        var successor = new MerchantApiKey(merchantId, key.Name, hash, _protector.Encrypt(rawSecret));
        _db.MerchantApiKeys.Add(successor);
        AuditTrail.Record(_db, User, merchantId, "api_key.rotated", "MerchantApiKey", key.Id.ToString());
        AuditTrail.Record(_db, User, merchantId, "api_key.created", "MerchantApiKey", successor.Id.ToString());
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            id = successor.Id,
            replacedKeyId = key.Id,
            name = successor.Name,
            createdAtUtc = successor.CreatedAtUtc,
            apiKey = rawKey,
            apiSecret = rawSecret
        });
    }

    [HttpPost("{keyId:guid}/revoke")]
    [Authorize(Policy = "MerchantAdmin")]
    public async Task<IActionResult> Revoke(Guid keyId, CancellationToken ct)
    {
        if (!TryMerchant(out var merchantId)) return Forbid();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var count = await _db.MerchantApiKeys
            .Where(k => k.Id == keyId && k.MerchantId == merchantId && k.RevokedAtUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAtUtc, DateTime.UtcNow), ct);

        if (count == 0)
            return NotFound(new { error = "ACTIVE_KEY_NOT_FOUND" });

        AuditTrail.Record(_db, User, merchantId, "api_key.revoked", "MerchantApiKey", keyId.ToString());
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return NoContent();
    }
}
