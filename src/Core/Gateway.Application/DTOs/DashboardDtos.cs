using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Gateway.Application.DTOs;

public class DashboardOverviewDto
{
    public int TotalInvoices { get; set; }
    public int PaidInvoices { get; set; }
    public int PendingInvoices { get; set; }
    public int ExpiredOrFailedInvoices { get; set; }
    public decimal TotalReceivedAmount { get; set; }
    public string FiatCurrency { get; set; } = "USD";
    public List<RecentInvoiceDto> RecentInvoices { get; set; } = new();
}

public class RecentInvoiceDto
{
    public Guid Id { get; set; }
    public decimal FiatAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public class DashboardInvoiceSummaryDto
{
    public Guid Id { get; set; }
    public decimal FiatAmount { get; set; }
    public string FiatCurrency { get; set; } = "USD";
    public long ExpectedAmountUnits { get; set; }
    public long PaidAmountUnits { get; set; }
    public string Status { get; set; } = string.Empty;
    public string DepositAddress { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public class DashboardInvoiceDetailsDto
{
    public Guid Id { get; set; }
    public string DepositAddress { get; set; } = string.Empty;
    public decimal FiatAmount { get; set; }
    public string FiatCurrency { get; set; } = "USD";
    public string Currency { get; set; } = "USDT";
    public string Network { get; set; } = "TRON";
    public long ExpectedAmountUnits { get; set; }
    public long PaidAmountUnits { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    public List<PaymentTransferSummaryDto> Transfers { get; set; } = new();
}

public class PaymentTransferSummaryDto
{
    public Guid Id { get; set; }
    public string TransactionHash { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public long AmountUnits { get; set; }
    public ulong BlockNumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime ObservedAtUtc { get; set; }
}

// ✅ این کلاس اضافه شد
public class MerchantProfileDto
{
    public Guid MerchantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<MerchantWalletDto> Wallets { get; set; } = new();
}

public class MerchantWalletDto
{
    public Guid WalletId { get; set; }
    public string Network { get; set; } = string.Empty;
    public uint TotalAddressesAllocated { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class UpdateMerchantProfileRequest
{
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string Name { get; set; } = string.Empty;

    public string? DefaultWebhookUrl { get; set; }
}

public class DashboardInvoiceFilterRequest
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Status { get; set; }
    public DateTime? FromDateUtc { get; set; }
    public DateTime? ToDateUtc { get; set; }
}

public class PagedDashboardResult<T>
{
    public IReadOnlyCollection<T> Items { get; set; } = Array.Empty<T>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
}