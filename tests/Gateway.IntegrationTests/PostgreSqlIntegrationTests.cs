using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Gateway.IntegrationTests;

public class PostgreSqlIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_gateway_db")
        .WithUsername("postgres")
        .WithPassword("postgres_test_password")
        .Build();

    private WireMockServer _wireMockServer = null!;
    private GatewayDbContext _dbContext = null!;
    private AesGcmSecretProtector _secretProtector = null!;
    private IConfiguration _configuration = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        
        // Start WireMock listening explicitly on IPv4 loopback
        _wireMockServer = WireMockServer.Start();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Security:MasterEncryptionKey", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"),
                new KeyValuePair<string, string?>("Testing:AllowLoopbackWebhooks", "true")
            })
            .Build();

        _secretProtector = new AesGcmSecretProtector(_configuration);

        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;

        _dbContext = new GatewayDbContext(options);
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        _wireMockServer.Stop();
        _wireMockServer.Dispose();
        await _dbContainer.DisposeAsync();
    }

    [Fact]
    public async Task OutboxDispatcher_AtomicClaimAndDelivery_ShouldSucceed()
    {
        var rawWebhookSecret = "whsec_test_secret_1234567890";
        var encryptedSecret = _secretProtector.Encrypt(rawWebhookSecret);

        _wireMockServer
            .Given(Request.Create().WithPath("/webhook-test").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithBody("{\"status\":\"ok\"}"));

        var destinationUrl = $"http://127.0.0.1:{_wireMockServer.Port}/webhook-test";

        var outbox = new OutboxEvent(
            Guid.NewGuid(),
            "invoice.paid",
            "{\"invoiceId\":\"123\",\"status\":\"Paid\"}",
            destinationUrl,
            encryptedSecret
        );

        _dbContext.OutboxEvents.Add(outbox);
        await _dbContext.SaveChangesAsync();

        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"OutboxEvents\" SET \"NextAttemptAtUtc\" = {DateTime.UtcNow.AddMinutes(-5)} WHERE \"Id\" = {outbox.Id}");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var dispatcher = new WebhookDispatcher(_dbContext, httpClient, _secretProtector, NullLogger<WebhookDispatcher>.Instance, _configuration);

        await dispatcher.DispatchPendingWebhooksAsync(CancellationToken.None);

        var refreshed = await _dbContext.OutboxEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == outbox.Id);

        refreshed.Should().NotBeNull();
        refreshed!.LastError.Should().BeNull(because: $"Dispatch should not record an error, but encountered: {refreshed.LastError}");
        refreshed.IsProcessed.Should().BeTrue();
    }

    [Fact]
    public async Task OutboxDispatcher_ConcurrentWorkers_ShouldNotDuplicateDeliveries()
    {
        var rawWebhookSecret = "whsec_test_secret_1234567890";
        var encryptedSecret = _secretProtector.Encrypt(rawWebhookSecret);

        _wireMockServer
            .Given(Request.Create().WithPath("/webhook-concurrent").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithBody("{\"status\":\"ok\"}"));

        var destinationUrl = $"http://127.0.0.1:{_wireMockServer.Port}/webhook-concurrent";

        var outbox = new OutboxEvent(
            Guid.NewGuid(),
            "invoice.paid",
            "{\"invoiceId\":\"456\",\"status\":\"Paid\"}",
            destinationUrl,
            encryptedSecret
        );

        _dbContext.OutboxEvents.Add(outbox);
        await _dbContext.SaveChangesAsync();

        // تضمین این که NextAttemptAtUtc از زمان جاری کوئری عقب‌تر است
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"OutboxEvents\" SET \"NextAttemptAtUtc\" = {DateTime.UtcNow.AddMinutes(-5)} WHERE \"Id\" = {outbox.Id}");

        using var client1 = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var client2 = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;

        await using var db1 = new GatewayDbContext(options);
        await using var db2 = new GatewayDbContext(options);

        var dispatcher1 = new WebhookDispatcher(db1, client1, _secretProtector, NullLogger<WebhookDispatcher>.Instance, _configuration);
        var dispatcher2 = new WebhookDispatcher(db2, client2, _secretProtector, NullLogger<WebhookDispatcher>.Instance, _configuration);

        // اجرای همزمان دو ورکر مجزا با کانکشن‌های مستقل
        var task1 = dispatcher1.DispatchPendingWebhooksAsync(CancellationToken.None);
        var task2 = dispatcher2.DispatchPendingWebhooksAsync(CancellationToken.None);

        await Task.WhenAll(task1, task2);

        // ایجاد یک DbContext کاملاً تمیز و تازه برای راستی‌آزمایی مقدار نهایی از دیسک
        await using var verifyDb = new GatewayDbContext(options);
        var refreshed = await verifyDb.OutboxEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == outbox.Id);

        refreshed.Should().NotBeNull();
        refreshed!.IsProcessed.Should().BeTrue();
        refreshed.AttemptCount.Should().Be(1); // اثبات این که دقیقا یک ورکر پردازش کرد و دیگری تکرار نکرد
    }

    [Fact]
    public async Task CheckoutExpiration_Persistence_ShouldUpdateDatabase()
    {
        var invoice = new Invoice(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "TDepositAddress123",
            10.0m,
            "USD",
            1.0m,
            "DirectPeg",
            DateTime.UtcNow,
            10_000_000L,
            50_000L,
            "https://test.com/cb",
            "token-xyz-123",
            TimeSpan.FromSeconds(-10) // Already expired
        );

        _dbContext.Invoices.Add(invoice);
        await _dbContext.SaveChangesAsync();

        invoice.EvaluateStatus();
        await _dbContext.SaveChangesAsync();

        var fromDb = await _dbContext.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoice.Id);
        fromDb!.Status.Should().Be(InvoiceStatus.Expired);
    }
}