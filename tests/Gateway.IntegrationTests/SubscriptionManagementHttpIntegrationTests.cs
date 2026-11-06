using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Application.DTOs;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Gateway.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public sealed class SubscriptionManagementHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("subscription_management_tests")
        .WithUsername("postgres")
        .WithPassword("postgres_test_password")
        .Build();

    private TestWebApplicationFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _owner = null!;
    private HttpClient _anonymous = null!;

    private Guid _merchantId;
    private Guid _otherMerchantId;
    private Guid _starterPlanId;
    private Guid _proPlanId;
    private Guid _disabledPlanId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new TestWebApplicationFactory(_postgres.GetConnectionString());
        _admin = _factory.CreateClientWithAuth("superadmin", new[] { "SuperAdmin" });
        _anonymous = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The existing factory starts Program.cs / migrations / bootstrap.
        await using var db = Db();
        var defaultPlan = await db.Plans.SingleAsync(p => p.IsDefault);
        _starterPlanId = defaultPlan.Id;

        var pro = new Plan("Pro", "Subscription test plan", 2, 60m, 29m);
        var disabled = new Plan("Retired", "Inactive subscription test plan", 3, 100m, 0m);
        disabled.Deactivate();
        db.Plans.AddRange(pro, disabled);
        _proPlanId = pro.Id;
        _disabledPlanId = disabled.Id;

        _merchantId = Guid.NewGuid();
        _otherMerchantId = Guid.NewGuid();
        db.Merchants.AddRange(
            new Merchant(_merchantId, "Subscription Merchant", "hash_" + Guid.NewGuid().ToString("N"), "sec1", "whsec1"),
            new Merchant(_otherMerchantId, "Other Merchant", "hash_" + Guid.NewGuid().ToString("N"), "sec2", "whsec2"));
        db.Subscriptions.Add(new Subscription(_merchantId, _starterPlanId));
        db.Subscriptions.Add(new Subscription(_otherMerchantId, _starterPlanId));
        await db.SaveChangesAsync();

        _owner = _factory.CreateClientWithAuth("owner", new[] { "Owner" }, _merchantId);
    }

    public async Task DisposeAsync()
    {
        _admin?.Dispose();
        _owner?.Dispose();
        _anonymous?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private GatewayDbContext Db() => new(
        new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);

    private string AdminPath(Guid? mid = null) =>
        $"/api/v1/admin/merchants/{mid ?? _merchantId}/subscription";

    private async Task<SubscriptionResponseDto> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        response.StatusCode.Should().Be(expected,
            await response.Content.ReadAsStringAsync());
        var dto = await response.Content.ReadFromJsonAsync<SubscriptionResponseDto>();
        dto.Should().NotBeNull();
        return dto!;
    }

    [Fact]
    public async Task GetMerchantView_UsesResolvedTenantAndShowsEffectiveQuota()
    {
        using var response = await _owner.GetAsync("/api/v1/dashboard/subscription");
        var dto = await ReadAsync(response, HttpStatusCode.OK);
        dto.MerchantId.Should().Be(_merchantId);
        dto.PlanId.Should().Be(_starterPlanId);
        dto.CurrentPeriodInvoiceCount.Should().Be(0);
        dto.RemainingInvoices.Should().Be(dto.EffectiveMonthlyInvoiceLimit);
    }

    [Fact]
    public async Task OwnerCannotReadOtherMerchant_AndCannotPerformAdminMutations()
    {
        using var other = _factory.CreateClientWithAuth("other-owner", new[] { "Owner" }, _otherMerchantId);
        using var response = await other.GetAsync("/api/v1/dashboard/subscription");
        (await ReadAsync(response, HttpStatusCode.OK)).MerchantId.Should().Be(_otherMerchantId);

        using var denied = await _owner.PostAsJsonAsync(AdminPath() + "/assign",
            new AssignSubscriptionRequest { PlanId = _proPlanId });
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var anon = await _anonymous.GetAsync(AdminPath());
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminGetAndHistory_ReturnExistingSubscription()
    {
        using var response = await _admin.GetAsync(AdminPath());
        (await ReadAsync(response, HttpStatusCode.OK)).Status.Should().Be(SubscriptionStatus.Active);

        using var history = await _admin.GetAsync(AdminPath() + "/history");
        history.StatusCode.Should().Be(HttpStatusCode.OK);
        var records = await history.Content.ReadFromJsonAsync<SubscriptionResponseDto[]>();
        records.Should().ContainSingle(s => s.MerchantId == _merchantId);
    }

    [Fact]
    public async Task Assign_WithActiveSubscription_Returns409()
    {
        using var response = await _admin.PostAsJsonAsync(AdminPath() + "/assign",
            new AssignSubscriptionRequest { PlanId = _proPlanId });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await using var db = Db();
        (await db.Subscriptions.CountAsync(s =>
            s.MerchantId == _merchantId && s.Status == SubscriptionStatus.Active)).Should().Be(1);
    }

    [Fact]
    public async Task Assign_AfterCancel_PreservesHistoryAndCreatesNewActive()
    {
        using var cancelled = await _admin.PostAsync(AdminPath() + "/cancel", null);
        (await ReadAsync(cancelled, HttpStatusCode.OK)).Status.Should().Be(SubscriptionStatus.Cancelled);

        using var created = await _admin.PostAsJsonAsync(AdminPath() + "/assign",
            new AssignSubscriptionRequest { PlanId = _proPlanId });
        var newSubscription = await ReadAsync(created, HttpStatusCode.Created);
        newSubscription.PlanId.Should().Be(_proPlanId);

        using var history = await _admin.GetAsync(AdminPath() + "/history");
        var rows = (await history.Content.ReadFromJsonAsync<SubscriptionResponseDto[]>())!;
        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(s => s.Status == SubscriptionStatus.Active);
        rows.Should().ContainSingle(s => s.Status == SubscriptionStatus.Cancelled);
    }

    [Fact]
    public async Task ChangePlan_DoesNotResetUsageOrPeriod()
    {
        await using (var db = Db())
        {
            var sub = await db.Subscriptions.SingleAsync(s => s.MerchantId == _merchantId);
            sub.RecordInvoiceCreation(40m);
            await db.SaveChangesAsync();
        }

        using var old = await _admin.GetAsync(AdminPath());
        var before = await ReadAsync(old, HttpStatusCode.OK);

        using var changed = await _admin.PutAsJsonAsync(AdminPath() + "/plan",
            new AssignSubscriptionRequest { PlanId = _proPlanId });
        var after = await ReadAsync(changed, HttpStatusCode.OK);
        after.PlanId.Should().Be(_proPlanId);
        after.CurrentPeriodStartUtc.Should().Be(before.CurrentPeriodStartUtc);
        after.CurrentPeriodEndUtc.Should().Be(before.CurrentPeriodEndUtc);
        after.CurrentPeriodInvoiceCount.Should().Be(1);
        after.CurrentPeriodVolumeUsd.Should().Be(40m);
        after.RemainingInvoices.Should().Be(1);
        after.RemainingVolumeUsd.Should().Be(20m);
    }

    [Fact]
    public async Task OverrideAndClearLimits_ChangesEffectiveQuota()
    {
        using var set = await _admin.PutAsJsonAsync(AdminPath() + "/limits",
            new OverrideLimitsRequest { CustomInvoiceLimit = 1, CustomVolumeLimitUsd = 25m });
        var overridden = await ReadAsync(set, HttpStatusCode.OK);
        overridden.EffectiveMonthlyInvoiceLimit.Should().Be(1);
        overridden.EffectiveMonthlyVolumeLimitUsd.Should().Be(25m);

        using var clear = await _admin.PutAsJsonAsync(AdminPath() + "/limits",
            new OverrideLimitsRequest { CustomInvoiceLimit = null, CustomVolumeLimitUsd = null });
        var restored = await ReadAsync(clear, HttpStatusCode.OK);
        restored.CustomInvoiceLimit.Should().BeNull();
        restored.CustomVolumeLimitUsd.Should().BeNull();
        restored.PlanId.Should().Be(_starterPlanId);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(1, -1)]
    public async Task Override_InvalidValues_Return400(int count, int volume)
    {
        using var invalid = await _admin.PutAsJsonAsync(AdminPath() + "/limits",
            new OverrideLimitsRequest { CustomInvoiceLimit = count, CustomVolumeLimitUsd = volume });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AssignAndChangePlan_RejectInactivePlan()
    {
        using var change = await _admin.PutAsJsonAsync(AdminPath() + "/plan",
            new AssignSubscriptionRequest { PlanId = _disabledPlanId });
        change.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var cancelled = await _admin.PostAsync(AdminPath() + "/cancel", null);
        cancelled.StatusCode.Should().Be(HttpStatusCode.OK);

        using var assign = await _admin.PostAsJsonAsync(AdminPath() + "/assign",
            new AssignSubscriptionRequest { PlanId = _disabledPlanId });
        assign.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var read = await _admin.GetAsync(AdminPath());
        (await ReadAsync(read, HttpStatusCode.OK)).Status.Should().Be(SubscriptionStatus.Cancelled);
    }

    [Fact]
    public async Task CancelledSubscription_DoesNotAutoReactivateOnBootstrapLogic()
    {
        using var cancelled = await _admin.PostAsync(AdminPath() + "/cancel", null);
        cancelled.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = Db();
        var merchantsWithoutAnySubscription = await db.Merchants
            .Where(m => !db.Subscriptions.Any(s => s.MerchantId == m.Id))
            .Select(m => m.Id)
            .ToListAsync();

        merchantsWithoutAnySubscription.Should().NotContain(_merchantId);
        (await db.Subscriptions.CountAsync(s =>
            s.MerchantId == _merchantId && s.Status == SubscriptionStatus.Active)).Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentAssignments_PreserveSingleActiveSubscription()
    {
        using var cancelled = await _admin.PostAsync(AdminPath() + "/cancel", null);
        cancelled.StatusCode.Should().Be(HttpStatusCode.OK);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            _admin.PostAsJsonAsync(AdminPath() + "/assign",
                new AssignSubscriptionRequest { PlanId = _proPlanId })));
        try
        {
            responses.Count(x => x.StatusCode == HttpStatusCode.Created).Should().Be(1);
            responses.Count(x => x.StatusCode == HttpStatusCode.Conflict).Should().Be(4);
            await using var db = Db();
            (await db.Subscriptions.CountAsync(s =>
                s.MerchantId == _merchantId && s.Status == SubscriptionStatus.Active)).Should().Be(1);
        }
        finally
        {
            foreach (var resp in responses) resp.Dispose();
        }
    }

    [Fact]
    public async Task MissingMerchant_Returns404()
    {
        var nonexistent = Guid.NewGuid();
        using var result = await _admin.GetAsync(AdminPath(nonexistent));
        result.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var cancel = await _admin.PostAsync(AdminPath(nonexistent) + "/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
