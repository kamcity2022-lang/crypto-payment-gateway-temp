using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public sealed class MerchantWebhookDashboardHttpTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("webhook_dashboard_tests").WithUsername("postgres")
        .WithPassword("postgres_test_password").Build();

    private TestWebApplicationFactory _factory = null!;
    private HttpClient _owner = null!;
    private HttpClient _viewer = null!;
    private HttpClient _other = null!;
    private HttpClient _anonymous = null!;
    private Guid _merchant;
    private Guid _otherMerchant;

    private const string BasePath = "/api/v1/dashboard/webhooks";
    private const string SafeEndpoint = "https://1.1.1.1/merchant-webhook";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new TestWebApplicationFactory(_postgres.GetConnectionString());
        using var startup = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await using (var db = NewDb())
        {
            _merchant = Guid.NewGuid();
            _otherMerchant = Guid.NewGuid();
            var own = new Merchant(_merchant, "Webhook A",
                "hash_" + Guid.NewGuid().ToString("N"), "secret", "webhook");
            var other = new Merchant(_otherMerchant, "Webhook B",
                "hash_" + Guid.NewGuid().ToString("N"), "secret", "webhook");
            own.SetDefaultWebhookUrl(SafeEndpoint);
            other.SetDefaultWebhookUrl(SafeEndpoint);
            db.Merchants.AddRange(own, other);
            await db.SaveChangesAsync();
        }

        _owner = _factory.CreateClientWithAuth("owner",
            new[] { "Owner" }, _merchant);
        _viewer = _factory.CreateClientWithAuth("viewer",
            new[] { "Viewer" }, _merchant);
        _other = _factory.CreateClientWithAuth("other-owner",
            new[] { "Owner" }, _otherMerchant);
        _anonymous = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private GatewayDbContext NewDb() => new(
        new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options);

    public async Task DisposeAsync()
    {
        _owner?.Dispose();
        _viewer?.Dispose();
        _other?.Dispose();
        _anonymous?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Settings_AuthorizationAndSsrfValidation()
    {
        using var anon = await _anonymous.GetAsync(BasePath + "/settings");
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var readOnly = await _viewer.GetAsync(BasePath + "/settings");
        readOnly.StatusCode.Should().Be(HttpStatusCode.OK);
        (await readOnly.Content.ReadAsStringAsync()).Should().Contain(SafeEndpoint);

        using var denied = await _viewer.PutAsJsonAsync(BasePath + "/settings",
            new { webhookUrl = "https://1.1.1.1/other" });
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        foreach (var bad in new[]
        {
            "http://1.1.1.1/hook",
            "https://127.0.0.1/hook",
            "https://169.254.169.254/latest/meta-data",
            "https://1.1.1.1/hook#fragment",
            "https://user:password@1.1.1.1/hook"
        })
        {
            using var invalid = await _owner.PutAsJsonAsync(BasePath + "/settings",
                new { webhookUrl = bad });
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, bad);
        }

        using var update = await _owner.PutAsJsonAsync(BasePath + "/settings",
            new { webhookUrl = "https://1.1.1.1/new-hook" });
        update.StatusCode.Should().Be(HttpStatusCode.OK,
            await update.Content.ReadAsStringAsync());
        await using var db = NewDb();
        (await db.Merchants.AsNoTracking().SingleAsync(m => m.Id == _merchant))
            .DefaultWebhookUrl.Should().Be("https://1.1.1.1/new-hook");
        (await db.Merchants.AsNoTracking().SingleAsync(m => m.Id == _otherMerchant))
            .DefaultWebhookUrl.Should().Be(SafeEndpoint);
    }

    [Fact]
    public async Task TestWebhook_HistoryAndRedelivery_AreTenantScoped()
    {
        using var forbiddenTest = await _viewer.PostAsync(BasePath + "/test", null);
        forbiddenTest.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var queue = await _owner.PostAsync(BasePath + "/test", null);
        queue.StatusCode.Should().Be(HttpStatusCode.Accepted,
            await queue.Content.ReadAsStringAsync());

        using var result = JsonDocument.Parse(await queue.Content.ReadAsStringAsync());
        var eventId = result.RootElement.GetProperty("eventId").GetGuid();

        using var history = await _owner.GetAsync(BasePath + "/events?status=queued");
        history.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await history.Content.ReadAsStringAsync();
        body.Should().Contain(eventId.ToString());
        body.Should().NotContain("encryptedSecretKey");

        using var otherHistory = await _other.GetAsync(BasePath + "/events");
        (await otherHistory.Content.ReadAsStringAsync()).Should().NotContain(eventId.ToString());

        using var otherReplay = await _other.PostAsync(
            BasePath + "/events/" + eventId + "/redeliver", null);
        otherReplay.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var cannotReplayQueued = await _owner.PostAsync(
            BasePath + "/events/" + eventId + "/redeliver", null);
        cannotReplayQueued.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await using (var db = NewDb())
        {
            var evt = await db.OutboxEvents.SingleAsync(e => e.Id == eventId);
            evt.RecordFailure("HTTP 503", maxRetries: 1);
            await db.SaveChangesAsync();
        }

        using var exhaustedHistory = await _owner.GetAsync(BasePath + "/events?status=exhausted");
        exhaustedHistory.StatusCode.Should().Be(HttpStatusCode.OK);
        (await exhaustedHistory.Content.ReadAsStringAsync()).Should().Contain(eventId.ToString());

        using var forbiddenReplay = await _viewer.PostAsync(
            BasePath + "/events/" + eventId + "/redeliver", null);
        forbiddenReplay.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var replay = await _owner.PostAsync(
            BasePath + "/events/" + eventId + "/redeliver", null);
        replay.StatusCode.Should().Be(HttpStatusCode.Accepted,
            await replay.Content.ReadAsStringAsync());
        using var duplicate = await _owner.PostAsync(
            BasePath + "/events/" + eventId + "/redeliver", null);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await using var verification = NewDb();
        var updated = await verification.OutboxEvents.AsNoTracking()
            .SingleAsync(e => e.Id == eventId);
        updated.IsProcessed.Should().BeFalse();
        updated.NextAttemptAtUtc.Should().NotBeNull();
        updated.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Events_RejectsInvalidFilters_AndClampsPagination()
    {
        using var invalid = await _owner.GetAsync(BasePath + "/events?status=random");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var page = await _owner.GetAsync(
            BasePath + "/events?page=-2&pageSize=999&status=all");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        using var data = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        data.RootElement.GetProperty("page").GetInt32().Should().Be(1);
        data.RootElement.GetProperty("pageSize").GetInt32().Should().Be(50);
        data.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
    }
}
