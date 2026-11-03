using Gateway.Application.Contracts;
using Gateway.Blockchain.Tron;
using Gateway.Domain.Entities;
using Gateway.Infrastructure.Security;
using Gateway.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Connection
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is required.");

builder.Services.AddDbContext<GatewayDbContext>(options =>
    options.UseNpgsql(connectionString));

// 2. Identity Setup (Human Users)
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

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options => { /* ... */ })
    .AddEntityFrameworkStores<GatewayDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, Gateway.Api.Identity.CustomUserClaimsPrincipalFactory>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SuperAdminOnly", policy => policy.RequireRole("SuperAdmin"));
    options.AddPolicy("MerchantOwner", policy => policy.RequireRole("Owner", "SuperAdmin").AddRequirements(new Gateway.Api.Authorization.MerchantIdRequirement()));
    options.AddPolicy("MerchantAdmin", policy => policy.RequireRole("Owner", "Admin", "SuperAdmin").AddRequirements(new Gateway.Api.Authorization.MerchantIdRequirement()));
    options.AddPolicy("MerchantDeveloper", policy => policy.RequireRole("Owner", "Admin", "Developer", "SuperAdmin").AddRequirements(new Gateway.Api.Authorization.MerchantIdRequirement()));
    options.AddPolicy("MerchantViewer", policy => policy.RequireRole("Owner", "Admin", "Developer", "Viewer", "SuperAdmin").AddRequirements(new Gateway.Api.Authorization.MerchantIdRequirement()));
});

builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Gateway.Api.Authorization.MerchantIdHandler>();
builder.Services.AddHostedService<Gateway.Api.Bootstrap.SuperAdminBootstrapService>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/api/v1/auth/login";
});

// 3. Core Infrastructure Services
builder.Services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

// 4. Controllers & Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 5. Health Check
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

var app = builder.Build();

// ✅ FIX: Apply EF Core migrations at startup to ensure tables exist before HostedServices run
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
    await dbContext.Database.MigrateAsync();
}

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