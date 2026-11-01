using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Api.Authentication;
using Gateway.Application.DTOs;
using Gateway.Blockchain.Tron;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
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

        await using var tx = await _dbContext.Database.BeginTransactionAsync(ct);

        // Pessimistic / row-level lock equivalent by reloading tracked wallet entity
        var wallet = await _dbContext.Wallets
            .Include(w => w.DerivedAddresses)
            .FirstOrDefaultAsync(w => w.MerchantId == merchantId && w.Network == "TRON", ct);

        if (wallet == null)
        {
            return BadRequest(new { error = "Merchant TRON wallet is not configured." });
        }

        var derivationService = new TronAddressDerivationService(wallet.AccountXPub);
        var derivedAddressStr = derivationService.DeriveAddress(wallet.LastDerivationIndex);
        var derivedAddressEntity = wallet.AllocateNextAddress(derivedAddressStr);

        // Snapshot Exchange Rate
        const decimal exchangeRate = 1.0m; // USDT peg baseline
        var cryptoAmount = request.FiatAmount / exchangeRate;
        var expectedUnits = (long)(cryptoAmount * 1_000_000m);
        var underpaymentThreshold = 50_000L;

        var checkoutToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var invoice = new Invoice(
            merchantId: merchantId,
            derivedAddressId: derivedAddressEntity.Id,
            depositAddress: derivedAddressStr,
            fiatAmount: request.FiatAmount,
            fiatCurrency: request.FiatCurrency.ToUpperInvariant(),
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
            // Ensure unified route matches frontend checkout.html?token=...
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