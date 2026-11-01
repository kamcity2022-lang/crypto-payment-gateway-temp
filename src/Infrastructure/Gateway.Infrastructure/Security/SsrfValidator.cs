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

        // Force HTTPS in production environments
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return false;

        if (uri.IsLoopback || uri.HostNameType == UriHostNameType.IPv4 && IPAddress.IsLoopback(IPAddress.Parse(uri.Host)))
            return false;

        try
        {
            var ips = Dns.GetHostAddresses(uri.DnsSafeHost);
            if (ips.Length == 0) return false;

            foreach (var ip in ips)
            {
                if (IsProhibitedIp(ip))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsProhibitedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true; // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true; // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true; // 169.254.0.0/16
            if (b[0] == 127) return true;
            if (b[0] == 0) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            if (ip.Equals(IPAddress.IPv6Loopback) || ip.Equals(IPAddress.IPv6None)) return true;
        }

        return false;
    }
}