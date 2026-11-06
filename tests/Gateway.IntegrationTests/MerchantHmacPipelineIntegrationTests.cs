using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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

// End-to-end HTTP tests: the real MVC pipeline must verify a raw body
// BEFORE [FromBody] has deserialized or consumed the request stream.
public sealed class MerchantHmacPipelineIntegrationTests : IAsyncLifetime
{
    private const string AccountXPub =
        "xpub6BosfCnifzxcFwrSzQiqu2DBVTshkCXacvNsWGYJVVhhawA7d4R5WSWGFNbi8Aw6ZRc1brxMyWMzG3DSSSSoekkudhUd9yLb6qx39T9nMdj";
    private const string ApiKey = "test-p0.3-api-key";
    private const string ApiSecret = "test-p0.3-secret";
    private const string Route = "/api/v1/invoices";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_hmac_pipeline").WithUsername("postgres")
        .WithPassword("postgres_test_password").Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private Guid _merchantId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new TestWebApplicationFactory(_postgres.GetConnectionString())
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IExchangeRateProvider>(new FixedTestRateProvider())));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var defaultPlanId = await db.Plans.Where(p => p.IsDefault).Select(p => p.Id).SingleAsync();

        _merchantId = Guid.NewGuid();
        db.Merchants.Add(new Merchant(
            _merchantId,
            "HMAC Test Merchant",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey))).ToLowerInvariant(),
            protector.Encrypt(ApiSecret),
            protector.Encrypt("test-webhook-secret")));
        db.Wallets.Add(new Wallet(_merchantId, AccountXPub, "TRON"));
        db.Subscriptions.Add(new Subscription(_merchantId, defaultPlanId));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private static byte[] Body(decimal amount) =>
        Encoding.UTF8.GetBytes(
            "{\"fiatAmount\":" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"fiatCurrency\":\"USD\",\"callbackUrl\":\"https://1.1.1.1/callback\",\"expiryMinutes\":30}");

    private static HttpRequestMessage Signed(
        byte[] actualBody, byte[]? signedBody = null, string? nonce = null,
        string? timestamp = null, string? route = null, string? method = null)
    {
        nonce ??= Guid.NewGuid().ToString("N");
        timestamp ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        route ??= Route;
        method ??= "POST";
        signedBody ??= actualBody;

        var canonical =
            $"{method}\n{Route}\n\n{timestamp}\n{nonce}\n" +
            Convert.ToHexString(SHA256.HashData(signedBody)).ToLowerInvariant();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ApiSecret));
        var signature = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        var req = new HttpRequestMessage(new HttpMethod(method), route);
        req.Content = new ByteArrayContent(actualBody);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-API-Key", ApiKey);
        req.Headers.Add("X-Signature", signature);
        req.Headers.Add("X-Timestamp", timestamp);
        req.Headers.Add("X-Nonce", nonce);
        return req;
    }

    [Fact]
    public async Task CorrectSignature_OverActualBody_CreatesInvoice()
    {
        using var req = Signed(Body(10m));
        using var response = await _client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        (await db.Invoices.CountAsync(i => i.MerchantId == _merchantId)).Should().Be(1);
    }

    [Fact]
    public async Task SignatureOverEmptyBody_CannotAuthorizeNonEmptyInvoice()
    {
        using var req = Signed(Body(10m), Array.Empty<byte>());
        using var response = await _client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        (await db.Invoices.CountAsync(i => i.MerchantId == _merchantId)).Should().Be(0);
    }

    [Fact]
    public async Task ChangedBody_AfterSigning_IsRejectedWithoutConsumingNonce()
    {
        var nonce = Guid.NewGuid().ToString("N");
        using var tampered = Signed(Body(900m), Body(10m), nonce);
        using var denied = await _client.SendAsync(tampered);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The same nonce can still be used by the legitimate signed body.
        using var legitimate = Signed(Body(10m), nonce: nonce);
        using var accepted = await _client.SendAsync(legitimate);
        accepted.StatusCode.Should().Be(HttpStatusCode.Created,
            await accepted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DuplicateNonce_IsRejected()
    {
        var nonce = Guid.NewGuid().ToString("N");
        using var first = Signed(Body(10m), nonce: nonce);
        using var accepted = await _client.SendAsync(first);
        accepted.StatusCode.Should().Be(HttpStatusCode.Created);

        using var replay = Signed(Body(10m), nonce: nonce);
        using var denied = await _client.SendAsync(replay);
        denied.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task QueryStringTampering_IsRejected()
    {
        using var req = Signed(Body(10m), route: Route + "?unexpected=1");
        using var denied = await _client.SendAsync(req);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MalformedOrExtremeTimestamp_Returns401Not500()
    {
        using var req = Signed(Body(10m), timestamp: long.MaxValue.ToString());
        using var denied = await _client.SendAsync(req);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OversizedBody_Returns413WithoutCreatingInvoice()
    {
        using var req = Signed(new byte[1024 * 1024 + 1]);
        using var denied = await _client.SendAsync(req);
        denied.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        (await db.Invoices.CountAsync(i => i.MerchantId == _merchantId)).Should().Be(0);
    }
}
