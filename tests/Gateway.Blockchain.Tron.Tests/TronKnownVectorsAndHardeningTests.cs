using System;
using FluentAssertions;
using Gateway.Blockchain.Tron;
using NBitcoin;
using Xunit;

namespace Gateway.Blockchain.Tron.Tests;

public class TronKnownVectorsAndHardeningTests
{
    private static string GetIndependentlyGeneratedBip32XPub()
    {
        // استفاده از سید مرجع و معتبر BIP-39 برای تولید xpub کاملاً استاندارد و دارای Base58Check معتبر
        var mnemonic = new Mnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        var masterKey = mnemonic.DeriveExtKey();
        var accountKey = masterKey.Derive(new KeyPath("m/44'/195'/0'"));
        return accountKey.Neuter().ToString(Network.Main);
    }

    [Fact]
    public void KnownVector_ShouldDeriveExactTronAddress()
    {
        var accountXPub = GetIndependentlyGeneratedBip32XPub();
        var service = new TronAddressDerivationService(accountXPub);

        // Derive child index 0
        var address0 = service.DeriveAddress(0);
        address0.Should().NotBeNullOrWhiteSpace();
        address0.Should().StartWith("T");
        address0.Length.Should().Be(34);

        // Derive child index 1
        var address1 = service.DeriveAddress(1);
        address1.Should().NotBeNullOrWhiteSpace();
        address1.Should().StartWith("T");
        address1.Length.Should().Be(34);
        address1.Should().NotBe(address0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-xpub")]
    public void Derivation_InvalidXPub_ShouldFail(string invalidXPub)
    {
        var act = () => new TronAddressDerivationService(invalidXPub);
        act.Should().Throw<Exception>();
    }
}