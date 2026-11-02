using System;
using FluentAssertions;
using Gateway.Blockchain.Tron;
using NBitcoin;
using Xunit;

namespace Gateway.Blockchain.Tron.Tests;

public class TronKnownVectorsAndHardeningTests
{
    // Index 0: Independently verified via LedgerHQ/app-tron official test vectors
    // for BIP39 "abandon...about" (empty passphrase) + BIP44 m/44'/195'/0'/0/0
    private const string ExpectedAddress0 = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";

    // LIMITATION: Index 1 value was derived from implementation output in a previous
    // test run. Independent external verification (e.g., via TronLink or iancoleman.io)
    // is required before this value can be considered cryptographically proven.
    // Full vector coverage is NOT claimed until external verification is performed.
    private const string ExpectedAddress1 = "TSeJkUh4Qv67VNFwY8LaAxERygNdy6NQZK";

    [Fact]
    public void KnownVector_Index0_ShouldMatchLedgerHQ()
    {
        var mnemonic = new Mnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        var xpub = mnemonic.DeriveExtKey().Derive(new KeyPath("m/44'/195'/0'")).Neuter().ToString(Network.Main);
        var service = new TronAddressDerivationService(xpub);

        var address0 = service.DeriveAddress(0);
        address0.Should().NotBeNullOrWhiteSpace();
        address0.Should().StartWith("T");
        address0.Length.Should().Be(34);
        address0.Should().Be(ExpectedAddress0);
    }

    [Fact]
    public void KnownVector_Index1_ShouldBeWellFormedAndDeterministic()
    {
        var mnemonic = new Mnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        var xpub = mnemonic.DeriveExtKey().Derive(new KeyPath("m/44'/195'/0'")).Neuter().ToString(Network.Main);
        var service = new TronAddressDerivationService(xpub);

        var address1 = service.DeriveAddress(1);
        address1.Should().NotBeNullOrWhiteSpace();
        address1.Should().StartWith("T");
        address1.Length.Should().Be(34);
        
        // Note: This assertion uses an implementation-derived value.
        // It proves determinism but NOT correctness against an external wallet.
        // External verification is pending.
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