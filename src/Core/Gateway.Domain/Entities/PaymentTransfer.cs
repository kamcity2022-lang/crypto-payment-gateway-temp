using System;
using Gateway.Domain.Enums;

namespace Gateway.Domain.Entities;

public class PaymentTransfer
{
    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public string Network { get; private set; } = string.Empty;
    public string TransactionHash { get; private set; } = string.Empty;
    public int TransferIndex { get; private set; }
    public string TokenContractAddress { get; private set; } = string.Empty;
    public string FromAddress { get; private set; } = string.Empty;
    public string ToAddress { get; private set; } = string.Empty;
    public long AmountUnits { get; private set; } // مقدار دقیق آن‌چین (واحد Sun: 1 USDT = 1,000,000 Sun)
    public ulong BlockNumber { get; private set; }
    public ulong Confirmations { get; private set; }
    public PaymentStatus Status { get; private set; }
    public DateTime ObservedAtUtc { get; private set; }
    public DateTime? ConfirmedAtUtc { get; private set; }

    private PaymentTransfer() { }

    public PaymentTransfer(
        Guid invoiceId,
        string network,
        string transactionHash,
        int transferIndex,
        string tokenContractAddress,
        string fromAddress,
        string toAddress,
        long amountUnits,
        ulong blockNumber)
    {
        Id = Guid.NewGuid();
        InvoiceId = invoiceId;
        Network = network;
        TransactionHash = transactionHash;
        TransferIndex = transferIndex;
        TokenContractAddress = tokenContractAddress;
        FromAddress = fromAddress;
        ToAddress = toAddress;
        AmountUnits = amountUnits;
        BlockNumber = blockNumber;
        Status = PaymentStatus.Observed;
        ObservedAtUtc = DateTime.UtcNow;
    }

    public void UpdateConfirmations(ulong currentBlockNumber, ulong requiredConfirmations)
    {
        if (currentBlockNumber >= BlockNumber)
        {
            Confirmations = (currentBlockNumber - BlockNumber) + 1;
            if (Confirmations >= requiredConfirmations && Status != PaymentStatus.Confirmed)
            {
                Status = PaymentStatus.Confirmed;
                ConfirmedAtUtc = DateTime.UtcNow;
            }
            else if (Status == PaymentStatus.Observed)
            {
                Status = PaymentStatus.Confirming;
            }
        }
    }

    public void MarkAsFailed()
    {
        Status = PaymentStatus.Failed;
    }

    public void MarkAsIgnored()
    {
        Status = PaymentStatus.Ignored;
    }
}