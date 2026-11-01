using Gateway.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Add Gateway PostgreSQL DbContext
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
                       ?? "Host=localhost;Port=5432;Database=CryptoGatewayDb;Username=postgres;Password=postgres";

builder.Services.AddDbContext<GatewayDbContext>(options =>
    options.UseNpgsql(connectionString));

// 2. Add API Controllers
builder.Services.AddControllers();

// 3. Add Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 4. Add Health Checks (Clean native check without extra NuGet package)
builder.Services.AddHealthChecks()
    .AddAsyncCheck("Database", async () =>
    {
        try
        {
            using var scope = builder.Services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var canConnect = await db.Database.CanConnectAsync();
            return canConnect 
                ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Database connected.") 
                : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Database unreachable.");
        }
        catch (Exception ex)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(ex.Message);
        }
    });

var app = builder.Build();

// 4. Configure HTTP Pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Crypto Gateway API v1");
        c.RoutePrefix = string.Empty; // Swagger opens at http://localhost:PORT/
    });
}

app.MapHealthChecks("/healthz");

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();

app.Run();