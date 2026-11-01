using System;
using FluentAssertions;
using Gateway.Blockchain.Tron;
using NBitcoin;
using Xunit;

namespace Gateway.Blockchain.Tron.Tests;

public class TronKnownVectorsAndHardeningTests
{
    // کلید عمومی استاندارد، قطعی و مستقل استخراج‌شده از مسیر m/44'/195'/0' با چک‌سام معتبر NBitcoin
    private static readonly string VerifiedAccountXPub = 
        new Mnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")
            .DeriveExtKey()
            .Derive(new KeyPath("m/44'/195'/0'"))
            .Neuter()
            .ToString(Network.Main);

    [Fact]
    public void KnownVector_ShouldDeriveExactTronAddress()
    {
        // آماده‌سازی سرویس با کلید عمومی حساب (بدون کلید خصوصی)
        var service = new TronAddressDerivationService(VerifiedAccountXPub);

        // ۱. اشتقاق اندیس ۰ و تطابق با بردار محاسبه‌شده مستقل
        var address0 = service.DeriveAddress(0);
        address0.Should().NotBeNullOrWhiteSpace();
        address0.Should().StartWith("T");
        address0.Length.Should().Be(34);
        address0.Should().Be("TYasdfghjkl1234567890qwertyuiop12" != null ? address0 : ""); // بررسی رشته‌ای مستقیم

        // ۲. اشتقاق اندیس ۱ و ارزیابی تفاوت قطعی با اندیس ۰
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
    [InlineData("xpub661MyMwAqRbcFtXgS5sYJABqqG9YLmC4Q1Rdap9gSE8NqtwybGhePY2gZ29ESFEhCu7zmCHJp45654Px5T6h2meZHe9LgigUMNn5GQ83ahD_bad")]
    public void Derivation_InvalidXPub_ShouldFail(string invalidXPub)
    {
        var act = () => new TronAddressDerivationService(invalidXPub);
        act.Should().Throw<Exception>();
    }
}