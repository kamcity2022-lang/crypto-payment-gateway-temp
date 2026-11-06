using Gateway.Api.Identity;
using Gateway.Application.Contracts;
using Gateway.Blockchain.Tron;
using Gateway.Domain.Entities;
using Gateway.Infrastructure.Security;
using Gateway.Infrastructure.ExchangeRates;
using Gateway.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Connection (from config/UserSecrets/env vars)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Fatal: 'DefaultConnection' connection string is missing. " +
        "Set it via User Secrets (Development) or environment variables (Production).");
}

builder.Services.AddDbContext<GatewayDbContext>(options =>
    options.UseNpgsql(connectionString));

// 2. Identity Setup (Human Users) — SINGLE registration
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequiredLength = 8;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.SignIn.RequireConfirmedEmail = true;
})
.AddEntityFrameworkStores<GatewayDbContext>()
.AddDefaultTokenProviders();

// Role or disabled-state changes update the user security stamp; validate
// on every request rather than allowing stale role claims for 30 minutes.
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.Zero);

// Custom claims factory to inject MerchantId into auth cookie
builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, CustomUserClaimsPrincipalFactory>();

// 3. Authorization Policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SuperAdminOnly", policy => policy.RequireRole("SuperAdmin"));
    options.AddPolicy("MerchantOwner", policy => policy.RequireRole("Owner", "SuperAdmin")
        .AddRequirements(new Gateway.Api.Authorization.MerchantIdRequirement()));
    options.AddPolicy("MerchantAdmin", policy => policy.RequireRole("Owner", "Admin", "SuperAdmin")
        .AddRequirements(new Gateway.Api.Authorization.MerchantIdRequirement()));
    options.AddPolicy("MerchantDeveloper", policy => policy.RequireRole("Owner", "Admin", "Developer", "SuperAdmin")
        .AddRequirements(new Gateway.Api.Authorization.MerchantIdRequirement()));
    options.AddPolicy("MerchantViewer", policy => policy.RequireRole("Owner", "Admin", "Developer", "Viewer", "SuperAdmin")
        .AddRequirements(new Gateway.Api.Authorization.MerchantIdRequirement()));
});

builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    Gateway.Api.Authorization.MerchantIdHandler>();

// 4. Cookie Configuration
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/api/v1/auth/login";
});

// 5. Core Infrastructure Services
builder.Services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IExchangeRateProvider, CoinbaseUsdtUsdRateProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});


// 7. Controllers & Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 8. Health Check
builder.Services.AddHealthChecks()
    .AddAsyncCheck("Database", async (ct) =>
    {
        try
        {
            var optionsBuilder = new DbContextOptionsBuilder<GatewayDbContext>();
            optionsBuilder.UseNpgsql(connectionString);
            await using var db = new GatewayDbContext(optionsBuilder.Options);
            var canConnect = await db.Database.CanConnectAsync(ct);
            return canConnect
                ? HealthCheckResult.Healthy("Database connected.")
                : HealthCheckResult.Unhealthy("Database unreachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    });

builder.Services.AddHttpClient<ITronSweepService, TronSweepService>();

// 9. Hosted Services (Bootstrap runs AFTER migrations)
builder.Services.AddHostedService<Gateway.Api.Bootstrap.SuperAdminBootstrapService>();

var app = builder.Build();

// ✅ SINGLE migration execution — runs BEFORE hosted services start
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
    await dbContext.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Crypto Gateway API v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/healthz");

app.Run();