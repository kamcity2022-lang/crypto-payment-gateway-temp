using System;
using System.IO;
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

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireMerchantSignatureAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;

        // 1. Validate required headers
        if (!request.Headers.TryGetValue("X-API-Key", out var apiKeyVals) ||
            !request.Headers.TryGetValue("X-Signature", out var sigVals) ||
            !request.Headers.TryGetValue("X-Timestamp", out var tsVals) ||
            !request.Headers.TryGetValue("X-Nonce", out var nonceVals))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Missing required authentication headers." });
            return;
        }

        var apiKey = apiKeyVals.ToString();
        var signature = sigVals.ToString();
        var nonce = nonceVals.ToString();

        // 2. Validate timestamp & clock skew (5 minutes)
        if (!long.TryParse(tsVals.ToString(), out var timestampEpoch))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid timestamp format." });
            return;
        }

        var reqTime = DateTimeOffset.FromUnixTimeSeconds(timestampEpoch).UtcDateTime;
        if (Math.Abs((DateTime.UtcNow - reqTime).TotalMinutes) > 5)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Request timestamp expired or skewed beyond 5 minutes." });
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<GatewayDbContext>();
        var protector = context.HttpContext.RequestServices.GetRequiredService<ISecretProtector>();

        // 3. Resolve merchant by API Key Hash
        using var sha = SHA256.Create();
        var keyHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
        var merchant = await db.Merchants.FirstOrDefaultAsync(m => m.ApiKeyHash == keyHash && m.IsActive);

        if (merchant == null)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid merchant credentials." });
            return;
        }

        // 4. Canonical Request Verification
        request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync();
            request.Body.Position = 0;
        }

        var bodyHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        var queryString = request.QueryString.HasValue ? request.QueryString.Value : string.Empty;
        var canonical = $"{request.Method.ToUpperInvariant()}\n{request.Path}\n{queryString}\n{timestampEpoch}\n{nonce}\n{bodyHash}";

        var secret = protector.Decrypt(merchant.EncryptedApiKeySecret);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expectedSig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expectedSig)))
        {
            // Invalid signature must NOT consume nonce
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid HMAC signature." });
            return;
        }

        // 5. Only AFTER successful signature verification, atomically persist the nonce
        var existingNonce = await db.MerchantNonces
            .AnyAsync(n => n.MerchantId == merchant.Id && n.Nonce == nonce);
        if (existingNonce)
        {
            context.Result = new ConflictObjectResult(new { error = "Replay detected: Nonce has already been used." });
            return;
        }
        db.MerchantNonces.Add(new MerchantNonce(merchant.Id, nonce, TimeSpan.FromMinutes(10)));
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            context.Result = new ConflictObjectResult(new { error = "Replay detected: Duplicate concurrent nonce." });
            return;
        }

        // ✅ FIX: Only set the trusted MerchantId. Do NOT inject human-user claims or roles.
        context.HttpContext.Items["MerchantId"] = merchant.Id;
        
        await next();
    }
}