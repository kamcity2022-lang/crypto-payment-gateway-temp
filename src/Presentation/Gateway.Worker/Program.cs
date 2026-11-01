using Gateway.Application.Contracts;
using Gateway.Blockchain.Tron;
using Gateway.Infrastructure.Webhooks;
using Gateway.Persistence;
using Gateway.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        var connectionString = hostContext.Configuration.GetConnectionString("DefaultConnection")
                               ?? "Host=localhost;Port=5432;Database=CryptoGatewayDb;Username=postgres;Password=postgres";

        services.AddDbContext<GatewayDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddHttpClient<ITronScannerService, TronScannerService>();
        services.AddHttpClient<IWebhookDispatcher, WebhookDispatcher>();

        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();