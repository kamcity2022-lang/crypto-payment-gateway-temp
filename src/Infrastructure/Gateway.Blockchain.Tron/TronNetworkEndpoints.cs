using System;
using Microsoft.Extensions.Configuration;

namespace Gateway.Blockchain.Tron;

/// <summary>
/// Named, fixed TronGrid endpoints. No arbitrary URL configuration is accepted
/// for chain requests, to prevent routing the payment worker to an unexpected
/// network or private host. Select a network explicitly in every process.
/// </summary>
public sealed class TronNetworkEndpoints
{
    public const string MainnetUsdtContract = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";

    public string Network { get; }
    public string TokenContract { get; }
    public Uri BaseUrl { get; }

    private TronNetworkEndpoints(string network, string tokenContract, Uri baseUrl)
    {
        Network = network;
        TokenContract = tokenContract;
        BaseUrl = baseUrl;
    }

    public static TronNetworkEndpoints FromConfiguration(IConfiguration config)
    {
        var name = config["Tron:Network"]?.Trim();
        string baseUrl;
        string normalized;
        switch (name?.ToLowerInvariant())
        {
            case "mainnet":
                normalized = "Mainnet";
                baseUrl = "https://api.trongrid.io/";
                if (!config.GetValue<bool>("Tron:EnableMainnet"))
                    throw new InvalidOperationException(
                        "Mainnet blocked: set Tron:EnableMainnet=true explicitly before using live TRON.");
                break;
            case "nile":
                normalized = "Nile";
                baseUrl = "https://nile.trongrid.io/";
                break;
            case "shasta":
                normalized = "Shasta";
                baseUrl = "https://api.shasta.trongrid.io/";
                break;
            default:
                throw new InvalidOperationException(
                    "Tron:Network is required and must be Mainnet, Nile or Shasta.");
        }

        var contract = config["Tron:UsdtContractAddress"]?.Trim();
        if (string.IsNullOrWhiteSpace(contract) ||
            contract.Length != 34 || !contract.StartsWith('T'))
            throw new InvalidOperationException(
                "Tron:UsdtContractAddress must be an explicit 34-character TRON contract address.");

        if (normalized == "Mainnet" &&
            !string.Equals(contract, MainnetUsdtContract, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Mainnet requires the reviewed canonical USDT TRC-20 contract.");

        if (normalized != "Mainnet" &&
            string.Equals(contract, MainnetUsdtContract, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Testnet must not use the Mainnet USDT contract address.");

        return new TronNetworkEndpoints(normalized, contract, new Uri(baseUrl));
    }

    public Uri Endpoint(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.StartsWith('/') || relativePath.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Only relative TronGrid API paths are allowed.", nameof(relativePath));
        return new Uri(BaseUrl, relativePath);
    }
}
