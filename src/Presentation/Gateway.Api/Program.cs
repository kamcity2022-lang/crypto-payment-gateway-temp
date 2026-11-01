using Gateway.Application.Contracts;
using Gateway.Blockchain.Tron;
using Gateway.Infrastructure.Security;
using Gateway.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Connection (Strict configuration requirement, no fallback)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                       ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is required.");

builder.Services.AddDbContext<GatewayDbContext>(options =>
    options.UseNpgsql(connectionString));

// 2. Core Infrastructure Services
builder.Services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

// 3. Controllers & Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 4. Health Check
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

app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/healthz");

app.Run();