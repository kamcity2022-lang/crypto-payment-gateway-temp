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
        if (IPAddress.IsLoopback(ip)) return true; // ::1
        if (ip.Equals(IPAddress.IPv6None)) return true; // ::
        if (ip.IsIPv6LinkLocal) return true; // fe80::/10
        if (ip.IsIPv6Multicast) return true; // ff00::/8
        if (ip.IsIPv6SiteLocal) return true; // fec0::/10

        var b = ip.GetAddressBytes();

        // ULA fc00::/7
        if ((b[0] & 0xFE) == 0xFC) return true;

        // IPv4-compatible IPv6 ::/96 (Deprecated)
        if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 &&
            b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
            b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0) return true;

        // IETF Protocol Assignments 2001::/23 (includes Teredo, ORCHID, etc.)
        if (b[0] == 0x20 && b[1] == 0x01 && (b[2] & 0xFE) == 0x00) return true;

        // Documentation 2001:db8::/32
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return true;

        // 6to4 2002::/16
        if (b[0] == 0x20 && b[1] == 0x02) return true;

        // NAT64 64:ff9b::/96
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B &&
            b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0) return true;

        // NAT64 well-known 64:ff9b:1::/48
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B &&
            b[4] == 0x00 && b[5] == 0x01) return true;

        // Discard 100::/64
        if (b[0] == 0x01 && b[1] == 0x00 && b[2] == 0 && b[3] == 0 &&
            b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0) return true;

        // 3fff::/20 (Documentation/Reserved)
        if (b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xF0) == 0) return true;

        // 5f00::/16 (Reserved/Special)
        if (b[0] == 0x5f && b[1] == 0x00) return true;

        return false;
    }
}