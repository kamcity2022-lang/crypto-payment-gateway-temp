using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.Contracts;
using Gateway.Infrastructure.Security;
using Gateway.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gateway.Infrastructure.Webhooks;

public class WebhookDispatcher : IWebhookDispatcher
{
    private readonly GatewayDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebhookDispatcher> _logger;

    public WebhookDispatcher(
        GatewayDbContext dbContext,
        HttpClient httpClient,
        ILogger<WebhookDispatcher> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task DispatchPendingWebhooksAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var pendingEvents = await _dbContext.OutboxEvents
            .Where(o => !o.IsProcessed && o.NextAttemptAtUtc != null && o.NextAttemptAtUtc <= now)
            .OrderBy(o => o.CreatedAtUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        if (!pendingEvents.Any())
            return;

        foreach (var ev in pendingEvents)
        {
            // 1. Enforce strict SSRF protection before initiating HTTP socket
            if (!SsrfValidator.IsDestinationSafe(ev.DestinationUrl))
            {
                _logger.LogWarning("Webhook destination {Url} blocked by SSRF filter for event {EventId}", ev.DestinationUrl, ev.Id);
                ev.RecordFailure("Destination blocked by SSRF validation rules.", maxRetries: 1);
                continue;
            }

            try
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                var signature = ComputeHmacSha256($"{timestamp}.{ev.Payload}", ev.SecretKey);

                using var request = new HttpRequestMessage(HttpMethod.Post, ev.DestinationUrl)
                {
                    Content = new StringContent(ev.Payload, Encoding.UTF8, "application/json")
                };

                // Add required security webhook headers
                request.Headers.Add("X-CryptoGateway-Event-Id", ev.Id.ToString());
                request.Headers.Add("X-CryptoGateway-Timestamp", timestamp);
                request.Headers.Add("X-CryptoGateway-Signature", signature);

                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    ev.RecordSuccess();
                    _logger.LogInformation("Webhook event {EventId} successfully delivered to {Url}", ev.Id, ev.DestinationUrl);
                }
                else
                {
                    var errorMsg = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                    ev.RecordFailure(errorMsg);
                    _logger.LogWarning("Webhook delivery failed for event {EventId}: {Status}", ev.Id, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                ev.RecordFailure(ex.Message);
                _logger.LogError(ex, "Exception while delivering webhook event {EventId}", ev.Id);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string ComputeHmacSha256(string data, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}