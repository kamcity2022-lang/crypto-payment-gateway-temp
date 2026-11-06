using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Api.Controllers;
using Gateway.Application.DTOs;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

// Real HTTP routing/auth with existing TestWebApplicationFactory; PostgreSQL via Testcontainers.
public sealed class AdminPlansHttpIntegrationTests : IAsyncLifetime
{
    private const string Route = "/api/v1/admin/plans";
    private const string AccountXPub =
        "xpub6BosfCnifzxcFwrSzQiqu2DBVTshkCXacvNsWGYJVVhhawA7d4R5WSWGFNbi8Aw6ZRc1brxMyWMzG3DSSSSoekkudhUd9yLb6qx39T9nMdj";
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_admin_plans").WithUsername("postgres")
        .WithPassword("postgres_test_password").Build();

    private TestWebApplicationFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _owner = null!;
    private HttpClient _anon = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _factory = new TestWebApplicationFactory(_container.GetConnectionString());
        _admin = _factory.CreateClientWithAuth("superadmin", new[] { "SuperAdmin" });
        _owner = _factory.CreateClientWithAuth("merchant-owner", new[] { "Owner" });
        _anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await using var db = NewDb();
        (await db.Plans.CountAsync(p => p.IsDefault && p.IsActive)).Should().Be(1);
    }

    public async Task DisposeAsync()
    {
        _admin?.Dispose(); _owner?.Dispose(); _anon?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        await _container.DisposeAsync();
    }

    private GatewayDbContext NewDb() => new(
        new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_container.GetConnectionString()).Options);

    private static CreatePlanRequest PlanRequest(
        string name = "Standard", int count = 10, decimal volume = 1000m,
        decimal price = 9m, bool isDefault = false) => new()
    {
        Name = name, Description = "Integration plan",
        MonthlyInvoiceLimit = count, MonthlyVolumeLimitUsd = volume,
        PriceUsd = price, IsDefault = isDefault
    };

    private async Task<PlanResponseDto> CreateAsync(string name = "Standard",
        int count = 10, decimal volume = 1000m, bool isDefault = false)
    {
        using var resp = await _admin.PostAsJsonAsync(
            Route, PlanRequest(name, count, volume, isDefault: isDefault));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<PlanResponseDto>())!;
    }

    [Fact]
    public async Task SuperAdminHtml_ExposesPlanManagementControls_AndExistingApiRoutes()
    {
        using var response = await _admin.GetAsync("/admin.html");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Subscription Plans");
        html.Should().Contain("id=\"plansTable\"");
        html.Should().Contain("id=\"planEditorForm\"");
        html.Should().Contain("loadPlans()");
        html.Should().Contain("openPlanEditor(");
        html.Should().Contain("savePlan(event)");
        html.Should().Contain("set-default");
        html.Should().Contain("deactivate");
        html.Should().Contain("API_BASE + '/admin/plans'");
    }

    [Fact]
    public async Task Create_AndReadListAndDetail_Works()
    {
        var plan = await CreateAsync("Business");
        plan.Name.Should().Be("Business");
        plan.IsActive.Should().BeTrue();
        plan.IsDefault.Should().BeFalse();

        using var list = await _admin.GetAsync(Route);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await list.Content.ReadFromJsonAsync<PlanResponseDto[]>();
        rows.Should().Contain(p => p.Id == plan.Id);

        using var detail = await _admin.GetAsync($"{Route}/{plan.Id}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await detail.Content.ReadFromJsonAsync<PlanResponseDto>())!.Name.Should().Be("Business");

        using var missing = await _admin.GetAsync($"{Route}/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(0, 100, 9)]
    [InlineData(-1, 100, 9)]
    [InlineData(10, -1, 9)]
    [InlineData(10, 100, -1)]
    public async Task InvalidLimits_Return400(int count, int volume, int price)
    {
        using var resp = await _admin.PostAsJsonAsync(
            Route, PlanRequest("Invalid", count, volume, price));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_PersistsLimitsAndPrice()
    {
        var plan = await CreateAsync("Editable");
        var req = new UpdatePlanRequest {
            Name = "Edited", Description = "New description",
            MonthlyInvoiceLimit = 3, MonthlyVolumeLimitUsd = 250m, PriceUsd = 19.99m
        };
        using var changed = await _admin.PutAsJsonAsync($"{Route}/{plan.Id}", req);
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await changed.Content.ReadFromJsonAsync<PlanResponseDto>())!;
        dto.MonthlyInvoiceLimit.Should().Be(3);
        dto.MonthlyVolumeLimitUsd.Should().Be(250m);
        dto.PriceUsd.Should().Be(19.99m);
        dto.Name.Should().Be("Edited");
    }

    [Fact]
    public async Task ActivateDeactivate_NonDefaultPlan_Works()
    {
        var plan = await CreateAsync("Toggle");
        using var disabled = await _admin.PostAsync($"{Route}/{plan.Id}/deactivate", null);
        disabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await disabled.Content.ReadFromJsonAsync<PlanResponseDto>())!.IsActive.Should().BeFalse();

        using var reenabled = await _admin.PostAsync($"{Route}/{plan.Id}/activate", null);
        reenabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await reenabled.Content.ReadFromJsonAsync<PlanResponseDto>())!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task DefaultCannotBeDeactivated()
    {
        await using var db = NewDb();
        var id = await db.Plans.Where(p => p.IsDefault).Select(p => p.Id).SingleAsync();
        using var resp = await _admin.PostAsync($"{Route}/{id}/deactivate", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await db.Plans.AsNoTracking().SingleAsync(p => p.Id == id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SetDefault_ClearsOld_RejectsInactiveTarget()
    {
        var target = await CreateAsync("Target");
        var inactive = await CreateAsync("Inactive");
        using var disabled = await _admin.PostAsync($"{Route}/{inactive.Id}/deactivate", null);
        disabled.StatusCode.Should().Be(HttpStatusCode.OK);
        using var refused = await _admin.PostAsync($"{Route}/{inactive.Id}/set-default", null);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var success = await _admin.PostAsync($"{Route}/{target.Id}/set-default", null);
        success.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = NewDb();
        (await db.Plans.CountAsync(p => p.IsDefault)).Should().Be(1);
        (await db.Plans.SingleAsync(p => p.IsDefault)).Id.Should().Be(target.Id);
    }

    [Fact]
    public async Task CreateAsDefault_ClearsPreviousDefault()
    {
        var created = await CreateAsync("Default on create", isDefault: true);
        created.IsDefault.Should().BeTrue();
        await using var db = NewDb();
        (await db.Plans.CountAsync(p => p.IsDefault)).Should().Be(1);
        (await db.Plans.SingleAsync(p => p.IsDefault)).Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task ConcurrentSetDefault_MaintainsUniqueIndex()
    {
        var candidates = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(i => CreateAsync($"Candidate-{i}")));
        var responses = await Task.WhenAll(candidates.Select(p =>
            _admin.PostAsync($"{Route}/{p.Id}/set-default", null)));
        try
        {
            responses.Should().OnlyContain(x => x.StatusCode == HttpStatusCode.OK);
            await using var db = NewDb();
            var defaults = await db.Plans.Where(p => p.IsDefault).Select(p => p.Id).ToListAsync();
            defaults.Should().ContainSingle();
            candidates.Select(p => p.Id).Should().Contain(defaults[0]);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Authorization_RejectsNonSuperAdminAndAnonymous()
    {
        using var ownerRead = await _owner.GetAsync(Route);
        ownerRead.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var ownerCreate = await _owner.PostAsJsonAsync(Route, PlanRequest());
        ownerCreate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var anonRead = await _anon.GetAsync(Route);
        anonRead.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var anonCreate = await _anon.PostAsJsonAsync(Route, PlanRequest());
        anonCreate.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExistingSubscription_ContinuesAfterPlanDeactivation()
    {
        var plan = await CreateAsync("Legacy");
        var mid = await SeedMerchantAsync(plan.Id);
        using var resp = await _admin.PostAsync($"{Route}/{plan.Id}/deactivate", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await InvoiceAsync(mid, 10m)).Should().BeOfType<CreatedAtActionResult>();
        await using var db = NewDb();
        var sub = await db.Subscriptions.SingleAsync(s => s.MerchantId == mid);
        sub.PlanId.Should().Be(plan.Id);
        sub.CurrentPeriodInvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdatedLimits_AffectSubsequentInvoiceCreation()
    {
        var plan = await CreateAsync("Adjustable quota", count: 2);
        var mid = await SeedMerchantAsync(plan.Id);
        (await InvoiceAsync(mid, 10m)).Should().BeOfType<CreatedAtActionResult>();
        using var changed = await _admin.PutAsJsonAsync($"{Route}/{plan.Id}",
            new UpdatePlanRequest {
                Name = "Adjustable quota", Description = "Reduced invoice count",
                MonthlyInvoiceLimit = 1, MonthlyVolumeLimitUsd = 1000m, PriceUsd = 0m
            });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);

        var rejected = (await InvoiceAsync(mid, 10m)).Should().BeOfType<ObjectResult>().Subject;
        rejected.StatusCode.Should().Be(403);
        System.Text.Json.JsonSerializer.Serialize(rejected.Value)
            .Should().Contain("SUBSCRIPTION_INVOICE_LIMIT_REACHED");
    }

    [Fact]
    public async Task FailedSetDefault_LeavesOldDefaultIntact()
    {
        await using var db = NewDb();
        var before = await db.Plans.Where(p => p.IsDefault).Select(p => p.Id).SingleAsync();
        using var resp = await _admin.PostAsync($"{Route}/{Guid.NewGuid()}/set-default", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var after = await db.Plans.AsNoTracking()
            .Where(p => p.IsDefault).Select(p => p.Id).SingleAsync();
        after.Should().Be(before);
    }

    private async Task<Guid> SeedMerchantAsync(Guid planId)
    {
        var id = Guid.NewGuid();
        await using var db = NewDb();
        db.Merchants.Add(new Merchant(id, "Plan Test Merchant",
            "hash_" + Guid.NewGuid().ToString("N"), "api-secret", "webhook-secret"));
        db.Wallets.Add(new Wallet(id, AccountXPub, "TRON"));
        db.Subscriptions.Add(new Subscription(id, planId));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<IActionResult> InvoiceAsync(Guid merchantId, decimal amount)
    {
        await using var db = NewDb();
        var ctrl = new InvoicesController(db, new FixedTestRateProvider()) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        ctrl.HttpContext.Items["MerchantId"] = merchantId;
        ctrl.Request.Scheme = "https";
        ctrl.Request.Host = new HostString("localhost");
        return await ctrl.CreateInvoice(new CreateInvoiceRequest {
            FiatAmount = amount, FiatCurrency = "USD",
            CallbackUrl = "https://1.1.1.1/callback", ExpiryMinutes = 30
        }, CancellationToken.None);
    }
}
