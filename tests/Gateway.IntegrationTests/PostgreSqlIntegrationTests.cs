using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("test_gateway_db")
        .WithUsername("postgres")
        .WithPassword("postgres_test_password")
        .Build();

    private WireMockServer _wireMockServer = null!;
    private GatewayDbContext _dbContext = null!;
    private AesGcmSecretProtector _secretProtector = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        _wireMockServer = WireMockServer.Start();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Security:MasterEncryptionKey", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")
            })
            .Build();

        _secretProtector = new AesGcmSecretProtector(config);

        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
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

        var destinationUrl = $"{_wireMockServer.Url}/webhook-test";

        var outbox = new OutboxEvent(
            Guid.NewGuid(),
            "invoice.paid",
            "{\"invoiceId\":\"123\",\"status\":\"Paid\"}",
            destinationUrl,
            encryptedSecret
        );

        _dbContext.OutboxEvents.Add(outbox);
        await _dbContext.SaveChangesAsync();

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var dispatcher = new WebhookDispatcher(_dbContext, httpClient, _secretProtector, NullLogger<WebhookDispatcher>.Instance);

        await dispatcher.DispatchPendingWebhooksAsync(CancellationToken.None);

        var refreshed = await _dbContext.OutboxEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == outbox.Id);

        refreshed.Should().NotBeNull();
        refreshed!.LastError.Should().BeNull(because: $"Dispatch should not record an error, but encountered: {refreshed.LastError}");
        refreshed.IsProcessed.Should().BeTrue();
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