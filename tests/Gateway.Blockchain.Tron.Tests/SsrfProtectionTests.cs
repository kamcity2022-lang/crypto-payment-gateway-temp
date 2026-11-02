using System.Net;
using FluentAssertions;
using Gateway.Infrastructure.Security;
using Xunit;

namespace Gateway.Domain.Tests;

public class SsrfProtectionTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("192.168.1.100", true)]
    [InlineData("172.16.5.4", true)]
    [InlineData("169.254.169.254", true)] // AWS/GCP Metadata IP
    [InlineData("8.8.8.8", false)]        // Public Internet IP
    [InlineData("1.1.1.1", false)]
    public void IsProhibitedIp_ShouldDetectInternalAddresses(string ipString, bool isInternal)
    {
        var ip = IPAddress.Parse(ipString);
        var result = SsrfValidator.IsProhibitedIp(ip);

        result.Should().Be(isInternal);
    }
    
    [Theory]
    [InlineData("::1", true)]                                     // Loopback
    [InlineData("fe80::1", true)]                                 // Link-local
    [InlineData("ff02::1", true)]                                 // Multicast
    [InlineData("fc00::1", true)]                                 // ULA
    [InlineData("2001:db8::1", true)]                             // Documentation
    [InlineData("64:ff9b::1", true)]                              // NAT64
    [InlineData("64:ff9b:1::1", true)]                            // NAT64 well-known
    [InlineData("100::1", true)]                                  // Discard
    [InlineData("2001:20::1", true)]                              // ORCHIDv2
    [InlineData("2001:2::1", true)]                               // Benchmarking BMWG
    [InlineData("::ffff:192.168.1.1", true)]                      // IPv4-mapped private
    [InlineData("::ffff:8.8.8.8", false)]                         // IPv4-mapped public
    [InlineData("2606:4700:4700::1111", false)]                   // Public IPv6 (Cloudflare)
    public void IsProhibitedIp_IPv6_ShouldDetectSpecialRanges(string ipString, bool isProhibited)
    {
        var ip = IPAddress.Parse(ipString);
        var result = SsrfValidator.IsProhibitedIp(ip);
        result.Should().Be(isProhibited);
    }
    
}