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

    // مبالغ تجاری و فیات
    public decimal FiatAmount { get; private set; }
    public string FiatCurrency { get; private set; } = "USD";
    public decimal ExchangeRate { get; private set; } // نرخ اسنپ‌شات‌شده در لحظه ایجاد

    // محاسبات درون‌زنجیره‌ای با دقت صحیح (Sun: 10^6)
    public long ExpectedAmountUnits { get; private set; }
    public long PaidAmountUnits { get; private set; }
    public long UnderpaymentThresholdUnits { get; private set; } // آستانه مجاز کسری جزئی

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
        ExpectedAmountUnits = expectedAmountUnits;
        UnderpaymentThresholdUnits = underpaymentThresholdUnits;
        PaidAmountUnits = 0;
        Status = InvoiceStatus.Pending;
        CallbackUrl = callbackUrl;
        CheckoutToken = checkoutToken;
        CreatedAtUtc = DateTime.UtcNow;
        ExpiresAtUtc = CreatedAtUtc.Add(validityDuration);
    }

    public void RegisterTransfer(PaymentTransfer transfer)
    {
        if (transfer == null) throw new ArgumentNullException(nameof(transfer));

        // جلوگیری از تکرار رویداد ترانسفر
        if (_transfers.Any(t => t.Network == transfer.Network &&
                                t.TransactionHash.Equals(transfer.TransactionHash, StringComparison.OrdinalIgnoreCase) &&
                                t.TransferIndex == transfer.TransferIndex))
        {
            return;
        }

        _transfers.Add(transfer);
        EvaluateStatus();
    }

    public void EvaluateStatus()
    {
        // جمع کل ترانسفرهای تایید شده
        var confirmedUnits = _transfers
            .Where(t => t.Status == PaymentStatus.Confirmed)
            .Sum(t => t.AmountUnits);

        PaidAmountUnits = confirmedUnits;

        var now = DateTime.UtcNow;

        if (PaidAmountUnits == 0)
        {
            if (now > ExpiresAtUtc && Status == InvoiceStatus.Pending)
            {
                Status = InvoiceStatus.Expired;
            }
            return;
        }

        var effectiveExpected = ExpectedAmountUnits - UnderpaymentThresholdUnits;

        if (PaidAmountUnits >= ExpectedAmountUnits)
        {
            Status = PaidAmountUnits > ExpectedAmountUnits ? InvoiceStatus.Overpaid : InvoiceStatus.Paid;
            PaidAtUtc ??= DateTime.UtcNow;
        }
        else if (PaidAmountUnits >= effectiveExpected)
        {
            // واریزی در محدوده آستانه پذیرش کسری
            Status = InvoiceStatus.Paid;
            PaidAtUtc ??= DateTime.UtcNow;
        }
        else
        {
            Status = now > ExpiresAtUtc ? InvoiceStatus.Underpaid : InvoiceStatus.PartiallyPaid;
        }
    }

    public void Cancel()
    {
        if (Status == InvoiceStatus.Paid || Status == InvoiceStatus.Overpaid)
            throw new InvalidOperationException("Cannot cancel an already paid invoice.");

        Status = InvoiceStatus.Cancelled;
    }
}