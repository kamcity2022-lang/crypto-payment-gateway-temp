using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<WebhookDispatcher> _logger;

    public WebhookDispatcher(GatewayDbContext dbContext, HttpClient httpClient, ISecretProtector secretProtector, ILogger<WebhookDispatcher> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    public async Task DispatchPendingWebhooksAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var pendingEvent = await _dbContext.OutboxEvents
            .FromSqlInterpolated($@"
                SELECT * FROM ""OutboxEvents""
                WHERE ""IsProcessed"" = FALSE AND ""NextAttemptAtUtc"" IS NOT NULL AND ""NextAttemptAtUtc"" <= {now}
                  AND (""LockedUntilUtc"" IS NULL OR ""LockedUntilUtc"" <= {now})
                ORDER BY ""CreatedAtUtc"" LIMIT 1 FOR UPDATE SKIP LOCKED")
            .FirstOrDefaultAsync(cancellationToken);

        if (pendingEvent == null) { await tx.RollbackAsync(cancellationToken); return; }

        pendingEvent.AcquireLease(TimeSpan.FromSeconds(30));
        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        if (!Uri.TryCreate(pendingEvent.DestinationUrl, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Webhook destination {Url} blocked: Only HTTPS is allowed in production.", pendingEvent.DestinationUrl);
            pendingEvent.RecordFailure("Destination blocked: Only HTTPS is allowed.", maxRetries: 1);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        string rawSecret;
        try { rawSecret = _secretProtector.Decrypt(pendingEvent.EncryptedSecretKey); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to decrypt webhook secret."); pendingEvent.RecordFailure("Decryption failure.", maxRetries: 1); await _dbContext.SaveChangesAsync(cancellationToken); return; }

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var payloadBytes = Encoding.UTF8.GetBytes(pendingEvent.Payload);
            var signature = ComputeHmacSha256($"{timestamp}.{pendingEvent.Payload}", rawSecret);

            using var request = new HttpRequestMessage(HttpMethod.Post, pendingEvent.DestinationUrl) { Content = new ByteArrayContent(payloadBytes) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-CryptoGateway-Event-Id", pendingEvent.Id.ToString());
            request.Headers.Add("X-CryptoGateway-Timestamp", timestamp);
            request.Headers.Add("X-CryptoGateway-Signature", signature);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _httpClient.SendAsync(request, cts.Token);

            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                pendingEvent.RecordFailure($"Redirect ({response.StatusCode}) not allowed.");
                _logger.LogWarning("Webhook redirect rejected for event {EventId}", pendingEvent.Id);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return;
            }

            if (response.IsSuccessStatusCode) { pendingEvent.RecordSuccess(); _logger.LogInformation("Webhook {EventId} delivered.", pendingEvent.Id); }
            else { pendingEvent.RecordFailure($"HTTP {(int)response.StatusCode}"); _logger.LogWarning("Webhook failed for {EventId}: {Status}", pendingEvent.Id, response.StatusCode); }
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) { pendingEvent.RecordFailure(ex.Message); _logger.LogError(ex, "Exception delivering webhook {EventId}", pendingEvent.Id); }
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public static string ComputeHmacSha256(string data, string rawSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(rawSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }
}