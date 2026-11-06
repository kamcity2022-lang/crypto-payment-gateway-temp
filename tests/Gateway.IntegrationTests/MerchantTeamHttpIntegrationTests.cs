using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Api.Controllers;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public sealed class MerchantTeamHttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("team_management_tests").WithUsername("postgres")
        .WithPassword("postgres_test_password").Build();

    private TestWebApplicationFactory _factory = null!;
    private HttpClient _owner = null!;
    private HttpClient _admin = null!;
    private HttpClient _viewer = null!;
    private HttpClient _otherOwner = null!;
    private Guid _merchant;
    private Guid _otherMerchant;
    private string _memberId = string.Empty;
    private string _ownerId = string.Empty;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new TestWebApplicationFactory(_postgres.GetConnectionString());
        using var startup = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            _merchant = Guid.NewGuid();
            _otherMerchant = Guid.NewGuid();
            db.Merchants.Add(new Merchant(_merchant, "Team A",
                "hash_" + Guid.NewGuid().ToString("N"), "secret", "whsec"));
            db.Merchants.Add(new Merchant(_otherMerchant, "Team B",
                "hash_" + Guid.NewGuid().ToString("N"), "secret", "whsec"));
            await db.SaveChangesAsync();

            var owner = new ApplicationUser
            {
                UserName = "team-owner@example.com", Email = "team-owner@example.com",
                MerchantId = _merchant, EmailConfirmed = true
            };
            (await users.CreateAsync(owner, "OwnerSecret@2026!")).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(owner, "Owner")).Succeeded.Should().BeTrue();
            _ownerId = owner.Id;

            var member = new ApplicationUser
            {
                UserName = "team-member@example.com", Email = "team-member@example.com",
                MerchantId = _merchant, EmailConfirmed = true
            };
            (await users.CreateAsync(member, "MemberSecret@2026!")).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(member, "Viewer")).Succeeded.Should().BeTrue();
            _memberId = member.Id;
        }
        _owner = _factory.CreateClientWithAuth("team-owner@example.com",
            new[] { "Owner" }, _merchant);
        _admin = _factory.CreateClientWithAuth("team-admin@example.com",
            new[] { "Admin" }, _merchant);
        _viewer = _factory.CreateClientWithAuth("team-viewer@example.com",
            new[] { "Viewer" }, _merchant);
        _otherOwner = _factory.CreateClientWithAuth("other-owner@example.com",
            new[] { "Owner" }, _otherMerchant);
    }

    public async Task DisposeAsync()
    {
        _owner?.Dispose(); _admin?.Dispose(); _viewer?.Dispose(); _otherOwner?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private GatewayDbContext NewDb() =>
        new(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options);

    [Fact]
    public async Task OwnerCanInviteLimitedRoleWithSingleUseEmailBoundToken()
    {
        using var response = await _owner.PostAsJsonAsync(
            "/api/v1/dashboard/team/invite",
            new InviteTeamMemberRequest
            {
                Email = "invited-person@example.com", Role = "Developer"
            });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("inviteToken").GetString()!;
        token.Should().HaveLength(64);

        await using var db = NewDb();
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var record = await db.MerchantClaimTokens.SingleAsync(t => t.HashedToken == hash);
        record.InvitedEmail.Should().Be("invited-person@example.com");
        record.InvitedRole.Should().Be("Developer");
        record.MerchantId.Should().Be(_merchant);
        record.IsUsed.Should().BeFalse();

        using var duplicate = await _owner.PostAsJsonAsync(
            "/api/v1/dashboard/team/invite",
            new { email = "invited-person@example.com", role = "Viewer" });
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var badRole = await _owner.PostAsJsonAsync(
            "/api/v1/dashboard/team/invite",
            new { email = "not-owner@example.com", role = "Owner" });
        badRole.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InviteActivation_RejectsWrongEmail_ThenAssignsInvitedRoleOnce()
    {
        using var invite = await _owner.PostAsJsonAsync(
            "/api/v1/dashboard/team/invite",
            new { email = "new-developer@example.com", role = "Developer" });
        invite.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await invite.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("inviteToken").GetString()!;

        using var anonymous = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var wrong = await anonymous.PostAsJsonAsync(
            "/api/v1/auth/activate",
            new { email = "wrong@example.com", password = "NewDevPassword@123", token });
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var valid = await anonymous.PostAsJsonAsync(
            "/api/v1/auth/activate",
            new { email = "new-developer@example.com", password = "NewDevPassword@123", token });
        valid.StatusCode.Should().Be(HttpStatusCode.OK,
            await valid.Content.ReadAsStringAsync());

        using var replay = await anonymous.PostAsJsonAsync(
            "/api/v1/auth/activate",
            new { email = "new-developer@example.com", password = "NewDevPassword@123", token });
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var created = await users.FindByEmailAsync("new-developer@example.com");
        created.Should().NotBeNull();
        created!.MerchantId.Should().Be(_merchant);
        (await users.GetRolesAsync(created)).Should().ContainSingle().Which.Should().Be("Developer");
    }

    [Fact]
    public async Task AdminAndViewerCannotManageTeamAndOtherTenantCannotSeeUsers()
    {
        using var denied = await _admin.PostAsJsonAsync(
            "/api/v1/dashboard/team/invite",
            new { email = "admin-denied@example.com", role = "Viewer" });
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var deniedViewer = await _viewer.GetAsync("/api/v1/dashboard/team");
        deniedViewer.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var list = await _otherOwner.GetAsync("/api/v1/dashboard/team");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync())
            .Should().NotContain("team-member@example.com");

        using var target = await _otherOwner.PutAsJsonAsync(
            "/api/v1/dashboard/team/" + _memberId + "/role",
            new { role = "Admin" });
        target.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OwnerChangesDelegatedRoleAndDisablesMemberWithSecurityStampRotation()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var initial = await users.FindByIdAsync(_memberId);
        initial.Should().NotBeNull();
        var initialStamp = await users.GetSecurityStampAsync(initial!);

        using var change = await _owner.PutAsJsonAsync(
            "/api/v1/dashboard/team/" + _memberId + "/role",
            new { role = "Developer" });
        change.StatusCode.Should().Be(HttpStatusCode.OK,
            await change.Content.ReadAsStringAsync());

        // The test's scoped UserManager/DbContext tracked the member BEFORE
        // the HTTP request. Reload from the database after the mutation:
        // otherwise FindByIdAsync returns the stale tracked entity.
        scope.ServiceProvider.GetRequiredService<GatewayDbContext>()
            .ChangeTracker.Clear();
        var changed = await users.FindByIdAsync(_memberId);
        var roles = await users.GetRolesAsync(changed!);
        roles.Should().ContainSingle().Which.Should().Be("Developer");
        (await users.GetSecurityStampAsync(changed!)).Should().NotBe(initialStamp);

        using var disable = await _owner.PutAsJsonAsync(
            "/api/v1/dashboard/team/" + _memberId + "/status",
            new { disabled = true });
        disable.StatusCode.Should().Be(HttpStatusCode.OK);
        scope.ServiceProvider.GetRequiredService<GatewayDbContext>()
            .ChangeTracker.Clear();
        (await users.FindByIdAsync(_memberId))!.IsDisabled.Should().BeTrue();

        using var selfOwner = await _owner.PutAsJsonAsync(
            "/api/v1/dashboard/team/" + _ownerId + "/status",
            new { disabled = true });
        selfOwner.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var promote = await _owner.PutAsJsonAsync(
            "/api/v1/dashboard/team/" + _memberId + "/role",
            new { role = "Owner" });
        promote.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
