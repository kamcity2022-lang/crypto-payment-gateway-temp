using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Gateway.Application.DTOs;

public class PlatformOverviewDto
{
    public int TotalMerchants { get; set; }
    public int ActiveMerchants { get; set; }
    public int InactiveMerchants { get; set; }
    public int TotalWallets { get; set; }
}

public class MerchantSummaryDto
{
    public Guid MerchantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int WalletCount { get; set; }
}

public class PagedMerchantResultDto
{
    public IReadOnlyCollection<MerchantSummaryDto> Items { get; set; } = Array.Empty<MerchantSummaryDto>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
}

public class MerchantDetailsDto
{
    public Guid MerchantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? DefaultWebhookUrl { get; set; }
    public int WalletCount { get; set; }
    public List<MerchantWalletSummaryDto> Wallets { get; set; } = new();
    public int TotalInvoiceCount { get; set; }
}

public class MerchantWalletSummaryDto
{
    public Guid WalletId { get; set; }
    public string Network { get; set; } = string.Empty;
    public uint TotalAddressesAllocated { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class AdminMerchantFilterRequest
{
    public int Page { get; set; } = 1;
    
    // ✅ اصلاح: حذف [Range(1, 100)] برای اجازه دادن به Math.Clamp در کنترلر
    // این کار باعث می‌شود مقادیر خارج از محدوده به جای رد شدن، به محدوده معتبر محدود شوند
    public int PageSize { get; set; } = 20;
    
    public string? Search { get; set; }
    
    public bool? IsActive { get; set; }
}