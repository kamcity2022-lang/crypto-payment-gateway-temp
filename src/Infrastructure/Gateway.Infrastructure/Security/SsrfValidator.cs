using System;
using System.Net;
using System.Net.Sockets;

namespace Gateway.Infrastructure.Security;

public static class SsrfValidator
{
    public static bool IsDestinationSafe(string urlString)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
            return false;

        // Only HTTPS or HTTP permitted
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return false;

        try
        {
            var hostEntry = Dns.GetHostEntry(uri.DnsSafeHost);
            foreach (var ip in hostEntry.AddressList)
            {
                if (IsInternalOrReservedIp(ip))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsInternalOrReservedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            // 169.254.0.0/16 (Link-local & Cloud Metadata services)
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            // 127.0.0.0/8
            if (bytes[0] == 127) return true;

            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
        }

        return false;
    }
}