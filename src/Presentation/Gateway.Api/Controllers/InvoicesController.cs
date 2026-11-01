using System;
using System.Data;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Api.Authentication;
using Gateway.Application.DTOs;
using Gateway.Blockchain.Tron;
using Gateway.Domain.Entities;
using Gateway.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/invoices")]
public class InvoicesController : ControllerBase
{
    private readonly GatewayDbContext _dbContext;

    public InvoicesController(GatewayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpPost]
    [RequireMerchantSignature]
    public async Task<IActionResult> CreateInvoice([FromBody] CreateInvoiceRequest request, CancellationToken ct)
    {
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        // Restrict fiat currency support explicitly (Phase E requirement)
        var fiatCurrency = request.FiatCurrency.Trim().ToUpperInvariant();
        if (fiatCurrency != "USD")
        {
            return BadRequest(new { error = $"Unsupported fiat currency '{fiatCurrency}'. Only 'USD' is supported currently." });
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Exclusive row lock on the wallet row to prevent concurrent derivation collisions
        var wallet = await _dbContext.Wallets
            .FromSqlInterpolated($"SELECT * FROM \"Wallets\" WHERE \"MerchantId\" = {merchantId} AND \"Network\" = 'TRON' FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (wallet == null)
        {
            return BadRequest(new { error = "Merchant TRON wallet is not configured." });
        }

        var derivationService = new TronAddressDerivationService(wallet.AccountXPub);
        var derivedAddressStr = derivationService.DeriveAddress(wallet.LastDerivationIndex);
        var derivedAddressEntity = wallet.AllocateNextAddress(derivedAddressStr);

        // Explicit 1.0 USD -> 1.0 USDT snapshot pegged rate
        const decimal exchangeRate = 1.0m;
        var cryptoAmount = request.FiatAmount / exchangeRate;
        var expectedUnits = (long)Math.Round(cryptoAmount * 1_000_000m, MidpointRounding.AwayFromZero);
        var underpaymentThreshold = 50_000L;

        var checkoutToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var invoice = new Invoice(
            merchantId: merchantId,
            derivedAddressId: derivedAddressEntity.Id,
            depositAddress: derivedAddressStr,
            fiatAmount: request.FiatAmount,
            fiatCurrency: fiatCurrency,
            exchangeRate: exchangeRate,
            exchangeRateSource: "DirectPegProvider",
            exchangeRateTimestampUtc: DateTime.UtcNow,
            expectedAmountUnits: expectedUnits,
            underpaymentThresholdUnits: underpaymentThreshold,
            callbackUrl: request.CallbackUrl,
            checkoutToken: checkoutToken,
            validityDuration: TimeSpan.FromMinutes(request.ExpiryMinutes)
        );

        _dbContext.Invoices.Add(invoice);
        await _dbContext.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var response = new InvoiceResponseDto
        {
            InvoiceId = invoice.Id,
            DepositAddress = invoice.DepositAddress,
            FiatAmount = invoice.FiatAmount,
            FiatCurrency = invoice.FiatCurrency,
            ExchangeRate = invoice.ExchangeRate,
            ExpectedCryptoAmount = cryptoAmount,
            ExpectedUnits = invoice.ExpectedAmountUnits,
            Currency = invoice.Currency,
            Network = invoice.Network,
            Status = invoice.Status.ToString(),
            CheckoutUrl = $"{Request.Scheme}://{Request.Host}/checkout.html?token={invoice.CheckoutToken}",
            ExpiresAtUtc = invoice.ExpiresAtUtc,
            CreatedAtUtc = invoice.CreatedAtUtc
        };

        return CreatedAtAction(nameof(GetInvoiceById), new { invoiceId = invoice.Id }, response);
    }

    [HttpGet("{invoiceId:guid}")]
    [RequireMerchantSignature]
    public async Task<IActionResult> GetInvoiceById(Guid invoiceId, CancellationToken ct)
    {
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        var invoice = await _dbContext.Invoices
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.MerchantId == merchantId, ct);

        if (invoice == null)
            return NotFound(new { error = "Invoice not found." });

        return Ok(new InvoiceResponseDto
        {
            InvoiceId = invoice.Id,
            DepositAddress = invoice.DepositAddress,
            FiatAmount = invoice.FiatAmount,
            FiatCurrency = invoice.FiatCurrency,
            ExchangeRate = invoice.ExchangeRate,
            ExpectedCryptoAmount = invoice.ExpectedAmountUnits / 1_000_000m,
            ExpectedUnits = invoice.ExpectedAmountUnits,
            Currency = invoice.Currency,
            Network = invoice.Network,
            Status = invoice.Status.ToString(),
            CheckoutUrl = $"{Request.Scheme}://{Request.Host}/checkout.html?token={invoice.CheckoutToken}",
            ExpiresAtUtc = invoice.ExpiresAtUtc,
            CreatedAtUtc = invoice.CreatedAtUtc
        });
    }
}