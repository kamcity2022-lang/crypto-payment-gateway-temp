using System;
using System.ComponentModel.DataAnnotations;

namespace Gateway.Application.DTOs;

public class UpdateSettlementAddressRequest
{
    [Required]
    [RegularExpression("^T[1-9A-HJ-NP-za-km-z]{33}$", ErrorMessage = "Invalid TRON base58 address.")]
    public string SettlementAddress { get; set; } = string.Empty;
}

public class SweepCandidateDto
{
    public Guid DerivedAddressId { get; set; }
    public uint AddressIndex { get; set; }
    public string Address { get; set; } = string.Empty;
    public decimal BalanceUsdt { get; set; }
    public long BalanceUnits { get; set; }
}

public class CreateSweepTaskRequest
{
    [Required]
    public Guid DerivedAddressId { get; set; }
}

public class SweepTaskDto
{
    public Guid SweepTaskId { get; set; }
    public string SourceAddress { get; set; } = string.Empty;
    public string DestinationAddress { get; set; } = string.Empty;
    public decimal AmountUsdt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? SweepTxHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class PrepareSweepTransactionRequest
{
    [Required]
    public Guid SweepTaskId { get; set; }
}

public class PrepareSweepTransactionResponse
{
    public Guid SweepTaskId { get; set; }
    public string RawTransactionJson { get; set; } = string.Empty;
    public string TxHash { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class BroadcastSweepRequest
{
    [Required]
    public Guid SweepTaskId { get; set; }

    [Required]
    public string SignedTransactionJson { get; set; } = string.Empty;
}
