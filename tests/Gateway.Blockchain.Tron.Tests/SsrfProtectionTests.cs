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
    public void IsInternalOrReservedIp_ShouldDetectInternalAddresses(string ipString, bool isInternal)
    {
        var ip = IPAddress.Parse(ipString);
        var result = SsrfValidator.IsInternalOrReservedIp(ip);

        result.Should().Be(isInternal);
    }
}