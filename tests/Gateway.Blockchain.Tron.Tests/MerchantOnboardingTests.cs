using System;
using FluentAssertions;
using Gateway.Blockchain.Tron;
using NBitcoin;
using Xunit;

namespace Gateway.Domain.Tests;

public class MerchantOnboardingTests
{
    private static string GenerateTestXPub()
    {
        var mnemonic = new Mnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        var masterKey = mnemonic.DeriveExtKey();
        var accountKey = masterKey.Derive(new KeyPath("m/44'/195'/0'"));
        return accountKey.Neuter().ToString(Network.Main);
    }

    [Fact]
    public void ValidXPub_ShouldSuccessfullyDerive_TronAddress()
    {
        var xpub = GenerateTestXPub();
        var service = new TronAddressDerivationService(xpub);
        var address = service.DeriveAddress(0);

        address.Should().StartWith("T");
        address.Length.Should().Be(34);
    }

    [Fact]
    public void InvalidXPub_ShouldThrowFormatException()
    {
        var action = () => new TronAddressDerivationService("invalid-xpub-format");
        action.Should().Throw<FormatException>();
    }
}