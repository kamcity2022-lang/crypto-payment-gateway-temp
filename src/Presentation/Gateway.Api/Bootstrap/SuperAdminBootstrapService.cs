using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Bootstrap;

public class SuperAdminBootstrapService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SuperAdminBootstrapService> _logger;

    public SuperAdminBootstrapService(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<SuperAdminBootstrapService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // ✅ FIX: Seed all five roles idempotently, independently of SuperAdmin config
        var roles = new[] { "SuperAdmin", "Owner", "Admin", "Developer", "Viewer" };
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded)
                {
                    _logger.LogError("Failed to create role {Role}: {Errors}", role, string.Join(", ", result.Errors.Select(e => e.Description)));
                }
            }
        }

        // ✅ FIX: Never overwrite or modify an existing SuperAdmin
        var existingSuperAdmin = await userManager.Users.FirstOrDefaultAsync(u => u.IsSuperAdmin, cancellationToken);
        if (existingSuperAdmin != null)
        {
            _logger.LogInformation("SuperAdmin bootstrap skipped: SuperAdmin already exists.");
            return;
        }

        var email = _configuration["Bootstrap:SuperAdminEmail"];
        var password = _configuration["Bootstrap:SuperAdminPassword"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogInformation("SuperAdmin bootstrap skipped: Bootstrap credentials not configured.");
            return;
        }

        // ✅ FIX: Fail safely if email belongs to a non-SuperAdmin account
        var existingUser = await userManager.FindByEmailAsync(email);
        if (existingUser != null)
        {
            _logger.LogError("SuperAdmin bootstrap failed: Email {Email} already belongs to a non-SuperAdmin account. Failing safely to prevent privilege escalation.", email);
            return;
        }

        // ✅ FIX: Ensure MerchantId = null and IsSuperAdmin = true
        var user = new ApplicationUser 
        { 
            UserName = email, 
            Email = email, 
            EmailConfirmed = true, 
            IsSuperAdmin = true, 
            MerchantId = null 
        };
        
        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            _logger.LogError("Failed to create SuperAdmin: {Errors}", string.Join(", ", createResult.Errors.Select(e => e.Description)));
            return;
        }

        var roleResult = await userManager.AddToRoleAsync(user, "SuperAdmin");
        if (!roleResult.Succeeded)
        {
            _logger.LogError("Failed to assign SuperAdmin role: {Errors}", string.Join(", ", roleResult.Errors.Select(e => e.Description)));
            return;
        }

        _logger.LogWarning("SuperAdmin bootstrap completed for {Email}. REMOVE BOOTSTRAP CREDENTIALS FROM CONFIGURATION IN PRODUCTION.", email);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}