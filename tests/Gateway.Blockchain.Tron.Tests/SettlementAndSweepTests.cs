using System;
using FluentAssertions;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Xunit;

namespace Gateway.Domain.Tests;

public class SettlementAndSweepTests
{
    [Fact]
    public void SetSettlementAddress_ValidTronAddress_ShouldUpdateSuccessfully()
    {
        var merchant = new Merchant(Guid.NewGuid(), "Store", "hash", "sec", "whsec");
        // یک آدرس ۳۴ کاراکتری استاندارد شبکه ترون با Base58 کاملاً معتبر
        var validTronAddress = "TNDzfKLv2DuYxK29iugQyLf3jGg8F7m5p2";

        merchant.SetSettlementAddress(validTronAddress);

        merchant.SettlementAddress.Should().Be(validTronAddress);
    }

    [Theory]
    [InlineData("0x1234567890abcdef1234567890abcdef12345678")] // EVM address
    [InlineData("TShortAddress")]
    [InlineData("")]
    public void SetSettlementAddress_InvalidAddress_ShouldThrowArgumentException(string invalidAddress)
    {
        var merchant = new Merchant(Guid.NewGuid(), "Store", "hash", "sec", "whsec");

        var act = () => merchant.SetSettlementAddress(invalidAddress);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SweepTask_LifecycleTransitions_ShouldWorkCorrectly()
    {
        var sweepTask = new SweepTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "TSourceAddress1234567890qwertyuiop",
            "TDestinationAddress1234567890qwerty",
            "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t",
            50_000_000L);

        sweepTask.Status.Should().Be(SweepStatus.PendingEvaluation);

        sweepTask.MarkFundingRequired();
        sweepTask.Status.Should().Be(SweepStatus.FundingRequired);

        sweepTask.MarkReadyToSign("fundingTx123");
        sweepTask.Status.Should().Be(SweepStatus.ReadyToSign);
        sweepTask.FundingTxHash.Should().Be("fundingTx123");

        sweepTask.MarkBroadcasting("sweepTx999");
        sweepTask.Status.Should().Be(SweepStatus.Broadcasting);
        sweepTask.SweepTxHash.Should().Be("sweepTx999");

        sweepTask.MarkCompleted();
        sweepTask.Status.Should().Be(SweepStatus.Completed);
        sweepTask.CompletedAtUtc.Should().NotBeNull();
    }
}