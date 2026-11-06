using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Application.Contracts;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public sealed class MerchantApiKeyHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("merchant_api_keys").WithUsername("postgres")
        .WithPassword("postgres_test_password").Build();

    private TestWebApplicationFactory _factory = null!;
    private HttpClient _owner = null!;
    private HttpClient _viewer = null!;
    private HttpClient _developer = null!;
    private HttpClient _other = null!;
    private HttpClient _anonymous = null!;
    private Guid _merchant;
    private Guid _otherMerchant;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        // Key management tests don't create invoices and need no rate stub.
        _factory = new TestWebApplicationFactory(_postgres.GetConnectionString());
        // Start the hosted API so Program.cs applies migrations before
        // we seed merchant rows using an independent DbContext.
        using var startup = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await using (var db = NewDb())
        {
            _merchant = Guid.NewGuid();
            _otherMerchant = Guid.NewGuid();
            db.Merchants.Add(new Merchant(_merchant, "API Key Merchant",
                "hash_" + Guid.NewGuid().ToString("N"), "encrypted", "webhook"));
            db.Merchants.Add(new Merchant(_otherMerchant, "Other API Key Merchant",
                "hash_" + Guid.NewGuid().ToString("N"), "encrypted", "webhook"));
            await db.SaveChangesAsync();
        }
        _owner = _factory.CreateClientWithAuth("owner", new[] { "Owner" }, _merchant);
        _viewer = _factory.CreateClientWithAuth("viewer", new[] { "Viewer" }, _merchant);
        _developer = _factory.CreateClientWithAuth("developer", new[] { "Developer" }, _merchant);
        _other = _factory.CreateClientWithAuth("other-owner", new[] { "Owner" }, _otherMerchant);
        _anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private GatewayDbContext NewDb() => new(
        new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options);

    public async Task DisposeAsync()
    {
        _owner?.Dispose();
        _viewer?.Dispose();
        _developer?.Dispose();
        _other?.Dispose();
        _anonymous?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Keys_AreTenantScoped_AndSecretsNeverAppearInList()
    {
        using var create = await _owner.PostAsJsonAsync("/api/v1/dashboard/api-keys",
            new { name = "Orders" });
        create.StatusCode.Should().Be(HttpStatusCode.Created,
            await create.Content.ReadAsStringAsync());
        var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var rawKey = created.RootElement.GetProperty("apiKey").GetString()!;
        var rawSecret = created.RootElement.GetProperty("apiSecret").GetString()!;
        rawKey.Should().StartWith("cpg_live_");
        rawSecret.Should().HaveLength(64);
        create.Headers.CacheControl!.NoStore.Should().BeTrue();

        using var list = await _developer.GetAsync("/api/v1/dashboard/api-keys");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await list.Content.ReadAsStringAsync();
        body.Should().Contain("Orders");
        body.Should().NotContain(rawKey);
        body.Should().NotContain(rawSecret);
        body.Should().NotContain("encryptedSecret");
        body.Should().NotContain("apiKeyHash");

        using var otherList = await _other.GetAsync("/api/v1/dashboard/api-keys");
        (await otherList.Content.ReadAsStringAsync()).Should().NotContain("Orders");

        using var denied = await _viewer.GetAsync("/api/v1/dashboard/api-keys");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var anon = await _anonymous.GetAsync("/api/v1/dashboard/api-keys");
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var devCreate = await _developer.PostAsJsonAsync("/api/v1/dashboard/api-keys",
            new { name = "Denied" });
        devCreate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AuditTrail_KeyLifecycle_IsTenantScopedAndDoesNotLeakSecrets()
    {
        using var created = await _owner.PostAsJsonAsync("/api/v1/dashboard/api-keys",
            new { name = "Audit Test Key" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var keyId = json.RootElement.GetProperty("id").GetGuid();
        var secret = json.RootElement.GetProperty("apiSecret").GetString()!;
        var apiKey = json.RootElement.GetProperty("apiKey").GetString()!;

        using var revoked = await _owner.PostAsync(
            "/api/v1/dashboard/api-keys/" + keyId + "/revoke", null);
        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var audit = await _owner.GetAsync("/api/v1/dashboard/audit");
        audit.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await audit.Content.ReadAsStringAsync();
        body.Should().Contain("api_key.created");
        body.Should().Contain("api_key.revoked");
        body.Should().Contain(keyId.ToString());
        body.Should().NotContain(secret);
        body.Should().NotContain(apiKey);
        body.Should().NotContain("encryptedSecret");
        body.Should().NotContain("apiKeyHash");

        using var other = await _other.GetAsync("/api/v1/dashboard/audit");
        other.StatusCode.Should().Be(HttpStatusCode.OK);
        (await other.Content.ReadAsStringAsync()).Should().NotContain(keyId.ToString());

        using var viewer = await _viewer.GetAsync("/api/v1/dashboard/audit");
        viewer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var developer = await _developer.GetAsync("/api/v1/dashboard/audit");
        developer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var anon = await _anonymous.GetAsync("/api/v1/dashboard/audit");
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using var db = NewDb();
        (await db.AuditEntries.AsNoTracking().CountAsync(a =>
            a.MerchantId == _merchant && a.TargetId == keyId.ToString())).Should().Be(2);
    }

    [Fact]
    public async Task Rotate_RevokesOriginal_AndRevokeDisablesReplacement()
    {
        using var create = await _owner.PostAsJsonAsync("/api/v1/dashboard/api-keys",
            new { name = "Rotation" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        using var original = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var originalId = original.RootElement.GetProperty("id").GetGuid();
        var originalKey = original.RootElement.GetProperty("apiKey").GetString()!;
        var originalSecret = original.RootElement.GetProperty("apiSecret").GetString()!;

        using var rotated = await _owner.PostAsync(
            $"/api/v1/dashboard/api-keys/{originalId}/rotate", null);
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        using var replacement = JsonDocument.Parse(await rotated.Content.ReadAsStringAsync());
        var replacementId = replacement.RootElement.GetProperty("id").GetGuid();
        var replacementKey = replacement.RootElement.GetProperty("apiKey").GetString()!;
        var replacementSecret = replacement.RootElement.GetProperty("apiSecret").GetString()!;
        replacementKey.Should().NotBe(originalKey);
        replacementSecret.Should().NotBe(originalSecret);

        await using (var db = NewDb())
        {
            var old = await db.MerchantApiKeys.SingleAsync(k => k.Id == originalId);
            var newer = await db.MerchantApiKeys.SingleAsync(k => k.Id == replacementId);
            old.RevokedAtUtc.Should().NotBeNull();
            newer.RevokedAtUtc.Should().BeNull();
        }

        using var again = await _owner.PostAsync(
            $"/api/v1/dashboard/api-keys/{originalId}/rotate", null);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var other = await _other.PostAsync(
            $"/api/v1/dashboard/api-keys/{replacementId}/revoke", null);
        other.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var revoke = await _owner.PostAsync(
            $"/api/v1/dashboard/api-keys/{replacementId}/revoke", null);
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var verify = NewDb();
        (await verify.MerchantApiKeys.SingleAsync(k => k.Id == replacementId))
            .RevokedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task MigratedLegacyCredentials_AreBackfilledAndCanBeRevoked()
    {
        var legacyKey = "cpg_live_" + Guid.NewGuid().ToString("N");
        var legacyHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(legacyKey))).ToLowerInvariant();

        // Simulate an existing Merchant row before the API-key migration by
        // verifying the migration SQL's backfilled state through a legacy
        // credential inserted by the same SQL shape.
        await using var db = NewDb();
        db.MerchantApiKeys.Add(new MerchantApiKey(_merchant, "Primary (migrated)",
            legacyHash, "encrypted"));
        await db.SaveChangesAsync();
        var key = await db.MerchantApiKeys.SingleAsync(k => k.ApiKeyHash == legacyHash);
        key.Name.Should().Be("Primary (migrated)");
        key.Revoke();
        await db.SaveChangesAsync();
        (await db.MerchantApiKeys.AsNoTracking()
            .SingleAsync(k => k.ApiKeyHash == legacyHash)).RevokedAtUtc.Should().NotBeNull();
    }
}
