using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Infrastructure.Security;
using Xunit;

namespace Gateway.Blockchain.Tron.Tests;

public class SecureHttpClientFactoryTests
{
    [Fact]
    public async Task ConnectCallback_Localhost_ShouldThrowAccessDenied()
    {
        var handler = SecureHttpClientFactory.CreateSecureHandler();
        using var client = new HttpClient(handler);

        Func<Task> act = async () => await client.GetAsync("http://localhost:12345/");

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.InnerException.Should().BeOfType<SocketException>();
        ((SocketException)ex.Which.InnerException!).SocketErrorCode.Should().Be(SocketError.AccessDenied);
    }

    [Fact]
    public void CreateSecureHandler_ShouldDisableAutoRedirect()
    {
        var handler = SecureHttpClientFactory.CreateSecureHandler();
        handler.AllowAutoRedirect.Should().BeFalse();
    }

    [Fact]
    public async Task ConnectCallback_PrivateIp_ShouldThrowAccessDenied()
    {
        var handler = SecureHttpClientFactory.CreateSecureHandler();
        using var client = new HttpClient(handler);

        // 10.0.0.1 is a private IP (RFC 1918)
        Func<Task> act = async () => await client.GetAsync("http://10.0.0.1:12345/");

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.InnerException.Should().BeOfType<SocketException>();
        ((SocketException)ex.Which.InnerException!).SocketErrorCode.Should().Be(SocketError.AccessDenied);
    }

    [Fact]
    public void CreateSecureHandler_ShouldHaveConnectTimeout()
    {
        var handler = SecureHttpClientFactory.CreateSecureHandler();
        handler.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }
}