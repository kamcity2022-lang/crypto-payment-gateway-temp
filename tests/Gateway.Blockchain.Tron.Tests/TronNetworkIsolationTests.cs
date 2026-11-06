using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Blockchain.Tron;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gateway.Blockchain.Tron.Tests;

public sealed class TronNetworkIsolationTests
{
    private const string MainnetContract = TronNetworkEndpoints.MainnetUsdtContract;
    // Illustrative testnet-format address, NOT a verified deployed USDT token.
    private const string TestnetContract = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";

    private static IConfiguration Config(string? network, string? contract, bool allowMainnet = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["Tron:Network"] = network,
            ["Tron:UsdtContractAddress"] = contract,
            ["Tron:EnableMainnet"] = allowMainnet ? "true" : "false"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void UnsetOrUnknownNetwork_RejectsBeforeSendingRequests(string? network)
    {
        Action create = () => TronNetworkEndpoints.FromConfiguration(
            Config(network, MainnetContract, true));
        create.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Mainnet_RequiresExplicitOptInAndCanonicalContract()
    {
        Action noOptIn = () => TronNetworkEndpoints.FromConfiguration(
            Config("Mainnet", MainnetContract));
        noOptIn.Should().Throw<InvalidOperationException>()
            .WithMessage("*Mainnet blocked*");

        Action wrongContract = () => TronNetworkEndpoints.FromConfiguration(
            Config("Mainnet", TestnetContract, true));
        wrongContract.Should().Throw<InvalidOperationException>()
            .WithMessage("*canonical USDT*");

        var mainnet = TronNetworkEndpoints.FromConfiguration(
            Config("Mainnet", MainnetContract, true));
        mainnet.BaseUrl.AbsoluteUri.Should().Be("https://api.trongrid.io/");
    }

    [Theory]
    [InlineData("Nile", "https://nile.trongrid.io/")]
    [InlineData("Shasta", "https://api.shasta.trongrid.io/")]
    public void Testnets_UseTheirOwnFixedHosts_AndRejectMainnetContract(
        string network, string expectedUrl)
    {
        var endpoints = TronNetworkEndpoints.FromConfiguration(
            Config(network, TestnetContract));
        endpoints.BaseUrl.AbsoluteUri.Should().Be(expectedUrl);
        endpoints.Endpoint("walletsolidity/getnowblock").Host
            .Should().Be(new Uri(expectedUrl).Host);
        Action bad = () => TronNetworkEndpoints.FromConfiguration(
            Config(network, MainnetContract));
        bad.Should().Throw<InvalidOperationException>()
            .WithMessage("*Mainnet USDT contract*");
    }

    [Fact]
    public void NetworkEndpoints_RejectAbsoluteAndRootRelativePathOverrides()
    {
        var endpoints = TronNetworkEndpoints.FromConfiguration(Config("Nile", TestnetContract));
        Action absolute = () => endpoints.Endpoint("https://api.trongrid.io/wallet/broadcasttransaction");
        Action rootRelative = () => endpoints.Endpoint("/wallet/broadcasttransaction");
        absolute.Should().Throw<ArgumentException>();
        rootRelative.Should().Throw<ArgumentException>();
    }

    private sealed class CapturingHttpHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":true,\"txid\":\"mock-tx\"}", Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task SweepBroadcast_UsesSelectedNileEndpointNotMainnet()
    {
        var handler = new CapturingHttpHandler();
        var service = new TronSweepService(new HttpClient(handler),
            Config("Nile", TestnetContract), NullLogger<TronSweepService>.Instance);
        var result = await service.BroadcastSignedTransactionAsync("{}", CancellationToken.None);
        result.Should().Be("mock-tx");
        handler.LastUri!.AbsoluteUri.Should().Be(
            "https://nile.trongrid.io/wallet/broadcasttransaction");
    }

    [Fact]
    public async Task ScannerVerification_UsesSelectedShastaEndpointNotMainnet()
    {
        var handler = new CapturingHttpHandler();
        var scanner = new TronScannerService(null!, new HttpClient(handler),
            Config("Shasta", TestnetContract), NullLogger<TronScannerService>.Instance);
        // Invalid mock receipt safely fails while still recording request routing.
        await scanner.VerifySolidifiedTransactionAsync(
            "abcd", "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH", 1, 0, CancellationToken.None);
        handler.LastUri!.AbsoluteUri.Should().Be(
            "https://api.shasta.trongrid.io/walletsolidity/gettransactioninfobyid");
    }
}
