using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
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
        var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var requiredRoles = new[] { "SuperAdmin", "Owner", "Admin", "Developer", "Viewer" };
        
        // STEP 1: Create required roles
        foreach (var role in requiredRoles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded)
                {
                    _logger.LogError("Bootstrap ABORTED: Failed to create role '{Role}'. Errors: {Errors}",
                        role, string.Join(", ", result.Errors.Select(e => e.Description)));
                    return;
                }
            }
        }

        // STEP 1.5: Ensure Default Plan Exists (idempotent)
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

        // STEP 1.6: Ensure all existing merchants have an active subscription (idempotent)
        var defaultPlan = await dbContext.Plans
            .FirstOrDefaultAsync(p => p.IsDefault && p.IsActive, cancellationToken);
        
        if (defaultPlan != null)
        {
            var merchantsWithoutSubscription = await dbContext.Merchants
                .Where(m => !dbContext.Subscriptions.Any(s => 
                    s.MerchantId == m.Id && s.Status == SubscriptionStatus.Active))
                .Select(m => m.Id)
                .ToListAsync(cancellationToken);

            if (merchantsWithoutSubscription.Count > 0)
            {
                foreach (var merchantId in merchantsWithoutSubscription)
                {
                    var subscription = new Subscription(merchantId, defaultPlan.Id);
                    dbContext.Subscriptions.Add(subscription);
                }
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Bootstrap: Assigned default subscriptions to {Count} existing merchants.", 
                    merchantsWithoutSubscription.Count);
            }
        }

        // STEP 2: Check if SuperAdmin already exists
        var existingSuperAdmin = await userManager.Users
            .FirstOrDefaultAsync(u => u.IsSuperAdmin, cancellationToken);
        if (existingSuperAdmin is not null)
        {
            _logger.LogInformation("Bootstrap skipped: SuperAdmin account already exists.");
            return;
        }

        // STEP 3: Read credentials
        var email = _configuration["Bootstrap:SuperAdminEmail"];
        var password = _configuration["Bootstrap:SuperAdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogInformation("Bootstrap skipped: SuperAdmin credentials not configured.");
            return;
        }

        // STEP 4: Check if email already used
        var existingUser = await userManager.FindByEmailAsync(email);
        if (existingUser is not null)
        {
            _logger.LogError("Bootstrap FAILED: Email '{Email}' already belongs to a non-SuperAdmin account.", email);
            return;
        }

        // STEP 5: Create SuperAdmin
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
            _logger.LogError("Bootstrap FAILED: Could not create SuperAdmin. Errors: {Errors}",
                string.Join(", ", createResult.Errors.Select(e => e.Description)));
            return;
        }

        // STEP 6: Assign role
        var roleResult = await userManager.AddToRoleAsync(user, "SuperAdmin");
        if (!roleResult.Succeeded)
        {
            _logger.LogError("Bootstrap FAILED: Role assignment failed. Errors: {Errors}",
                string.Join(", ", roleResult.Errors.Select(e => e.Description)));
            await userManager.DeleteAsync(user);
            return;
        }

        _logger.LogWarning("Bootstrap SUCCESS: SuperAdmin account created for '{Email}'.", email);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}