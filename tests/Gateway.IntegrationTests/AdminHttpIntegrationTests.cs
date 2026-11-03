using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Application.DTOs;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Context.Request.Headers.ContainsKey("X-Test-User"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var userName = Context.Request.Headers["X-Test-User"].ToString();
        var roles = Context.Request.Headers["X-Test-Roles"].ToString().Split(',');

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, userName),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.Trim()));
        }

        if (Context.Request.Headers.TryGetValue("X-Test-MerchantId", out var merchantIdHeader) && Guid.TryParse(merchantIdHeader, out var merchantId))
        {
            claims.Add(new Claim("MerchantId", merchantId.ToString()));
        }

        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "TestScheme");

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public TestWebApplicationFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<GatewayDbContext>));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<GatewayDbContext>(options =>
                options.UseNpgsql(_connectionString));

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "TestScheme";
                options.DefaultChallengeScheme = "TestScheme";
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestScheme", options => { });
        });
    }

    public HttpClient CreateClientWithAuth(string userName, string[] roles, Guid? merchantId = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", userName);
        client.DefaultRequestHeaders.Add("X-Test-Roles", string.Join(",", roles));
        if (merchantId.HasValue)
        {
            client.DefaultRequestHeaders.Add("X-Test-MerchantId", merchantId.Value.ToString());
        }
        return client;
    }
}

public class AdminHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_admin_http_db").WithUsername("postgres").WithPassword("postgres_test_password").Build();

    private TestWebApplicationFactory _factory = null!;
    private HttpClient _unauthorizedClient = null!;
    private HttpClient _merchantClient = null!;
    private HttpClient _superAdminClient = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        _factory = new TestWebApplicationFactory(_dbContainer.GetConnectionString());

        _unauthorizedClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _merchantClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _superAdminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        await dbContext.Database.MigrateAsync();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        if (!await roleManager.RoleExistsAsync("SuperAdmin"))
            await roleManager.CreateAsync(new IdentityRole("SuperAdmin"));
        if (!await roleManager.RoleExistsAsync("Owner"))
            await roleManager.CreateAsync(new IdentityRole("Owner"));

        var superAdmin = new ApplicationUser { UserName = "superadmin@test.com", Email = "superadmin@test.com", IsSuperAdmin = true, EmailConfirmed = true };
        await userManager.CreateAsync(superAdmin, "SuperAdmin@123!");
        await userManager.AddToRoleAsync(superAdmin, "SuperAdmin");

        var merchantId = Guid.NewGuid();
        // ✅ اصلاح: استفاده از hash یکتا برای جلوگیری از تداخل با حلقه زیر
        var merchant = new Merchant(merchantId, "Test Merchant", "hash_main_merchant", "secret_main", "webhook_secret_main");
        dbContext.Merchants.Add(merchant);

        var merchantUser = new ApplicationUser { UserName = "merchant@test.com", Email = "merchant@test.com", MerchantId = merchantId, EmailConfirmed = true };
        await userManager.CreateAsync(merchantUser, "Merchant@123!");
        await userManager.AddToRoleAsync(merchantUser, "Owner");

        // ✅ اصلاح: شروع حلقه از 2 برای جلوگیری از تداخل ApiKeyHash با مرچنت اصلی
        for (int i = 2; i <= 26; i++)
        {
            var m = new Merchant(Guid.NewGuid(), $"Merchant {i:D2}", $"hash{i}", $"secret{i}", $"webhook{i}");
            dbContext.Merchants.Add(m);
        }
        await dbContext.SaveChangesAsync();

        // Use reflection to set some merchants to inactive for testing the IsActive filter
        var allMerchants = await dbContext.Merchants.ToListAsync();
        for (int i = 0; i < allMerchants.Count; i++)
        {
            if (i % 2 == 0)
            {
                var prop = typeof(Merchant).GetProperty("IsActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                prop?.SetValue(allMerchants[i], false);
            }
        }
        await dbContext.SaveChangesAsync();

        _superAdminClient = _factory.CreateClientWithAuth("superadmin@test.com", new[] { "SuperAdmin" });
        _merchantClient = _factory.CreateClientWithAuth("merchant@test.com", new[] { "Owner" }, merchantId);
    }

    public async Task DisposeAsync()
    {
        _unauthorizedClient?.Dispose();
        _merchantClient?.Dispose();
        _superAdminClient?.Dispose();
        if (_factory != null) await _factory.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    [Fact]
    public async Task GetOverview_Unauthenticated_ShouldReturnUnauthorized()
    {
        var response = await _unauthorizedClient.GetAsync("/api/v1/admin/overview");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetOverview_MerchantUser_ShouldReturnForbidden()
    {
        var response = await _merchantClient.GetAsync("/api/v1/admin/overview");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetOverview_SuperAdmin_ShouldReturnSuccess()
    {
        var response = await _superAdminClient.GetAsync("/api/v1/admin/overview");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var overview = await response.Content.ReadFromJsonAsync<PlatformOverviewDto>();
        overview.Should().NotBeNull();
        overview.TotalMerchants.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetMerchants_FilterIsActiveTrue_ShouldReturnOnlyActive()
    {
        var response = await _superAdminClient.GetAsync("/api/v1/admin/merchants?isActive=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PagedMerchantResultDto>();
        result.Should().NotBeNull();
        result.Items.Should().OnlyContain(m => m.IsActive);
    }

    [Fact]
    public async Task GetMerchants_FilterIsActiveFalse_ShouldReturnOnlyInactive()
    {
        var response = await _superAdminClient.GetAsync("/api/v1/admin/merchants?isActive=false");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PagedMerchantResultDto>();
        result.Should().NotBeNull();
        result.Items.Should().OnlyContain(m => !m.IsActive);
    }

    [Fact]
    public async Task GetMerchants_Pagination_ShouldWorkAcrossPages()
    {
        var response1 = await _superAdminClient.GetAsync("/api/v1/admin/merchants?page=1&pageSize=10");
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        var result1 = await response1.Content.ReadFromJsonAsync<PagedMerchantResultDto>();
        result1.Should().NotBeNull();
        result1.Items.Count.Should().Be(10);
        result1.Page.Should().Be(1);
        result1.TotalPages.Should().BeGreaterThanOrEqualTo(3);

        var response2 = await _superAdminClient.GetAsync("/api/v1/admin/merchants?page=2&pageSize=10");
        response2.StatusCode.Should().Be(HttpStatusCode.OK);
        var result2 = await response2.Content.ReadFromJsonAsync<PagedMerchantResultDto>();
        result2.Should().NotBeNull();
        result2.Items.Count.Should().Be(10);
        result2.Page.Should().Be(2);

        var ids1 = result1.Items.Select(m => m.MerchantId).ToHashSet();
        var ids2 = result2.Items.Select(m => m.MerchantId).ToHashSet();
        ids1.Intersect(ids2).Should().BeEmpty("Pagination should not return overlapping items");
    }

    [Fact]
    public async Task GetMerchants_PageSizeBoundary_ShouldClampToMax100()
    {
        var response = await _superAdminClient.GetAsync("/api/v1/admin/merchants?pageSize=150");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PagedMerchantResultDto>();
        result.Should().NotBeNull();
        result.PageSize.Should().Be(100, "PageSize should be clamped to a maximum of 100");
    }

    [Fact]
    public async Task GetMerchantDetails_SensitiveData_NotExposedInJson()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var merchant = await dbContext.Merchants.FirstAsync();

        var response = await _superAdminClient.GetAsync($"/api/v1/admin/merchants/{merchant.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();

        json.Should().NotContain("ApiKeyHash", "sensitive credentials must not be exposed");
        json.Should().NotContain("EncryptedApiKeySecret", "sensitive credentials must not be exposed");
        json.Should().NotContain("EncryptedWebhookSecret", "sensitive credentials must not be exposed");
        json.Should().NotContain("Secret", "sensitive credentials must not be exposed");
        json.Should().NotContain("Hash", "sensitive credentials must not be exposed");
    }
}