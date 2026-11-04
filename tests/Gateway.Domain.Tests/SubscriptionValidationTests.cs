using System;
using System.Reflection;
using FluentAssertions;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Xunit;

namespace Gateway.Domain.Tests;

public class SubscriptionValidationTests
{
    [Fact]
    public void Subscription_RecordUsage_WithNegativeInvoiceIncrement_ShouldThrow()
    {
        var sub = new Subscription(Guid.NewGuid(), Guid.NewGuid());
        var act = () => sub.RecordUsage(-1, 0m);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Subscription_RecordUsage_WithNegativeVolumeIncrement_ShouldThrow()
    {
        var sub = new Subscription(Guid.NewGuid(), Guid.NewGuid());
        var act = () => sub.RecordUsage(0, -1m);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Subscription_OverrideLimits_WithNegativeInvoiceLimit_ShouldThrow()
    {
        var sub = new Subscription(Guid.NewGuid(), Guid.NewGuid());
        var act = () => sub.OverrideLimits(-1, null);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Subscription_OverrideLimits_WithNegativeVolumeLimit_ShouldThrow()
    {
        var sub = new Subscription(Guid.NewGuid(), Guid.NewGuid());
        var act = () => sub.OverrideLimits(null, -1m);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Subscription_OverrideLimits_WithNullValues_ShouldSucceed()
    {
        var sub = new Subscription(Guid.NewGuid(), Guid.NewGuid());
        sub.OverrideLimits(null, null);
        sub.CustomInvoiceLimit.Should().BeNull();
        sub.CustomVolumeLimitUsd.Should().BeNull();
    }

    [Fact]
    public void Subscription_ResetPeriodIfExpired_WithMultipleElapsedPeriods_ShouldAdvanceAll()
    {
        var sub = new Subscription(Guid.NewGuid(), Guid.NewGuid());
        
        // Simulate 3 months have passed using reflection
        var threeMonthsAgo = DateTime.UtcNow.AddMonths(-3);
        var startProp = typeof(Subscription).GetProperty(nameof(Subscription.CurrentPeriodStartUtc))!;
        var endProp = typeof(Subscription).GetProperty(nameof(Subscription.CurrentPeriodEndUtc))!;
        startProp.SetValue(sub, threeMonthsAgo);
        endProp.SetValue(sub, threeMonthsAgo.AddMonths(1));
        
        // ✅ استفاده از Reflection برای تغییر مقادیر read-only
        var countProp = typeof(Subscription).GetProperty(nameof(Subscription.CurrentPeriodInvoiceCount))!;
        var volumeProp = typeof(Subscription).GetProperty(nameof(Subscription.CurrentPeriodVolumeUsd))!;
        countProp.SetValue(sub, 50);
        volumeProp.SetValue(sub, 1000m);
        
        sub.ResetPeriodIfExpired();
        
        sub.CurrentPeriodInvoiceCount.Should().Be(0);
        sub.CurrentPeriodVolumeUsd.Should().Be(0m);
        sub.CurrentPeriodEndUtc.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public void Subscription_ResetPeriodIfExpired_WithNoElapsedPeriod_ShouldNotChange()
    {
        var sub = new Subscription(Guid.NewGuid(), Guid.NewGuid());
        var originalEnd = sub.CurrentPeriodEndUtc;
        
        // ✅ استفاده از Reflection برای تغییر مقدار read-only
        var countProp = typeof(Subscription).GetProperty(nameof(Subscription.CurrentPeriodInvoiceCount))!;
        countProp.SetValue(sub, 50);
        
        sub.ResetPeriodIfExpired();
        
        sub.CurrentPeriodEndUtc.Should().Be(originalEnd);
        sub.CurrentPeriodInvoiceCount.Should().Be(50);
    }

    [Fact]
    public void Subscription_InitialState_ShouldBeActive()
    {
        var sub = new Subscription(Guid.NewGuid(), Guid.NewGuid());
        sub.Status.Should().Be(SubscriptionStatus.Active);
        sub.CurrentPeriodInvoiceCount.Should().Be(0);
        sub.CurrentPeriodVolumeUsd.Should().Be(0m);
    }
}