using Gateway.Application.Contracts;
using Gateway.Blockchain.Tron;
using Gateway.Infrastructure.Security;
using Gateway.Infrastructure.Webhooks;
using Gateway.Persistence;
using Gateway.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Sockets;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        var connectionString = hostContext.Configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Fatal: 'DefaultConnection' connection string is required for Gateway.Worker.");

        services.AddDbContext<GatewayDbContext>(options => options.UseNpgsql(connectionString));
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddHttpClient<ITronScannerService, TronScannerService>();
        
        services.AddHttpClient<IWebhookDispatcher, WebhookDispatcher>()
            .ConfigurePrimaryHttpMessageHandler(Gateway.Infrastructure.Security.SecureHttpClientFactory.CreateSecureHandler);

        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();