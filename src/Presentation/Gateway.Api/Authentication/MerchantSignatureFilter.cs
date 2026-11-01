using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
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

        // Verify required security headers
        if (!request.Headers.TryGetValue("X-API-Key", out var apiKeyValues) ||
            !request.Headers.TryGetValue("X-Signature", out var signatureValues) ||
            !request.Headers.TryGetValue("X-Timestamp", out var timestampValues) ||
            !request.Headers.TryGetValue("X-Nonce", out var nonceValues))
        {
            context.Result = new ObjectResult(new { error = "Missing required security headers (X-API-Key, X-Signature, X-Timestamp, X-Nonce)." })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
            return;
        }

        var apiKey = apiKeyValues.ToString();
        var signature = signatureValues.ToString();
        var timestampStr = timestampValues.ToString();
        var nonce = nonceValues.ToString();

        // Check timestamp window (5 minutes)
        if (!long.TryParse(timestampStr, out var timestampEpoch))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid timestamp format." });
            return;
        }

        var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestampEpoch).UtcDateTime;
        if (Math.Abs((DateTime.UtcNow - requestTime).TotalMinutes) > 5)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Request timestamp expired or skewed beyond 5 minutes." });
            return;
        }

        var dbContext = context.HttpContext.RequestServices.GetRequiredService<GatewayDbContext>();

        // Hash API Key to look up Merchant
        var apiKeyHash = ComputeSha256(apiKey);
        var merchant = await dbContext.Merchants.FirstOrDefaultAsync(m => m.ApiKeyHash == apiKeyHash && m.IsActive);

        if (merchant == null)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid or inactive merchant API key." });
            return;
        }

        // Read Raw Body for signature computation
        request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync();
            request.Body.Position = 0;
        }

        // Canonical string: METHOD\nPATH\nTIMESTAMP\nNONCE\nBODY_SHA256
        var bodyHash = ComputeSha256(rawBody);
        var canonicalString = $"{request.Method.ToUpperInvariant()}\n{request.Path}\n{timestampStr}\n{nonce}\n{bodyHash}";

        // Compute HMAC using merchant's unencrypted secret (simplified for filter demonstration)
        var expectedSignature = ComputeHmacSha256(canonicalString, merchant.EncryptedApiKeySecret);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(expectedSignature.ToLowerInvariant())))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid request signature." });
            return;
        }

        // Pass authenticated merchant context
        context.HttpContext.Items["MerchantId"] = merchant.Id;

        await next();
    }

    private static string ComputeSha256(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ComputeHmacSha256(string message, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}