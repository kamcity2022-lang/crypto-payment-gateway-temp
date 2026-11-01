using System;
using System.Text.Json;
using FluentAssertions;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Xunit;

namespace Gateway.Domain.Tests;

public class CorePaymentEngineE2ETests
{
    [Fact]
    public void FullPaymentLifecycle_FromCreationToOutbox_ShouldSucceedAtomically()
    {
        // 1. Arrange Merchant, Wallet, and Invoice
        var merchantId = Guid.NewGuid();
        var merchant = new Merchant(
            merchantId,
            name: "Test Store",
            apiKeyHash: "hash-123",
            encryptedApiKeySecret: "secret-enc",
            encryptedWebhookSecret: "webhook-secret-enc"
        );

        var depositAddress = "TYasdfghjkl1234567890qwertyuiop12";
        var expectedUnits = 50_000_000L; // 50 USDT (6 decimals)

        var invoice = new Invoice(
            merchantId: merchant.Id,
            derivedAddressId: Guid.NewGuid(),
            depositAddress: depositAddress,
            fiatAmount: 50.0m,
            fiatCurrency: "USD",
            exchangeRate: 1.0m,
            exchangeRateSource: "DirectPegProvider",
            exchangeRateTimestampUtc: DateTime.UtcNow,
            expectedAmountUnits: expectedUnits,
            underpaymentThresholdUnits: 50_000L,
            callbackUrl: "https://api.store.com/webhooks/crypto",
            checkoutToken: "checkout-token-xyz",
            validityDuration: TimeSpan.FromMinutes(30)
        );

        invoice.Status.Should().Be(InvoiceStatus.Pending);

        // 2. Incoming Blockchain Transfer Observed by Scanner
        var transfer = new PaymentTransfer(
            invoiceId: invoice.Id,
            network: "TRON",
            transactionHash: "0xabcdef1234567890",
            transferIndex: 0,
            tokenContractAddress: "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t",
            fromAddress: "TCustomerWalletAddress1234567890",
            toAddress: depositAddress,
            amountUnits: expectedUnits,
            blockNumber: 50_000_000
        );

        invoice.RegisterTransfer(transfer);
        invoice.Status.Should().Be(InvoiceStatus.Pending); // Not confirmed yet

        // 3. Confirmations Reach Finality Threshold (e.g. 20 blocks)
        transfer.UpdateConfirmations(currentBlockNumber: 50_000_025, requiredConfirmations: 19);
        invoice.EvaluateStatus();

        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.PaidAmountUnits.Should().Be(expectedUnits);
        invoice.PaidAtUtc.Should().NotBeNull();

        // 4. Generate Atomic Outbox Event for Webhook Dispatcher
        var webhookPayload = JsonSerializer.Serialize(new
        {
            @event = "invoice.paid",
            invoiceId = invoice.Id,
            amountPaidUnits = invoice.PaidAmountUnits,
            status = invoice.Status.ToString(),
            paidAt = invoice.PaidAtUtc
        });

        var outboxEvent = new OutboxEvent(
            aggregateId: invoice.Id,
            eventType: "invoice.paid",
            payload: webhookPayload,
            destinationUrl: invoice.CallbackUrl,
            secretKey: "merchant-webhook-secret"
        );

        outboxEvent.IsProcessed.Should().BeFalse();
        outboxEvent.AttemptCount.Should().Be(0);
        outboxEvent.NextAttemptAtUtc.Should().NotBeNull();

        // 5. Simulate Outbox Success Delivery
        outboxEvent.RecordSuccess();
        outboxEvent.IsProcessed.Should().BeTrue();
        outboxEvent.NextAttemptAtUtc.Should().BeNull();
    }
}