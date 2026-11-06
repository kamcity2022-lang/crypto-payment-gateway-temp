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
        // Fail before starting the hosted Worker if the operator has not
        // explicitly selected a network and (for mainnet) opted in.
        _ = TronNetworkEndpoints.FromConfiguration(hostContext.Configuration);

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