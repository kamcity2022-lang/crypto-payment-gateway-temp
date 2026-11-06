using System;
using System.ComponentModel.DataAnnotations;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;

namespace Gateway.Application.DTOs;

public sealed class AssignSubscriptionRequest
{
    [Required]
    public Guid PlanId { get; set; }
}

public sealed class SubscriptionResponseDto
{
    public Guid SubscriptionId { get; set; }
    public Guid MerchantId { get; set; }
    public Guid PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public SubscriptionStatus Status { get; set; }
    public DateTime CurrentPeriodStartUtc { get; set; }
    public DateTime CurrentPeriodEndUtc { get; set; }
    public int CurrentPeriodInvoiceCount { get; set; }
    public decimal CurrentPeriodVolumeUsd { get; set; }
    public int? CustomInvoiceLimit { get; set; }
    public decimal? CustomVolumeLimitUsd { get; set; }
    public int EffectiveMonthlyInvoiceLimit { get; set; }
    public decimal EffectiveMonthlyVolumeLimitUsd { get; set; }
    public int RemainingInvoices { get; set; }
    // A null remaining-volume value means an unlimited USD volume quota.
    public decimal? RemainingVolumeUsd { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public static SubscriptionResponseDto From(Subscription s, Plan plan)
    {
        var invoiceLimit = s.CustomInvoiceLimit ?? plan.MonthlyInvoiceLimit;
        var volumeLimit = s.CustomVolumeLimitUsd ?? plan.MonthlyVolumeLimitUsd;
        // Reset is performed lazily by invoice creation; display the effective
        // current-period usage without mutating persistence in GET endpoints.
        var currentStart = s.CurrentPeriodStartUtc;
        var currentEnd = s.CurrentPeriodEndUtc;
        var periodEnded = DateTime.UtcNow >= currentEnd;
        if (periodEnded)
        {
            // Mirror ResetPeriodIfExpired without mutating a GET request.
            // Even if no new invoices exist, the reset date must be current.
            var now = DateTime.UtcNow;
            while (now >= currentEnd)
            {
                currentStart = currentEnd;
                currentEnd = currentStart.AddMonths(1);
            }
        }
        var count = periodEnded ? 0 : s.CurrentPeriodInvoiceCount;
        var volume = periodEnded ? 0m : s.CurrentPeriodVolumeUsd;

        return new SubscriptionResponseDto
        {
            SubscriptionId = s.Id,
            MerchantId = s.MerchantId,
            PlanId = s.PlanId,
            PlanName = plan.Name,
            Status = s.Status,
            CurrentPeriodStartUtc = currentStart,
            CurrentPeriodEndUtc = currentEnd,
            CurrentPeriodInvoiceCount = count,
            CurrentPeriodVolumeUsd = volume,
            CustomInvoiceLimit = s.CustomInvoiceLimit,
            CustomVolumeLimitUsd = s.CustomVolumeLimitUsd,
            EffectiveMonthlyInvoiceLimit = invoiceLimit,
            EffectiveMonthlyVolumeLimitUsd = volumeLimit,
            RemainingInvoices = Math.Max(0, invoiceLimit - count),
            RemainingVolumeUsd = volumeLimit == 0 ? null : Math.Max(0m, volumeLimit - volume),
            CreatedAtUtc = s.CreatedAtUtc
        };
    }
}
