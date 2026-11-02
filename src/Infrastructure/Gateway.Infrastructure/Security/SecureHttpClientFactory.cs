using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Gateway.Infrastructure.Security;

public static class SecureHttpClientFactory
{
    public static SocketsHttpHandler CreateSecureHandler()
    {
        return new SocketsHttpHandler
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
        };
    }
}