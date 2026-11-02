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
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var host = context.DnsEndPoint.Host;
                    IPAddress[] ips;
                    try { ips = await Dns.GetHostAddressesAsync(host, cancellationToken); } 
                    catch { throw new SocketException((int)SocketError.HostNotFound); }

                    if (ips == null || ips.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

                    IPAddress? targetIp = null;
                    foreach (var ip in ips)
                    {
                        var checkIp = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
                        if (!SsrfValidator.IsProhibitedIp(checkIp)) { targetIp = checkIp; break; }
                    }

                    if (targetIp == null) throw new SocketException((int)SocketError.AccessDenied);

                    var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try 
                    { 
                        await socket.ConnectAsync(targetIp, context.DnsEndPoint.Port, cancellationToken); 
                        return new NetworkStream(socket, ownsSocket: true); 
                    } 
                    catch 
                    { 
                        socket.Dispose(); 
                        throw; 
                    }
                }
            });

        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();