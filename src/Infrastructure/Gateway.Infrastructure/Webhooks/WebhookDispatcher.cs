using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.Contracts;
using Gateway.Domain.Entities;
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

    public WebhookDispatcher(
        GatewayDbContext dbContext,
        HttpClient httpClient,
        ISecretProtector secretProtector,
        ILogger<WebhookDispatcher> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    public async Task DispatchPendingWebhooksAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        OutboxEvent pendingEvent;

        await using (var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var claimed = await _dbContext.OutboxEvents
                .FromSqlInterpolated($@"
                    SELECT * FROM ""OutboxEvents""
                    WHERE ""IsProcessed"" = FALSE AND ""NextAttemptAtUtc"" IS NOT NULL
                      AND ""NextAttemptAtUtc"" <= {now}
                      AND (""LockedUntilUtc"" IS NULL OR ""LockedUntilUtc"" <= {now})
                    ORDER BY ""CreatedAtUtc"" LIMIT 1 FOR UPDATE SKIP LOCKED")
                .FirstOrDefaultAsync(cancellationToken);

            if (claimed is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return;
            }

            claimed.AcquireLease(TimeSpan.FromSeconds(30));
            await _dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            // PostgreSQL stores timestamps at microsecond precision. Reload
            // to obtain the exact persisted lease token for the later CAS.
            await _dbContext.Entry(claimed).ReloadAsync(cancellationToken);
            pendingEvent = claimed;
        }

        // Treat the exact persisted lease expiry as a fencing token. A second
        // worker may acquire the event if this worker stalls beyond its lease.
        // The first worker must never overwrite the second worker's outcome.
        var leaseToken = pendingEvent.LockedUntilUtc!.Value;
        _dbContext.Entry(pendingEvent).State = EntityState.Detached;

        if (!Uri.TryCreate(pendingEvent.DestinationUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Webhook {EventId} blocked: HTTPS required.", pendingEvent.Id);
            pendingEvent.RecordFailure("Destination blocked: HTTPS required.", maxRetries: 1);
            await PersistOutcomeIfLeaseOwnedAsync(pendingEvent, leaseToken, cancellationToken);
            return;
        }

        string rawSecret;
        try
        {
            rawSecret = _secretProtector.Decrypt(pendingEvent.EncryptedSecretKey);
        }
        catch (Exception ex)
        {
            // Do not copy exception messages into persisted delivery history or
            // logs: provider/transport messages may include credentials or URLs.
            _logger.LogError("Webhook {EventId} secret decryption failed ({ExceptionType}).",
                pendingEvent.Id, ex.GetType().Name);
            pendingEvent.RecordFailure("Webhook secret decryption failed.", maxRetries: 1);
            await PersistOutcomeIfLeaseOwnedAsync(pendingEvent, leaseToken, cancellationToken);
            return;
        }

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var payloadBytes = Encoding.UTF8.GetBytes(pendingEvent.Payload);
            var signature = ComputeHmacSha256($"{timestamp}.{pendingEvent.Payload}", rawSecret);

            using var request = new HttpRequestMessage(HttpMethod.Post, pendingEvent.DestinationUrl)
            {
                Content = new ByteArrayContent(payloadBytes)
            };
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-CryptoGateway-Event-Id", pendingEvent.Id.ToString());
            request.Headers.Add("X-CryptoGateway-Timestamp", timestamp);
            request.Headers.Add("X-CryptoGateway-Signature", signature);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                pendingEvent.RecordFailure($"Redirect ({(int)response.StatusCode}) not allowed.");
                _logger.LogWarning("Webhook redirect rejected for event {EventId}", pendingEvent.Id);
            }
            else if (response.IsSuccessStatusCode)
            {
                pendingEvent.RecordSuccess();
                _logger.LogInformation("Webhook {EventId} delivered.", pendingEvent.Id);
            }
            else
            {
                pendingEvent.RecordFailure($"HTTP {(int)response.StatusCode}");
                _logger.LogWarning("Webhook failed for {EventId}: {Status}",
                    pendingEvent.Id, response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            pendingEvent.RecordFailure("Webhook delivery timeout.");
            _logger.LogWarning("Webhook {EventId} timed out.", pendingEvent.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // On shutdown, keep the lease. Another worker will retry it after
            // expiry; don't fabricate an unsuccessful delivery attempt.
            throw;
        }
        catch (Exception ex)
        {
            pendingEvent.RecordFailure("Webhook transport failure.");
            _logger.LogWarning("Webhook {EventId} failed ({ExceptionType}).",
                pendingEvent.Id, ex.GetType().Name);
        }

        await PersistOutcomeIfLeaseOwnedAsync(pendingEvent, leaseToken, cancellationToken);
    }

    private async Task PersistOutcomeIfLeaseOwnedAsync(
        OutboxEvent pendingEvent, DateTime leaseToken, CancellationToken ct)
    {
        // ExecuteUpdate atomically checks the lease before writing the result.
        // A slow/stale worker cannot clobber a newer lease or manual replay.
        var attempts = pendingEvent.AttemptCount;
        var processed = pendingEvent.IsProcessed;
        var next = pendingEvent.NextAttemptAtUtc;
        var locked = pendingEvent.LockedUntilUtc;
        var error = pendingEvent.LastError;

        var changed = await _dbContext.OutboxEvents
            .Where(e => e.Id == pendingEvent.Id &&
                        !e.IsProcessed && e.LockedUntilUtc == leaseToken)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(e => e.AttemptCount, attempts)
                .SetProperty(e => e.IsProcessed, processed)
                .SetProperty(e => e.NextAttemptAtUtc, next)
                .SetProperty(e => e.LockedUntilUtc, locked)
                .SetProperty(e => e.LastError, error), ct);

        if (changed == 0)
        {
            _logger.LogWarning(
                "Discarded stale webhook outcome for {EventId}: lease no longer owned.",
                pendingEvent.Id);
        }
    }

    public static string ComputeHmacSha256(string data, string rawSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(rawSecret));
        return Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }
}
