using System;
using FluentAssertions;
using Gateway.Domain.Entities;
using Xunit;

namespace Gateway.Domain.Tests;

public class PlanValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Plan_WithInvalidName_ShouldThrowArgumentException(string? invalidName)
    {
        var act = () => new Plan(invalidName!, "desc", 100, 5000m, 0m);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Plan_WithValidName_ShouldSucceed()
    {
        var plan = new Plan("Starter", "desc", 100, 5000m, 0m);
        plan.Name.Should().Be("Starter");
    }

    [Fact]
    public void Plan_UpdateWithInvalidName_ShouldThrowArgumentException()
    {
        var plan = new Plan("Starter", "desc", 100, 5000m, 0m);
        var act = () => plan.Update("   ", "desc", 100, 5000m, 0m);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Plan_WithNegativeVolumeLimit_ShouldThrow()
    {
        var act = () => new Plan("Test", "desc", 100, -1m, 0m);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Plan_WithZeroVolumeLimit_ShouldSucceed_Unlimited()
    {
        var plan = new Plan("Test", "desc", 100, 0m, 0m);
        plan.MonthlyVolumeLimitUsd.Should().Be(0m);
    }
}