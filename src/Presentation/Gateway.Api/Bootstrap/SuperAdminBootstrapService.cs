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
        using var scope = _serviceProvider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var requiredRoles = new[] { "SuperAdmin", "Owner", "Admin", "Developer", "Viewer" };

        // STEP 1: Create required roles. Abort immediately if any fail.
        foreach (var role in requiredRoles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded)
                {
                    _logger.LogError(
                        "Bootstrap ABORTED: Failed to create role '{Role}'. Errors: {Errors}",
                        role,
                        string.Join(", ", result.Errors.Select(e => e.Description)));
                    return; // Stop execution to prevent partial state
                }
            }
        }
        
        // STEP 1.5: Ensure Default Plan Exists
        var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        if (!await dbContext.Plans.AnyAsync(p => p.IsDefault, cancellationToken))
        {
            var starterPlan = new Plan(
                name: "Starter", 
                description: "Default Free/Starter Tier", 
                monthlyInvoiceLimit: 100, 
                monthlyVolumeLimitUsd: 5000m, 
                priceUsd: 0m, 
                isDefault: true);
            dbContext.Plans.Add(starterPlan);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Bootstrap: Default 'Starter' plan created.");
        }

        // STEP 2: Check if a SuperAdmin already exists.
        var existingSuperAdmin = await userManager.Users
            .FirstOrDefaultAsync(u => u.IsSuperAdmin, cancellationToken);

        if (existingSuperAdmin is not null)
        {
            _logger.LogInformation("Bootstrap skipped: SuperAdmin account already exists.");
            return;
        }

        // STEP 3: Read credentials from configuration.
        var email = _configuration["Bootstrap:SuperAdminEmail"];
        var password = _configuration["Bootstrap:SuperAdminPassword"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogInformation("Bootstrap skipped: SuperAdmin credentials not configured in appsettings.");
            return;
        }

        // STEP 4: Check if the email is already used by a non-SuperAdmin user.
        var existingUser = await userManager.FindByEmailAsync(email);
        if (existingUser is not null)
        {
            _logger.LogError(
                "Bootstrap FAILED: Email '{Email}' already belongs to a non-SuperAdmin account. " +
                "Refusing to promote existing user for security reasons.", email);
            return;
        }

        // STEP 5: Create the SuperAdmin user.
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            IsSuperAdmin = true,
            MerchantId = null // SuperAdmin is cross-tenant
        };

        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            _logger.LogError(
                "Bootstrap FAILED: Could not create SuperAdmin user. Errors: {Errors}",
                string.Join(", ", createResult.Errors.Select(e => e.Description)));
            return;
        }

        // STEP 6: Assign the SuperAdmin role.
        var roleResult = await userManager.AddToRoleAsync(user, "SuperAdmin");
        if (!roleResult.Succeeded)
        {
            _logger.LogError(
                "Bootstrap FAILED: User created but role assignment failed. " +
                "Attempting to clean up partially created user. Errors: {Errors}",
                string.Join(", ", roleResult.Errors.Select(e => e.Description)));
            
            // CRITICAL FIX: Delete the orphaned user so the next startup can retry cleanly
            var deleteResult = await userManager.DeleteAsync(user);
            if (!deleteResult.Succeeded)
            {
                _logger.LogCritical(
                    "Bootstrap CRITICAL FAILURE: Could not delete partially created user. " +
                    "Manual intervention required. User '{Email}' exists without SuperAdmin role. " +
                    "Delete errors: {Errors}",
                    email,
                    string.Join(", ", deleteResult.Errors.Select(e => e.Description)));
            }
            else
            {
                _logger.LogWarning(
                    "Bootstrap cleanup successful: Partially created user '{Email}' deleted. " +
                    "Next startup will retry bootstrap.", email);
            }
            return;
        }

        _logger.LogWarning(
            "Bootstrap SUCCESS: SuperAdmin account created for '{Email}'. " +
            "SECURITY WARNING: Remove Bootstrap credentials from configuration in production!",
            email);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}