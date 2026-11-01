using FluentAssertions;
using NBitcoin;
using Xunit;

namespace Gateway.Blockchain.Tron.Tests;

public class TronAddressDerivationTests
{
    private static string GenerateValidTronAccountXPub()
    {
        // استفاده از سید استاندارد BIP-39 صرفاً برای ایجاد یک xpub واقعی و معتبر در سطح حافظه تست
        var mnemonic = new Mnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        var masterKey = mnemonic.DeriveExtKey();

        // مسیر حساب ترون طبق BIP-44: m / 44' / 195' / 0'
        var accountKey = masterKey.Derive(new KeyPath("m/44'/195'/0'"));
        
        // استخراج xPub بدون Private Key
        return accountKey.Neuter().ToString(Network.Main);
    }

    [Fact]
    public void DeriveAddress_ShouldGenerateValidTronAddresses_StartingWithT()
    {
        var validAccountXPub = GenerateValidTronAccountXPub();
        var service = new TronAddressDerivationService(validAccountXPub);

        for (uint i = 0; i < 20; i++)
        {
            var address = service.DeriveAddress(i);

            address.Should().NotBeNullOrWhiteSpace();
            address.Should().StartWith("T");
            address.Length.Should().Be(34);
        }
    }

    [Fact]
    public void DeriveAddress_ShouldBeDeterministic_AcrossMultipleInstances()
    {
        var validAccountXPub = GenerateValidTronAccountXPub();
        var service1 = new TronAddressDerivationService(validAccountXPub);
        var service2 = new TronAddressDerivationService(validAccountXPub);

        for (uint i = 0; i < 10; i++)
        {
            service1.DeriveAddress(i).Should().Be(service2.DeriveAddress(i));
        }
    }
}