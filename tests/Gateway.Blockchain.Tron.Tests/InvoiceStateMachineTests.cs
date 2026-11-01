using System;
using FluentAssertions;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Xunit;

namespace Gateway.Domain.Tests;

public class InvoiceStateMachineTests
{
    private static Invoice CreateSampleInvoice(long expectedUnits = 10_000_000, long underpaymentThreshold = 50_000)
    {
        return new Invoice(
            merchantId: Guid.NewGuid(),
            derivedAddressId: Guid.NewGuid(),
            depositAddress: "TYasdfghjkl1234567890qwertyuiop12",
            fiatAmount: 10.0m,
            fiatCurrency: "USD",
            exchangeRate: 1.0m,
            exchangeRateSource: "DirectPegProvider",
            exchangeRateTimestampUtc: DateTime.UtcNow,
            expectedAmountUnits: expectedUnits, // 10 USDT
            underpaymentThresholdUnits: underpaymentThreshold,
            callbackUrl: "https://example.com/callback",
            checkoutToken: "secure-token-123",
            validityDuration: TimeSpan.FromMinutes(20)
        );
    }

    [Fact]
    public void RegisterTransfer_ExactAmountConfirmed_ShouldSetStatusToPaid()
    {
        var invoice = CreateSampleInvoice();
        var transfer = new PaymentTransfer(
            invoice.Id, "TRON", "0xabc123", 0, "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t",
            "TFromAddress123", invoice.DepositAddress, 10_000_000, 100);

        transfer.UpdateConfirmations(120, 19);
        invoice.RegisterTransfer(transfer);

        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.PaidAmountUnits.Should().Be(10_000_000);
        invoice.PaidAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void RegisterTransfer_OverpaidAmount_ShouldSetStatusToOverpaid()
    {
        var invoice = CreateSampleInvoice();
        var transfer = new PaymentTransfer(
            invoice.Id, "TRON", "0xabc999", 0, "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t",
            "TFromAddress123", invoice.DepositAddress, 12_000_000, 100);

        transfer.UpdateConfirmations(120, 19);
        invoice.RegisterTransfer(transfer);

        invoice.Status.Should().Be(InvoiceStatus.Overpaid);
    }

    [Fact]
    public void RegisterTransfer_DuplicateTransfer_ShouldNotDoubleCount()
    {
        var invoice = CreateSampleInvoice();
        var transfer1 = new PaymentTransfer(
            invoice.Id, "TRON", "0xhashDuplicate", 0, "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t",
            "TFrom1", invoice.DepositAddress, 5_000_000, 100);
        transfer1.UpdateConfirmations(120, 19);

        var transfer2 = new PaymentTransfer(
            invoice.Id, "TRON", "0xhashDuplicate", 0, "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t",
            "TFrom1", invoice.DepositAddress, 5_000_000, 100);
        transfer2.UpdateConfirmations(120, 19);

        invoice.RegisterTransfer(transfer1);
        invoice.RegisterTransfer(transfer2);

        invoice.Transfers.Should().HaveCount(1);
        invoice.PaidAmountUnits.Should().Be(5_000_000);
        invoice.Status.Should().Be(InvoiceStatus.PartiallyPaid);
    }
}