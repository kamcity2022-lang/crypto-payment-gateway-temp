using System;
using Gateway.Domain.Enums;

namespace Gateway.Domain.Entities;

public class SweepTask
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid DerivedAddressId { get; private set; }
    public string SourceAddress { get; private set; } = string.Empty;
    public string DestinationAddress { get; private set; } = string.Empty;
    public string TokenContractAddress { get; private set; } = string.Empty;
    public long AmountUnits { get; private set; }
    public SweepStatus Status { get; private set; }
    public string? FundingTxHash { get; private set; }
    public string? SweepTxHash { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    private SweepTask() { }

    public SweepTask(
        Guid merchantId,
        Guid derivedAddressId,
        string sourceAddress,
        string destinationAddress,
        string tokenContractAddress,
        long amountUnits)
    {
        if (amountUnits <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountUnits), "Sweep amount must be greater than zero.");

        Id = Guid.NewGuid();
        MerchantId = merchantId;
        DerivedAddressId = derivedAddressId;
        SourceAddress = sourceAddress;
        DestinationAddress = destinationAddress;
        TokenContractAddress = tokenContractAddress;
        AmountUnits = amountUnits;
        Status = SweepStatus.PendingEvaluation;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public void MarkFundingRequired()
    {
        Status = SweepStatus.FundingRequired;
    }

    public void MarkReadyToSign(string? fundingTxHash = null)
    {
        Status = SweepStatus.ReadyToSign;
        FundingTxHash = fundingTxHash;
    }

    public void MarkBroadcasting(string sweepTxHash)
    {
        Status = SweepStatus.Broadcasting;
        SweepTxHash = sweepTxHash;
    }

    public void MarkCompleted()
    {
        Status = SweepStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
        ErrorMessage = null;
    }

    public void MarkFailed(string error)
    {
        Status = SweepStatus.Failed;
        ErrorMessage = error;
    }
}