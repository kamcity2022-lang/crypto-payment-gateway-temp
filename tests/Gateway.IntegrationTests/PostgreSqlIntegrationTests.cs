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

// Isolated test transport that intercepts HTTPS requests without needing real TLS certificates.
// This satisfies "isolated test transport" and "preserve production HTTPS-only policy".
public sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<(HttpRequestMessage Req, byte[] Body)> Captured { get; } = new();

    public CapturingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content != null ? await request.Content.ReadAsByteArrayAsync(ct) : Array.Empty<byte>();
        Captured.Add((request, body));
        return _responder(request);
    }
}

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

        // Production requires HTTPS. The handler intercepts it before network I/O.
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
        var (req, body) = handler.Captured[0];
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
        refreshed.AttemptCount.Should().Be(1); // Exactly one worker claimed it via FOR UPDATE SKIP LOCKED

        handler.Captured.Should().HaveCount(1); // Exactly one HTTP request was made
        var (req, body) = handler.Captured[0];
        
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

        // Only one request made (to the original destination), redirect was NOT followed
        handler.Captured.Should().HaveCount(1);
        handler.Captured[0].Req.RequestUri!.AbsolutePath.Should().Be("/webhook-redirect");
    }
}