using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Domain.Entities;
using Gateway.Persistence;
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
        var email = _configuration["Bootstrap:SuperAdminEmail"];
        var password = _configuration["Bootstrap:SuperAdminPassword"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogInformation("SuperAdmin bootstrap skipped: Bootstrap credentials not configured.");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        var roles = new[] { "SuperAdmin", "Owner", "Admin", "Developer", "Viewer" };
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        var existingSuperAdmin = await userManager.Users.FirstOrDefaultAsync(u => u.IsSuperAdmin, cancellationToken);
        if (existingSuperAdmin != null)
        {
            _logger.LogInformation("SuperAdmin bootstrap skipped: SuperAdmin already exists.");
            return;
        }

        var user = await userManager.FindByEmailAsync(email);
        if (user == null)
        {
            user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, IsSuperAdmin = true, MerchantId = null };
            var result = await userManager.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                _logger.LogError("Failed to create SuperAdmin: {Errors}", string.Join(", ", result.Errors.Select(e => e.Description)));
                return;
            }
        }
        
        if (!await userManager.IsInRoleAsync(user, "SuperAdmin"))
        {
            await userManager.AddToRoleAsync(user, "SuperAdmin");
            user.IsSuperAdmin = true;
            await userManager.UpdateAsync(user);
        }

        _logger.LogWarning("SuperAdmin bootstrap completed for {Email}. REMOVE BOOTSTRAP CREDENTIALS FROM CONFIGURATION IN PRODUCTION.", email);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}