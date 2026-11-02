using System;
using FluentAssertions;
using Gateway.Domain.Entities;
using Xunit;

namespace Gateway.Domain.Tests;

public class MerchantClaimTokenTests
{
    [Fact]
    public void MerchantClaimToken_ValidToken_ShouldPassValidation()
    {
        var merchantId = Guid.NewGuid();
        var plainToken = "secure-random-token-12345";
        var token = new MerchantClaimToken(merchantId, plainToken, TimeSpan.FromHours(24));

        token.Validate(plainToken).Should().BeTrue();
        token.IsUsed.Should().BeFalse();
    }

    [Fact]
    public void MerchantClaimToken_WrongToken_ShouldFailValidation()
    {
        var merchantId = Guid.NewGuid();
        var plainToken = "secure-random-token-12345";
        var token = new MerchantClaimToken(merchantId, plainToken, TimeSpan.FromHours(24));

        token.Validate("wrong-token").Should().BeFalse();
    }

    [Fact]
    public void MerchantClaimToken_UsedToken_ShouldFailValidation()
    {
        var merchantId = Guid.NewGuid();
        var plainToken = "secure-random-token-12345";
        var token = new MerchantClaimToken(merchantId, plainToken, TimeSpan.FromHours(24));
        
        token.MarkAsUsed();
        token.Validate(plainToken).Should().BeFalse();
    }

    [Fact]
    public void MerchantClaimToken_ExpiredToken_ShouldFailValidation()
    {
        var merchantId = Guid.NewGuid();
        var plainToken = "expired-token";
        var token = new MerchantClaimToken(merchantId, plainToken, TimeSpan.FromTicks(-1));

        token.Validate(plainToken).Should().BeFalse();
    }
}