using System;
using System.Collections.Generic;
using System.Linq;
using Gateway.Domain.Enums;

namespace Gateway.Domain.Entities;

public class Invoice
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid DerivedAddressId { get; private set; }
    public string DepositAddress { get; private set; } = string.Empty;
    public string Currency { get; private set; } = "USDT";
    public string Network { get; private set; } = "TRON";

    public decimal FiatAmount { get; private set; }
    public string FiatCurrency { get; private set; } = "USD";
    public decimal ExchangeRate { get; private set; }
    public string ExchangeRateSource { get; private set; } = string.Empty;
    public DateTime ExchangeRateTimestampUtc { get; private set; }

    public long ExpectedAmountUnits { get; private set; }
    public long PaidAmountUnits { get; private set; }
    public long UnderpaymentThresholdUnits { get; private set; }

    public InvoiceStatus Status { get; private set; }
    public string CallbackUrl { get; private set; } = string.Empty;
    public string CheckoutToken { get; private set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? PaidAtUtc { get; private set; }

    private readonly List<PaymentTransfer> _transfers = new();
    public IReadOnlyCollection<PaymentTransfer> Transfers => _transfers.AsReadOnly();

    private Invoice() { }

    public Invoice(
        Guid merchantId,
        Guid derivedAddressId,
        string depositAddress,
        decimal fiatAmount,
        string fiatCurrency,
        decimal exchangeRate,
        string exchangeRateSource,
        DateTime exchangeRateTimestampUtc,
        long expectedAmountUnits,
        long underpaymentThresholdUnits,
        string callbackUrl,
        string checkoutToken,
        TimeSpan validityDuration)
    {
        if (fiatAmount <= 0)
            throw new ArgumentOutOfRangeException(nameof(fiatAmount), "Fiat amount must be positive.");
        if (expectedAmountUnits <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedAmountUnits), "Expected crypto units must be positive.");

        Id = Guid.NewGuid();
        MerchantId = merchantId;
        DerivedAddressId = derivedAddressId;
        DepositAddress = depositAddress;
        FiatAmount = fiatAmount;
        FiatCurrency = fiatCurrency;
        ExchangeRate = exchangeRate;
        ExchangeRateSource = exchangeRateSource;
        ExchangeRateTimestampUtc = exchangeRateTimestampUtc;
        ExpectedAmountUnits = expectedAmountUnits;
        UnderpaymentThresholdUnits = underpaymentThresholdUnits;
        PaidAmountUnits = 0;
        Status = InvoiceStatus.Pending;
        CallbackUrl = callbackUrl;
        CheckoutToken = checkoutToken;
        CreatedAtUtc = DateTime.UtcNow;
        ExpiresAtUtc = CreatedAtUtc.Add(validityDuration);
    }

    public bool RegisterTransfer(PaymentTransfer transfer)
    {
        if (transfer == null) throw new ArgumentNullException(nameof(transfer));

        // Idempotency: Ignore duplicate transfers matching (Network, TxHash, TransferIndex)
        if (_transfers.Any(t => t.Network == transfer.Network &&
                                t.TransactionHash.Equals(transfer.TransactionHash, StringComparison.OrdinalIgnoreCase) &&
                                t.TransferIndex == transfer.TransferIndex))
        {
            return false;
        }

        _transfers.Add(transfer);
        EvaluateStatus();
        return true;
    }

    public InvoiceStatus EvaluateStatus()
    {
        var confirmedUnits = _transfers
            .Where(t => t.Status == PaymentStatus.Confirmed)
            .Sum(t => t.AmountUnits);

        PaidAmountUnits = confirmedUnits;
        var now = DateTime.UtcNow;

        // If invoice is already completed/cancelled, do not revert
        if (Status == InvoiceStatus.Cancelled)
            return Status;

        if (PaidAmountUnits == 0)
        {
            if (now > ExpiresAtUtc && Status == InvoiceStatus.Pending)
            {
                Status = InvoiceStatus.Expired;
            }
            return Status;
        }

        var effectiveExpected = ExpectedAmountUnits - UnderpaymentThresholdUnits;

        if (PaidAmountUnits >= ExpectedAmountUnits)
        {
            Status = PaidAmountUnits > ExpectedAmountUnits ? InvoiceStatus.Overpaid : InvoiceStatus.Paid;
            PaidAtUtc ??= DateTime.UtcNow;
        }
        else if (PaidAmountUnits >= effectiveExpected)
        {
            Status = InvoiceStatus.Paid;
            PaidAtUtc ??= DateTime.UtcNow;
        }
        else
        {
            Status = now > ExpiresAtUtc ? InvoiceStatus.Underpaid : InvoiceStatus.PartiallyPaid;
        }

        return Status;
    }

    public void MarkAsExpiredIfPastDue()
    {
        if (Status == InvoiceStatus.Pending && DateTime.UtcNow > ExpiresAtUtc && PaidAmountUnits == 0)
        {
            Status = InvoiceStatus.Expired;
        }
    }
}