using System;
using System.Net;
using System.Net.Sockets;

namespace Gateway.Infrastructure.Security;

public static class SsrfValidator
{
    // بررسی پیش‌از‌پرواز فقط برای لایه API (مثلاً CallbackUrl فاکتور).
    // برای تحویل وب‌هوک کافی نیست؛ حتماً باید ConnectCallback در
    // Gateway.Worker/Program.cs هم IP را پین و اعتبارسنجی کند.
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

        // نرمال‌سازی IPv4-mapped IPv6 (::ffff:a.b.c.d) به IPv4 خالص
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork) return IsProhibitedIPv4(ip);
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return IsProhibitedIPv6(ip);

        return true; // خانواده آدرس ناشناخته -> رد
    }

    private static bool IsProhibitedIPv4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();

        if (IPAddress.IsLoopback(ip)) return true;                    // 127.0.0.0/8
        if (b[0] == 0) return true;                                    // 0.0.0.0/8
        if (b[0] == 10) return true;                                   // 10.0.0.0/8
        if (b[0] == 100 && (b[1] & 0xC0) == 64) return true;           // 100.64.0.0/10 CGNAT
        if (b[0] == 127) return true;                                  // 127.0.0.0/8 (صریح)
        if (b[0] == 169 && b[1] == 254) return true;                   // 169.254.0.0/16 + metadata
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;      // 172.16.0.0/12
        if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;        // 192.0.0.0/24 IETF
        if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;        // 192.0.2.0/24 TEST-NET-1
        if (b[0] == 192 && b[1] == 168) return true;                   // 192.168.0.0/16
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;    // 198.18.0.0/15 benchmarking
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;     // 198.51.100.0/24 TEST-NET-2
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;      // 203.0.113.0/24 TEST-NET-3
        if (b[0] >= 224) return true;                                  // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved

        return false; // IPv4 unicast عمومی
    }

    private static bool IsProhibitedIPv6(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;      // ::1
        if (ip.Equals(IPAddress.IPv6None)) return true; // ::
        if (ip.IsIPv6LinkLocal) return true;            // fe80::/10
        if (ip.IsIPv6Multicast) return true;            // ff00::/8
        if (ip.IsIPv6SiteLocal) return true;            // fec0::/10 deprecated

        var b = ip.GetAddressBytes();

        // ULA fc00::/7 (شامل fd00::/8 و رنج‌های IMDSv2 مثل fd00:ec2::254)
        if ((b[0] & 0xFE) == 0xFC) return true;

        // 2001:db8::/32 documentation
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return true;

        // Teredo 2001::/32 و 6to4 2002::/16 — مکانیزم‌های گذار که IPv4 جاسازی می‌کنند
        // و می‌توانند بردار دورزدن باشند؛ برای وب‌هوک پرداخت سخت‌گیرانه رد می‌شوند
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return true; // Teredo
        if (b[0] == 0x20 && b[1] == 0x02) return true;                                  // 6to4

        // NAT64 64:ff9b::/96
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B &&
            b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0) return true;

        return false; // فرض بر unicast جهانی
    }
}