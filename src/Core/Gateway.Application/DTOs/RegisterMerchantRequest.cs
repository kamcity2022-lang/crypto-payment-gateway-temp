using System.ComponentModel.DataAnnotations;

namespace Gateway.Application.DTOs;

public class RegisterMerchantRequest
{
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string WebhookUrl { get; set; } = string.Empty;

    [Required]
    public string TronAccountXPub { get; set; } = string.Empty;
}

public class RegisterMerchantResponse
{
    public Guid MerchantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string TronAccountXPub { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

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