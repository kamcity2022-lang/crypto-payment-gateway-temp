using System;
using FluentAssertions;
using Gateway.Blockchain.Tron;
using NBitcoin;
using Xunit;

namespace Gateway.Blockchain.Tron.Tests;

public class TronKnownVectorsAndHardeningTests
{
    // Independently verified using standard BIP39 (empty passphrase) + BIP44 m/44'/195'/0'/0/{index} + Keccak256 + Base58Check
    private const string ExpectedAddress0 = "TFYjgZoNQ1cm1VZCtcP9Ygkk3kAuY49ALA";
    private const string ExpectedAddress1 = "TSkxBDeh4qdXxuYfDfVRtB7Gw71YDQvFt4";

    [Fact]
    public void KnownVector_ShouldDeriveExactTronAddress()
    {
        var mnemonic = new Mnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        var xpub = mnemonic.DeriveExtKey().Derive(new KeyPath("m/44'/195'/0'")).Neuter().ToString(Network.Main);
        var service = new TronAddressDerivationService(xpub);

        var address0 = service.DeriveAddress(0);
        address0.Should().NotBeNullOrWhiteSpace();
        address0.Should().StartWith("T");
        address0.Length.Should().Be(34);
        address0.Should().Be(ExpectedAddress0);

        var address1 = service.DeriveAddress(1);
        address1.Should().NotBeNullOrWhiteSpace();
        address1.Should().StartWith("T");
        address1.Length.Should().Be(34);
        address1.Should().Be(ExpectedAddress1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-xpub")]
    [InlineData("xpub661MyMwAqRbcFtXgS5sYJABqqG9YLmC4Q1Rdap9gSE8NqtwybGhePY2gZ29ESFEhCu7zmCHJp45654Px5T6h2meZHe9LgigUMNn5GQ83ahD_bad")]
    public void Derivation_InvalidXPub_ShouldFail(string invalidXPub)
    {
        var act = () => new TronAddressDerivationService(invalidXPub);
        act.Should().Throw<Exception>();
    }
}