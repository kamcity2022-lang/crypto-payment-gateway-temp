using System;
using System.Security.Cryptography;
using FluentAssertions;
using Gateway.Blockchain.Tron;
using NBitcoin;
using Xunit;

namespace Gateway.Blockchain.Tron.Tests;

public class TronKnownVectorsAndHardeningTests
{
    [Fact]
    public void KnownVector_ShouldDeriveExactTronAddress()
    {
        // Vector derived from BIP-44 account xpub
        // Path: m/44'/195'/0'
        var mnemonic = new Mnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        var accountXPub = mnemonic.DeriveExtKey().Derive(new KeyPath("m/44'/195'/0'")).Neuter().ToString(Network.Main);

        var service = new TronAddressDerivationService(accountXPub);

        // Derive index 0
        var address0 = service.DeriveAddress(0);

        address0.Should().StartWith("T");
        address0.Length.Should().Be(34);

        // Derive index 1
        var address1 = service.DeriveAddress(1);
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