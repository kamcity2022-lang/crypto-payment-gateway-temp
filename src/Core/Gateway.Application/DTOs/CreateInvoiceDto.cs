using System.ComponentModel.DataAnnotations;

namespace Gateway.Application.DTOs;

public class CreateInvoiceRequest
{
    [Range(0.01, 1000000.00)]
    public decimal FiatAmount { get; set; }

    [Required]
    [StringLength(10)]
    public string FiatCurrency { get; set; } = "USD";

    [Required]
    public string CallbackUrl { get; set; } = string.Empty;

    public int ExpiryMinutes { get; set; } = 30;
}

public class InvoiceResponseDto
{
    public Guid InvoiceId { get; set; }
    public string DepositAddress { get; set; } = string.Empty;
    public decimal FiatAmount { get; set; }
    public string FiatCurrency { get; set; } = "USD";
    public decimal ExchangeRate { get; set; }
    public decimal ExpectedCryptoAmount { get; set; }
    public long ExpectedUnits { get; set; }
    public string Currency { get; set; } = "USDT";
    public string Network { get; set; } = "TRON";
    public string Status { get; set; } = string.Empty;
    public string CheckoutUrl { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class CheckoutStatusDto
{
    public string DepositAddress { get; set; } = string.Empty;
    public decimal ExpectedAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public string Currency { get; set; } = "USDT";
    public string Network { get; set; } = "TRON";
    public string Status { get; set; } = string.Empty;
    public long RemainingSeconds { get; set; }
}