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

public class UpdateMerchantProfileRequest
{
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string Name { get; set; } = string.Empty;

    public string? DefaultWebhookUrl { get; set; }
}