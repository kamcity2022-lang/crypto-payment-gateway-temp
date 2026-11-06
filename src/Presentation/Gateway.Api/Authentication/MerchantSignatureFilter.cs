using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Gateway.Application.Contracts;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.Authentication;

// Resource filters run before MVC model binding. An action filter runs AFTER
// [FromBody] deserialization and can accidentally authenticate an empty body.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireMerchantSignatureAttribute : Attribute, IAsyncResourceFilter
{
    private const int MaxSignedBodyBytes = 1024 * 1024;
    private const long AllowedClockSkewSeconds = 300;

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var ct = context.HttpContext.RequestAborted;

        if (!request.Headers.TryGetValue("X-API-Key", out var apiKeyValues) ||
            !request.Headers.TryGetValue("X-Signature", out var signatureValues) ||
            !request.Headers.TryGetValue("X-Timestamp", out var timestampValues) ||
            !request.Headers.TryGetValue("X-Nonce", out var nonceValues))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Missing required authentication headers." });
            return;
        }

        var apiKey = apiKeyValues.ToString();
        var signature = signatureValues.ToString();
        var nonce = nonceValues.ToString();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(nonce) ||
            nonce.Length > 128 || signature.Length != 64)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid authentication headers." });
            return;
        }

        if (!long.TryParse(timestampValues.ToString(), out var timestampEpoch))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid timestamp format." });
            return;
        }

        // Compare Unix seconds instead of constructing DateTimeOffset from
        // untrusted input (extreme values must return 401 rather than 500).
        var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (timestampEpoch < nowEpoch - AllowedClockSkewSeconds ||
            timestampEpoch > nowEpoch + AllowedClockSkewSeconds)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Request timestamp expired or skewed beyond 5 minutes." });
            return;
        }

        // Check size before reading when Content-Length is available; also
        // enforce an incremental limit for chunked transfer encodings.
        if (request.ContentLength > MaxSignedBodyBytes)
        {
            context.Result = PayloadTooLarge();
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<GatewayDbContext>();
        var protector = context.HttpContext.RequestServices.GetRequiredService<ISecretProtector>();
        var apiKeyHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
        var merchant = await db.Merchants.FirstOrDefaultAsync(
            m => m.ApiKeyHash == apiKeyHash && m.IsActive, ct);

        if (merchant is null)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid merchant credentials." });
            return;
        }

        // Compute SHA-256 over EXACT raw bytes, not decoded/re-encoded text.
        // Reset the buffered stream so MVC input formatters can bind the body.
        request.EnableBuffering(bufferThreshold: 64 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16 * 1024];
        long total = 0;
        int bytesRead;
        while ((bytesRead = await request.Body.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            total += bytesRead;
            if (total > MaxSignedBodyBytes)
            {
                context.Result = PayloadTooLarge();
                return;
            }
            hash.AppendData(buffer, 0, bytesRead);
        }
        request.Body.Position = 0;

        var bodyHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        // The API contract signs the exact path and raw query string.
        var queryString = request.QueryString.HasValue ? request.QueryString.Value : string.Empty;
        var canonical = $"{request.Method.ToUpperInvariant()}\n{request.Path}\n{queryString}\n{timestampEpoch}\n{nonce}\n{bodyHash}";

        var secret = protector.Decrypt(merchant.EncryptedApiKeySecret);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        Span<byte> provided = stackalloc byte[32];
        if (!Convert.TryFromHexString(signature, provided, out var decodedLength) ||
            decodedLength != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid HMAC signature." });
            return;
        }

        // A rejected signature must not consume a nonce. The unique database
        // key closes the race between parallel requests with the same nonce.
        if (await db.MerchantNonces.AnyAsync(
            n => n.MerchantId == merchant.Id && n.Nonce == nonce, ct))
        {
            context.Result = new ConflictObjectResult(new { error = "Replay detected: Nonce has already been used." });
            return;
        }

        db.MerchantNonces.Add(new MerchantNonce(merchant.Id, nonce, TimeSpan.FromMinutes(10)));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            context.Result = new ConflictObjectResult(new { error = "Replay detected: Duplicate concurrent nonce." });
            return;
        }

        context.HttpContext.Items["MerchantId"] = merchant.Id;
        await next();
    }

    private static ObjectResult PayloadTooLarge() =>
        new(new { error = "SIGNED_BODY_TOO_LARGE" })
        {
            StatusCode = StatusCodes.Status413PayloadTooLarge
        };
}
