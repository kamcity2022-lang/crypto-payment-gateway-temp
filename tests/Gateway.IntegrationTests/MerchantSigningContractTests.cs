using System;
using System.Text;
using FluentAssertions;
using Gateway.Examples.MerchantIntegration;
using Xunit;

namespace Gateway.IntegrationTests;

// Pure deterministic contract tests: do not contact TronGrid or the API.
public sealed class MerchantSigningContractTests
{
    private const string Json = "{\"fiatAmount\":10,\"fiatCurrency\":\"USD\",\"callbackUrl\":\"https://merchant.example/webhook\",\"expiryMinutes\":30}";
    private const string WebhookJson = "{\"event\":\"invoice.paid\",\"invoiceId\":\"00000000-0000-0000-0000-000000000001\"}";

    [Fact]
    public void MerchantHmac_UsesExactBodyBytesAndSixLineCanonical()
    {
        var data = Encoding.UTF8.GetBytes(Json);
        var signature = GatewaySignatures.SignApiRequest(
            "POST", "/api/v1/invoices", "", "1730880000",
            "nonce-1", data, "example-secret-not-real");
        signature.Should().Be("15d4c9f9f7416afd907eb7ae842a748bff32c619512a06eca2996b400a56823c");

        // Method/path/query and bytes must all be bound to signature.
        GatewaySignatures.SignApiRequest("POST", "/api/v1/invoices", "?a=1",
            "1730880000", "nonce-1", data, "example-secret-not-real")
            .Should().NotBe(signature);
        GatewaySignatures.SignApiRequest("POST", "/api/v1/invoices", "",
            "1730880000", "nonce-1",
            Encoding.UTF8.GetBytes(Json + " "), "example-secret-not-real")
            .Should().NotBe(signature);
    }

    [Fact]
    public void WebhookHmac_VerifiesRawBytesAndRejectsTamperingAndSkew()
    {
        var data = Encoding.UTF8.GetBytes(WebhookJson);
        var signature = GatewaySignatures.SignWebhook(
            "1730880000", data, "example-webhook-secret");
        signature.Should().Be("e16973ce93b586b96ca3e65e46f563daa2a0dc8add3c191c1abf8b02117517c1");

        var observedAt = DateTimeOffset.FromUnixTimeSeconds(1730880000);
        GatewaySignatures.VerifyWebhook("1730880000", signature,
            data, "example-webhook-secret", observedAt).Should().BeTrue();
        GatewaySignatures.VerifyWebhook("1730880000", signature,
            Encoding.UTF8.GetBytes(WebhookJson + " "), "example-webhook-secret", observedAt)
            .Should().BeFalse();
        GatewaySignatures.VerifyWebhook("1730880000", signature,
            data, "wrong-secret", observedAt).Should().BeFalse();
        GatewaySignatures.VerifyWebhook("1730880000", "nothex",
            data, "example-webhook-secret", observedAt).Should().BeFalse();
        GatewaySignatures.VerifyWebhook("1730880000", signature,
            data, "example-webhook-secret", observedAt.AddMinutes(6)).Should().BeFalse();
    }
}
