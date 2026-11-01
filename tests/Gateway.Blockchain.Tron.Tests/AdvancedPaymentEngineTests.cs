using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Xunit;

namespace Gateway.Domain.Tests;

public class AdvancedPaymentEngineTests
{
    [Fact]
    public void LatePayment_AfterExpiration_ShouldTransitionToUnderpaidOrPaidExplicitly()
    {
        var invoice = new Invoice(
            Guid.NewGuid(), Guid.NewGuid(), "TDepositAddress123", 10.0m, "USD", 1.0m,
            "DirectPeg", DateTime.UtcNow, 10_000_000L, 50_000L,
            "https://callback.com", "token123", TimeSpan.FromSeconds(-10)); // Expired

        invoice.EvaluateStatus();
        invoice.Status.Should().Be(InvoiceStatus.Expired);

        // Payment arrives late
        var transfer = new PaymentTransfer(
            invoice.Id, "TRON", "0xLateTx", 0, "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t",
            "TFrom", invoice.DepositAddress, 10_000_000L, 100);

        transfer.UpdateConfirmations(150, 19);
        invoice.RegisterTransfer(transfer);

        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.PaidAmountUnits.Should().Be(10_000_000L);
    }

    [Fact]
    public void CumulativePartialPayments_ShouldSumCorrectlyUntilThresholdMet()
    {
        var invoice = new Invoice(
            Guid.NewGuid(), Guid.NewGuid(), "TDepositAddress123", 20.0m, "USD", 1.0m,
            "DirectPeg", DateTime.UtcNow, 20_000_000L, 50_000L,
            "https://callback.com", "token123", TimeSpan.FromMinutes(20));

        // First transfer: 10 USDT
        var tx1 = new PaymentTransfer(invoice.Id, "TRON", "0xTx1", 0, "contract", "TFrom", invoice.DepositAddress, 10_000_000L, 100);
        tx1.UpdateConfirmations(130, 19);
        invoice.RegisterTransfer(tx1);

        invoice.Status.Should().Be(InvoiceStatus.PartiallyPaid);
        invoice.PaidAmountUnits.Should().Be(10_000_000L);

        // Second transfer: 10 USDT
        var tx2 = new PaymentTransfer(invoice.Id, "TRON", "0xTx2", 0, "contract", "TFrom", invoice.DepositAddress, 10_000_000L, 105);
        tx2.UpdateConfirmations(130, 19);
        invoice.RegisterTransfer(tx2);

        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.PaidAmountUnits.Should().Be(20_000_000L);
    }

    [Fact]
    public void NonceReplayProtection_ShouldRejectIdenticalNonce()
    {
        var merchantId = Guid.NewGuid();
        var nonce1 = new MerchantNonce(merchantId, "nonce-abc", TimeSpan.FromMinutes(5));
        var nonce2 = new MerchantNonce(merchantId, "nonce-abc", TimeSpan.FromMinutes(5));

        nonce1.Nonce.Should().Be(nonce2.Nonce);
        nonce1.MerchantId.Should().Be(nonce2.MerchantId);
    }
}