using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Api.Controllers;
using Gateway.Application.DTOs;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public class AdminTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_admin_db").WithUsername("postgres").WithPassword("postgres_test_password").Build();
    
    private GatewayDbContext _dbContext = null!;
    private AdminController _controller = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        var opts = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;
        
        _dbContext = new GatewayDbContext(opts);
        await _dbContext.Database.MigrateAsync();

        _controller = new AdminController(_dbContext);

        // Seed test data
        var merchant1 = new Merchant(Guid.NewGuid(), "Active Merchant Corp", "hash1", "secret1", "webhook1");
        
        // ✅ اصلاح: تغییر نام به "Dormant" برای جلوگیری از تداخل جستجوی زیررشته‌ای با کلمه "Active"
        var merchant2 = new Merchant(Guid.NewGuid(), "Dormant Merchant LLC", "hash2", "secret2", "webhook2");
        
        _dbContext.Merchants.AddRange(merchant1, merchant2);
        
        var wallet = new Wallet(merchant1.Id, "xpub123", "TRON");
        _dbContext.Wallets.Add(wallet);
        
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    private void SetupHttpContext(params Claim[] claims)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    [Fact]
    public async Task GetOverview_SuperAdmin_ShouldReturnValidOverview()
    {
        SetupHttpContext(new Claim(ClaimTypes.Role, "SuperAdmin"));

        var result = await _controller.GetOverview(CancellationToken.None);
        
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var overview = okResult.Value.Should().BeOfType<PlatformOverviewDto>().Subject;
        
        overview.TotalMerchants.Should().Be(2);
        overview.ActiveMerchants.Should().Be(2); 
        overview.InactiveMerchants.Should().Be(0);
        overview.TotalWallets.Should().Be(1);
    }

    [Fact]
    public async Task GetMerchants_SuperAdmin_PaginationAndSearch_ShouldWork()
    {
        SetupHttpContext(new Claim(ClaimTypes.Role, "SuperAdmin"));

        var filter = new AdminMerchantFilterRequest { Page = 1, PageSize = 10, Search = "Active" };
        var result = await _controller.GetMerchants(filter, CancellationToken.None);
        
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var pagedResult = okResult.Value.Should().BeOfType<PagedMerchantResultDto>().Subject;
        
        pagedResult.TotalCount.Should().Be(1);
        pagedResult.Items.Should().ContainSingle();
        pagedResult.Items.First().Name.Should().Be("Active Merchant Corp");
        pagedResult.Items.First().WalletCount.Should().Be(1);
    }

    [Fact]
    public async Task GetMerchantDetails_SuperAdmin_ValidMerchant_ShouldReturnDetails()
    {
        SetupHttpContext(new Claim(ClaimTypes.Role, "SuperAdmin"));
        
        var merchant = await _dbContext.Merchants.FirstOrDefaultAsync(m => m.Name == "Active Merchant Corp");
        merchant.Should().NotBeNull("Test data should be seeded correctly");

        var result = await _controller.GetMerchantDetails(merchant.Id, CancellationToken.None);
        
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var details = okResult.Value.Should().BeOfType<MerchantDetailsDto>().Subject;
        
        details.MerchantId.Should().Be(merchant.Id);
        details.Name.Should().Be(merchant.Name);
        details.WalletCount.Should().Be(1); 
        details.TotalInvoiceCount.Should().Be(0);
        details.Wallets.Should().ContainSingle();
    }

    [Fact]
    public async Task GetMerchantDetails_SuperAdmin_NonExistentMerchant_ShouldReturnNotFound()
    {
        SetupHttpContext(new Claim(ClaimTypes.Role, "SuperAdmin"));
        
        var fakeId = Guid.NewGuid();
        var result = await _controller.GetMerchantDetails(fakeId, CancellationToken.None);
        
        result.Should().BeOfType<NotFoundObjectResult>();
        var notFoundResult = result as NotFoundObjectResult;
        notFoundResult.Value.Should().BeEquivalentTo(new { error = "Merchant not found." });
    }

    [Fact]
    public void AdminDtos_DoNotExposeSensitiveCredentials()
    {
        var summaryProps = typeof(MerchantSummaryDto).GetProperties();
        summaryProps.Should().NotContain(p => p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        summaryProps.Should().NotContain(p => p.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
        summaryProps.Should().NotContain(p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));

        var detailsProps = typeof(MerchantDetailsDto).GetProperties();
        detailsProps.Should().NotContain(p => p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        detailsProps.Should().NotContain(p => p.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
        detailsProps.Should().NotContain(p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));
    }
}