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

    public void RecordUsage(int invoiceCountIncrement, decimal volumeIncrementUsd)
    {
        if (invoiceCountIncrement < 0)
            throw new ArgumentOutOfRangeException(nameof(invoiceCountIncrement), "Invoice count increment cannot be negative.");
        if (volumeIncrementUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(volumeIncrementUsd), "Volume increment cannot be negative.");
        if (CurrentPeriodInvoiceCount + invoiceCountIncrement < 0)
            throw new InvalidOperationException("Resulting invoice count would be invalid.");
        if (CurrentPeriodVolumeUsd + volumeIncrementUsd < 0)
            throw new InvalidOperationException("Resulting volume would be invalid.");

        CurrentPeriodInvoiceCount += invoiceCountIncrement;
        CurrentPeriodVolumeUsd += volumeIncrementUsd;
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