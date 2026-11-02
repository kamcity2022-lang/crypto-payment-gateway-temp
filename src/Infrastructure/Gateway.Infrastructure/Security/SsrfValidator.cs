using System;
using System.Net;
using System.Net.Sockets;

namespace Gateway.Infrastructure.Security;

public static class SsrfValidator
{
    public static bool IsDestinationSafe(string urlString, bool allowHttp = false, bool allowLoopbackForTesting = false)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && (!allowHttp || uri.Scheme != Uri.UriSchemeHttp)) return false;
        if (!allowLoopbackForTesting && uri.IsLoopback) return false;

        try
        {
            var ips = Dns.GetHostAddresses(uri.DnsSafeHost);
            if (ips.Length == 0) return false;
            foreach (var ip in ips)
            {
                if (allowLoopbackForTesting && IPAddress.IsLoopback(ip)) continue;
                if (IsProhibitedIp(ip)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    public static bool IsProhibitedIp(IPAddress ip)
    {
        if (ip == null) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork) return IsProhibitedIPv4(ip);
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return IsProhibitedIPv6(ip);

        return true;
    }

    private static bool IsProhibitedIPv4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (IPAddress.IsLoopback(ip)) return true;
        if (b[0] == 0) return true;
        if (b[0] == 10) return true;
        if (b[0] == 100 && (b[1] & 0xC0) == 64) return true; // CGNAT
        if (b[0] == 127) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;
        if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;
        if (b[0] >= 224) return true;
        return false;
    }

    private static bool IsProhibitedIPv6(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.IPv6None)) return true;
        if (ip.IsIPv6LinkLocal) return true;
        if (ip.IsIPv6Multicast) return true;
        if (ip.IsIPv6SiteLocal) return true;

        var b = ip.GetAddressBytes();

        if ((b[0] & 0xFE) == 0xFC) return true; // ULA
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return true; // Documentation
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return true; // Teredo
        if (b[0] == 0x20 && b[1] == 0x02) return true; // 6to4
        
        // NAT64 64:ff9b::/96
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B &&
            b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0) return true;

        // NAT64 well-known 64:ff9b:1::/48 (RFC 8215)
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B &&
            b[4] == 0x00 && b[5] == 0x01) return true;

        // Discard 100::/64 (RFC 6666)
        if (b[0] == 0x01 && b[1] == 0x00 && b[2] == 0 && b[3] == 0 &&
            b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0) return true;

        // ORCHIDv2 2001:20::/28
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && (b[3] & 0xF0) == 0x20) return true;

        // Benchmarking BMWG 2001:2::/48
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x02 &&
            b[4] == 0 && b[5] == 0) return true;

        return false;
    }
}