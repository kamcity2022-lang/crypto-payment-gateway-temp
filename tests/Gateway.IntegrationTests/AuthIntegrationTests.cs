using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public class AuthIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_auth_db").WithUsername("postgres").WithPassword("postgres_test_password").Build();
    
    private GatewayDbContext _dbContext = null!;
    private UserManager<ApplicationUser> _userManager = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        var opts = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(_dbContainer.GetConnectionString()).Options;
        _dbContext = new GatewayDbContext(opts);
        await _dbContext.Database.MigrateAsync();

        var userStore = new UserStore<ApplicationUser>(_dbContext);
        _userManager = new UserManager<ApplicationUser>(userStore, null, new PasswordHasher<ApplicationUser>(), null, null, null, null, null, null);
        
        // اطمینان از وجود نقش Owner در دیتابیس تست
        var roleStore = new RoleStore<IdentityRole>(_dbContext);
        var roleManager = new RoleManager<IdentityRole>(roleStore, null, null, null, null);
        if (!await _dbContext.Roles.AnyAsync(r => r.Name == "Owner"))
        {
            await roleManager.CreateAsync(new IdentityRole("Owner"));
        }
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    [Fact]
    public async Task ActivateAccount_ConcurrentAttempts_ShouldOnlySucceedOnce()
    {
        var merchantId = Guid.NewGuid();
        var plainToken = "secure-atomic-test-token";
        var tokenRecord = new MerchantClaimToken(merchantId, plainToken, TimeSpan.FromHours(1));
        
        _dbContext.MerchantClaimTokens.Add(tokenRecord);
        await _dbContext.SaveChangesAsync();

        var hashedToken = HashToken(plainToken);
        var now = DateTime.UtcNow;

        // شبیه‌سازی واقعی رقابت: استفاده از دو نمونه DbContext کاملاً جداگانه 
        // (دقیقاً مانند زمانی که دو درخواست HTTP همزمان به سرور می‌رسند)
        var options = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(_dbContainer.GetConnectionString()).Options;
        
        await using var db1 = new GatewayDbContext(options);
        await using var db2 = new GatewayDbContext(options);

        var task1 = db1.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"MerchantClaimTokens\" SET \"IsUsed\" = true WHERE \"HashedToken\" = {hashedToken} AND \"IsUsed\" = false AND \"ExpiresAtUtc\" > {now}");
        
        var task2 = db2.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"MerchantClaimTokens\" SET \"IsUsed\" = true WHERE \"HashedToken\" = {hashedToken} AND \"IsUsed\" = false AND \"ExpiresAtUtc\" > {now}");

        await Task.WhenAll(task1, task2);

        var totalRowsAffected = task1.Result + task2.Result;
        totalRowsAffected.Should().Be(1, "Only one concurrent request should successfully update the token due to the 'IsUsed = false' condition");

        // ✅ اصلاح: استفاده از AsNoTracking() برای دور زدن Change Tracker و خواندن داده به‌روز از دیتابیس
        var updatedToken = await _dbContext.MerchantClaimTokens.AsNoTracking().FirstAsync(t => t.Id == tokenRecord.Id);
        updatedToken.IsUsed.Should().BeTrue();
    }

    private static string HashToken(string token)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
}