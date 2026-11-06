using System;
using Gateway.Domain.Enums;

namespace Gateway.Domain.Entities;

public class Subscription
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid PlanId { get; private set; }
    public SubscriptionStatus Status { get; private set; }
    public DateTime CurrentPeriodStartUtc { get; private set; }
    public DateTime CurrentPeriodEndUtc { get; private set; }
    public int CurrentPeriodInvoiceCount { get; private set; }
    public decimal CurrentPeriodVolumeUsd { get; private set; }
    
    public int? CustomInvoiceLimit { get; private set; }
    public decimal? CustomVolumeLimitUsd { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public virtual Merchant Merchant { get; private set; } = null!;
    public virtual Plan Plan { get; private set; } = null!;

    private Subscription() { }

    public Subscription(Guid merchantId, Guid planId)
    {
        Id = Guid.NewGuid();
        MerchantId = merchantId;
        PlanId = planId;
        Status = SubscriptionStatus.Active;
        CurrentPeriodStartUtc = DateTime.UtcNow;
        CurrentPeriodEndUtc = DateTime.UtcNow.AddMonths(1);
        CurrentPeriodInvoiceCount = 0;
        CurrentPeriodVolumeUsd = 0m;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public void ResetPeriodIfExpired()
    {
        var now = DateTime.UtcNow;
        if (now < CurrentPeriodEndUtc)
            return;

        // Advance through ALL elapsed billing periods
        while (now >= CurrentPeriodEndUtc)
        {
            CurrentPeriodStartUtc = CurrentPeriodEndUtc;
            CurrentPeriodEndUtc = CurrentPeriodStartUtc.AddMonths(1);
        }
        CurrentPeriodInvoiceCount = 0;
        CurrentPeriodVolumeUsd = 0m;
    }

    public void RecordInvoiceCreation(decimal fiatAmountUsd)
    {
        if (fiatAmountUsd <= 0)
            throw new ArgumentOutOfRangeException(nameof(fiatAmountUsd), "Fiat amount must be positive.");

        CurrentPeriodInvoiceCount++;
        CurrentPeriodVolumeUsd += fiatAmountUsd;
    }

    public void ChangePlan(Guid planId)
    {
        if (planId == Guid.Empty)
            throw new ArgumentException("Plan ID cannot be empty.", nameof(planId));
        if (Status != SubscriptionStatus.Active)
            throw new InvalidOperationException("Only active subscriptions can change plans.");

        PlanId = planId;
        // The current billing period, consumed quota and overrides are retained.
    }

    public void Cancel()
    {
        if (Status != SubscriptionStatus.Active)
            throw new InvalidOperationException("Only active subscriptions can be cancelled.");

        Status = SubscriptionStatus.Cancelled;
    }

    public void OverrideLimits(int? customInvoiceLimit, decimal? customVolumeLimitUsd)
    {
        if (customInvoiceLimit.HasValue && customInvoiceLimit.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(customInvoiceLimit), "Custom invoice limit cannot be negative.");
        if (customVolumeLimitUsd.HasValue && customVolumeLimitUsd.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(customVolumeLimitUsd), "Custom volume limit cannot be negative.");

        CustomInvoiceLimit = customInvoiceLimit;
        CustomVolumeLimitUsd = customVolumeLimitUsd;
    }
}