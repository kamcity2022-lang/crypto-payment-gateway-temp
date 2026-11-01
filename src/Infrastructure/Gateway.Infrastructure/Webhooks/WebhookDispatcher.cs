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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gateway.Infrastructure.Webhooks;

public class WebhookDispatcher : IWebhookDispatcher
{
    private readonly GatewayDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly bool _allowLoopbackForTesting;

    public WebhookDispatcher(
        GatewayDbContext dbContext,
        HttpClient httpClient,
        ISecretProtector secretProtector,
        ILogger<WebhookDispatcher> logger,
        IConfiguration? configuration = null)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _secretProtector = secretProtector;
        _logger = logger;
        _allowLoopbackForTesting = configuration?.GetValue<bool>("Testing:AllowLoopbackWebhooks") ?? false;
    }

    public async Task DispatchPendingWebhooksAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        // Atomic claim using row-level locking (FOR UPDATE SKIP LOCKED) to guarantee multi-worker safety
        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var pendingEvent = await _dbContext.OutboxEvents
            .FromSqlInterpolated($@"
                SELECT * FROM ""OutboxEvents""
                WHERE ""IsProcessed"" = FALSE
                  AND ""NextAttemptAtUtc"" IS NOT NULL
                  AND ""NextAttemptAtUtc"" <= {now}
                  AND (""LockedUntilUtc"" IS NULL OR ""LockedUntilUtc"" <= {now})
                ORDER BY ""CreatedAtUtc""
                LIMIT 1
                FOR UPDATE SKIP LOCKED")
            .FirstOrDefaultAsync(cancellationToken);

        if (pendingEvent == null)
        {
            await tx.RollbackAsync(cancellationToken);
            return;
        }

        // Lease duration: 30 seconds
        pendingEvent.AcquireLease(TimeSpan.FromSeconds(30));
        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        // Security check: SSRF Validation
        if (!SsrfValidator.IsDestinationSafe(pendingEvent.DestinationUrl, allowHttp: _allowLoopbackForTesting, allowLoopbackForTesting: _allowLoopbackForTesting))
        {
            _logger.LogWarning("Webhook destination {Url} blocked by SSRF filter for event {EventId}", pendingEvent.DestinationUrl, pendingEvent.Id);
            pendingEvent.RecordFailure("Destination blocked by SSRF validation rules.", maxRetries: 1);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        // Decrypt webhook secret immediately before signing
        string rawSecret;
        try
        {
            rawSecret = _secretProtector.Decrypt(pendingEvent.EncryptedSecretKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt webhook secret for event {EventId}. Aborting delivery.", pendingEvent.Id);
            pendingEvent.RecordFailure("Decryption failure for webhook secret.", maxRetries: 1);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var payloadBytes = Encoding.UTF8.GetBytes(pendingEvent.Payload);
            var signaturePayload = $"{timestamp}.{pendingEvent.Payload}";
            var signature = ComputeHmacSha256(signaturePayload, rawSecret);

            using var request = new HttpRequestMessage(HttpMethod.Post, pendingEvent.DestinationUrl)
            {
                Content = new ByteArrayContent(payloadBytes)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            request.Headers.Add("X-CryptoGateway-Event-Id", pendingEvent.Id.ToString());
            request.Headers.Add("X-CryptoGateway-Timestamp", timestamp);
            request.Headers.Add("X-CryptoGateway-Signature", signature);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _httpClient.SendAsync(request, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                pendingEvent.RecordSuccess();
                _logger.LogInformation("Webhook event {EventId} successfully delivered.", pendingEvent.Id);
            }
            else
            {
                var errorMsg = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                pendingEvent.RecordFailure(errorMsg);
                _logger.LogWarning("Webhook delivery failed for event {EventId}: {Status}", pendingEvent.Id, response.StatusCode);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            pendingEvent.RecordFailure(ex.Message);
            _logger.LogError(ex, "Exception while delivering webhook event {EventId}", pendingEvent.Id);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public static string ComputeHmacSha256(string data, string rawSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(rawSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    
    private static HttpClient CreateSecureHttpClient(bool allowLoopback)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // Strictly disable automatic redirects
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, cancellationToken);
                var targetIp = entry.AddressList.FirstOrDefault();

                if (targetIp == null || (!allowLoopback && SsrfValidator.IsProhibitedIp(targetIp)))
                {
                    throw new SocketException((int)SocketError.AccessDenied);
                }

                var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(targetIp, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }
}