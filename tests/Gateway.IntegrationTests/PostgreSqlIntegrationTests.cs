using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Gateway.Infrastructure.Security;
using Gateway.Infrastructure.Webhooks;
using Gateway.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public class PostgreSqlIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_gateway_db").WithUsername("postgres").WithPassword("postgres_test_password").Build();

    private GatewayDbContext _dbContext = null!;
    private AesGcmSecretProtector _secretProtector = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Security:MasterEncryptionKey", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF") })
            .Build();
        _secretProtector = new AesGcmSecretProtector(configuration);
        var options = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(_dbContainer.GetConnectionString()).Options;
        _dbContext = new GatewayDbContext(options);
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    [Fact]
    public async Task OutboxDispatcher_AtomicClaimAndDelivery_ShouldSucceed()
    {
        var rawWebhookSecret = "whsec_test_secret_1234567890";
        var encryptedSecret = _secretProtector.Encrypt(rawWebhookSecret);

        var handler = new CapturingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\"}") });
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var destinationUrl = "https://webhook.example.com/webhook-test";
        var outbox = new OutboxEvent(Guid.NewGuid(), "invoice.paid", "{\"invoiceId\":\"123\",\"status\":\"Paid\"}", destinationUrl, encryptedSecret);

        _dbContext.OutboxEvents.Add(outbox);
        await _dbContext.SaveChangesAsync();
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxEvents\" SET \"NextAttemptAtUtc\" = {DateTime.UtcNow.AddMinutes(-5)} WHERE \"Id\" = {outbox.Id}");

        var dispatcher = new WebhookDispatcher(_dbContext, httpClient, _secretProtector, NullLogger<WebhookDispatcher>.Instance);
        await dispatcher.DispatchPendingWebhooksAsync(CancellationToken.None);

        var refreshed = await _dbContext.OutboxEvents.AsNoTracking().FirstOrDefaultAsync(o => o.Id == outbox.Id);
        refreshed.Should().NotBeNull();
        refreshed!.LastError.Should().BeNull();
        refreshed.IsProcessed.Should().BeTrue();

        handler.Captured.Should().HaveCount(1);
        var captured = handler.Captured[0];
        var req = captured.Req;
        var body = captured.Body;

        var expectedBodyBytes = Encoding.UTF8.GetBytes(outbox.Payload);
        body.Should().Equal(expectedBodyBytes);

        req.Headers.TryGetValues("X-CryptoGateway-Event-Id", out var _).Should().BeTrue();
        var timestamp = req.Headers.GetValues("X-CryptoGateway-Timestamp").First();
        var signature = req.Headers.GetValues("X-CryptoGateway-Signature").First();
        var expectedSig = WebhookDispatcher.ComputeHmacSha256($"{timestamp}.{Encoding.UTF8.GetString(body)}", rawWebhookSecret);
        signature.Should().Be(expectedSig);
    }

    [Fact]
    public async Task OutboxDispatcher_ConcurrentWorkers_ShouldNotDuplicateDeliveries()
    {
        var rawWebhookSecret = "whsec_test_secret_1234567890";
        var encryptedSecret = _secretProtector.Encrypt(rawWebhookSecret);

        var handler = new CapturingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\"}") });

        var destinationUrl = "https://webhook.example.com/webhook-concurrent";
        var outbox = new OutboxEvent(Guid.NewGuid(), "invoice.paid", "{\"invoiceId\":\"456\",\"status\":\"Paid\"}", destinationUrl, encryptedSecret);

        _dbContext.OutboxEvents.Add(outbox);
        await _dbContext.SaveChangesAsync();
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxEvents\" SET \"NextAttemptAtUtc\" = {DateTime.UtcNow.AddMinutes(-5)} WHERE \"Id\" = {outbox.Id}");

        using var client1 = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var client2 = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var options = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(_dbContainer.GetConnectionString()).Options;
        await using var db1 = new GatewayDbContext(options);
        await using var db2 = new GatewayDbContext(options);

        var dispatcher1 = new WebhookDispatcher(db1, client1, _secretProtector, NullLogger<WebhookDispatcher>.Instance);
        var dispatcher2 = new WebhookDispatcher(db2, client2, _secretProtector, NullLogger<WebhookDispatcher>.Instance);

        await Task.WhenAll(
            dispatcher1.DispatchPendingWebhooksAsync(CancellationToken.None),
            dispatcher2.DispatchPendingWebhooksAsync(CancellationToken.None)
        );

        await using var verifyDb = new GatewayDbContext(options);
        var refreshed = await verifyDb.OutboxEvents.AsNoTracking().FirstOrDefaultAsync(o => o.Id == outbox.Id);
        refreshed.Should().NotBeNull();
        refreshed!.IsProcessed.Should().BeTrue();
        refreshed.AttemptCount.Should().Be(1);

        handler.Captured.Should().HaveCount(1);
        var captured = handler.Captured[0];
        var req = captured.Req;
        var body = captured.Body;

        var expectedBodyBytes = Encoding.UTF8.GetBytes(outbox.Payload);
        body.Should().Equal(expectedBodyBytes);

        var timestamp = req.Headers.GetValues("X-CryptoGateway-Timestamp").First();
        var signature = req.Headers.GetValues("X-CryptoGateway-Signature").First();
        var eventId = req.Headers.GetValues("X-CryptoGateway-Event-Id").First();
        var expectedSig = WebhookDispatcher.ComputeHmacSha256($"{timestamp}.{Encoding.UTF8.GetString(body)}", rawWebhookSecret);
        signature.Should().Be(expectedSig);
        eventId.Should().Be(outbox.Id.ToString());
    }

    [Fact]
    public async Task CheckoutExpiration_Persistence_ShouldUpdateDatabase()
    {
        var invoice = new Invoice(Guid.NewGuid(), Guid.NewGuid(), "TDepositAddress123", 10.0m, "USD", 1.0m, "DirectPeg", DateTime.UtcNow, 10_000_000L, 50_000L, "https://test.com/cb", "token-xyz-123", TimeSpan.FromSeconds(-10));
        _dbContext.Invoices.Add(invoice);
        await _dbContext.SaveChangesAsync();
        invoice.EvaluateStatus();
        await _dbContext.SaveChangesAsync();
        var fromDb = await _dbContext.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoice.Id);
        fromDb!.Status.Should().Be(InvoiceStatus.Expired);
    }

    [Fact]
    public async Task OutboxDispatcher_RedirectResponse_ShouldNotFollowAndReject()
    {
        var rawWebhookSecret = "whsec_test_secret_redirect";
        var encryptedSecret = _secretProtector.Encrypt(rawWebhookSecret);

        var handler = new CapturingHttpMessageHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
            resp.Headers.Location = new Uri("https://attacker.example.com/webhook-final");
            return resp;
        });
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var destinationUrl = "https://webhook.example.com/webhook-redirect";
        var outbox = new OutboxEvent(Guid.NewGuid(), "invoice.paid", "{\"redirect\":true}", destinationUrl, encryptedSecret);

        _dbContext.OutboxEvents.Add(outbox);
        await _dbContext.SaveChangesAsync();
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxEvents\" SET \"NextAttemptAtUtc\" = {DateTime.UtcNow.AddMinutes(-5)} WHERE \"Id\" = {outbox.Id}");

        var options = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(_dbContainer.GetConnectionString()).Options;
        await using var db = new GatewayDbContext(options);
        var dispatcher = new WebhookDispatcher(db, httpClient, _secretProtector, NullLogger<WebhookDispatcher>.Instance);
        await dispatcher.DispatchPendingWebhooksAsync(CancellationToken.None);

        var refreshed = await db.OutboxEvents.AsNoTracking().FirstOrDefaultAsync(o => o.Id == outbox.Id);
        refreshed.Should().NotBeNull();
        refreshed!.IsProcessed.Should().BeFalse();
        refreshed.LastError.Should().Contain("Redirect");

        handler.Captured.Should().HaveCount(1);
        handler.Captured[0].Req.RequestUri!.AbsolutePath.Should().Be("/webhook-redirect");
    }

    [Fact]
    public async Task OutboxDispatcher_ExpiredLease_StaleWorkerCannotOverwriteNewerSuccessfulDelivery()
    {
        var encrypted = _secretProtector.Encrypt("lease-test-secret");
        var evt = new OutboxEvent(Guid.NewGuid(), "invoice.paid", "{\"verified\":true}",
            "https://webhook.example.com/lease", encrypted);
        _dbContext.OutboxEvents.Add(evt);
        await _dbContext.SaveChangesAsync();

        var blockedHandler = new BlockingWebhookHandler();
        using var firstHttp = new HttpClient(blockedHandler);
        using var secondHttp = new HttpClient(new CapturingHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)));

        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString()).Options;
        await using var firstDb = new GatewayDbContext(options);
        await using var secondDb = new GatewayDbContext(options);

        var first = new WebhookDispatcher(firstDb, firstHttp, _secretProtector,
            NullLogger<WebhookDispatcher>.Instance);
        var second = new WebhookDispatcher(secondDb, secondHttp, _secretProtector,
            NullLogger<WebhookDispatcher>.Instance);

        var firstJob = first.DispatchPendingWebhooksAsync(CancellationToken.None);
        await blockedHandler.WaitUntilStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Emulate time passing beyond a worker's lease while its HTTPS request
        // is still outstanding. Worker 2 acquires and completes the same event.
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"OutboxEvents\" SET \"LockedUntilUtc\" = {DateTime.UtcNow.AddSeconds(-1)} WHERE \"Id\" = {evt.Id}");

        await second.DispatchPendingWebhooksAsync(CancellationToken.None);
        blockedHandler.Release(HttpStatusCode.InternalServerError);
        await firstJob.WaitAsync(TimeSpan.FromSeconds(10));

        await using var verify = new GatewayDbContext(options);
        var persisted = await verify.OutboxEvents.AsNoTracking()
            .SingleAsync(x => x.Id == evt.Id);
        persisted.IsProcessed.Should().BeTrue();
        persisted.AttemptCount.Should().Be(1);
        persisted.LastError.Should().BeNull();
        persisted.NextAttemptAtUtc.Should().BeNull();
        persisted.LockedUntilUtc.Should().BeNull();
    }

    [Fact]
    public async Task OutboxDispatcher_TransportFailure_DoesNotPersistSensitiveExceptionMessage()
    {
        var encrypted = _secretProtector.Encrypt("secret-not-to-log");
        var evt = new OutboxEvent(Guid.NewGuid(), "invoice.paid", "{}",
            "https://webhook.example.com/fail", encrypted);
        _dbContext.OutboxEvents.Add(evt);
        await _dbContext.SaveChangesAsync();

        using var http = new HttpClient(new FailingWebhookHandler(
            "Authorization: secret-not-to-log"));
        var dispatcher = new WebhookDispatcher(_dbContext, http, _secretProtector,
            NullLogger<WebhookDispatcher>.Instance);

        await dispatcher.DispatchPendingWebhooksAsync(CancellationToken.None);
        await using var verify = new GatewayDbContext(
            new DbContextOptionsBuilder<GatewayDbContext>()
                .UseNpgsql(_dbContainer.GetConnectionString()).Options);
        var persisted = await verify.OutboxEvents.AsNoTracking()
            .SingleAsync(x => x.Id == evt.Id);
        persisted.IsProcessed.Should().BeFalse();
        persisted.AttemptCount.Should().Be(1);
        persisted.LastError.Should().Be("Webhook transport failure.");
        persisted.LastError.Should().NotContain("secret-not-to-log");
        persisted.LockedUntilUtc.Should().BeNull();
        persisted.NextAttemptAtUtc.Should().NotBeNull();
    }
}

/// <summary>
/// Isolated test transport that intercepts HTTPS requests without needing real TLS certificates.
/// This satisfies "isolated test transport" and "preserve production HTTPS-only policy".
/// </summary>
public sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<(HttpRequestMessage Req, byte[] Body)> Captured { get; } = new();

    public CapturingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content != null ? await request.Content.ReadAsByteArrayAsync(cancellationToken) : Array.Empty<byte>();
        Captured.Add((request, body));
        return _responder(request);
    }
}
internal sealed class BlockingWebhookHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource<bool> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<HttpStatusCode> _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitUntilStartedAsync() => _started.Task;
    public void Release(HttpStatusCode result) => _release.TrySetResult(result);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _started.TrySetResult(true);
        var result = await _release.Task.WaitAsync(cancellationToken);
        return new HttpResponseMessage(result);
    }
}

internal sealed class FailingWebhookHandler : HttpMessageHandler
{
    private readonly string _message;
    public FailingWebhookHandler(string message) => _message = message;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException(_message);
}
