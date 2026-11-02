using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Api.Authentication;
using Gateway.Application.DTOs;
using Gateway.Blockchain.Tron;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Gateway.Infrastructure.Security;
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
        if (!SsrfValidator.IsDestinationSafe(request.CallbackUrl, allowHttp: false))
        {
            return BadRequest(new { error = "Invalid or prohibited callback URL destination." });
        }
        
        var merchantId = (Guid)HttpContext.Items["MerchantId"]!;

        var fiatCurrency = request.FiatCurrency.Trim().ToUpperInvariant();
        if (fiatCurrency != "USD")
        {
            return BadRequest(new { error = $"Unsupported fiat currency '{fiatCurrency}'. Only 'USD' is supported currently." });
        }

        // 1. Check Idempotency-Key if provided by merchant
        string? idempotencyKey = null;
        string requestPayloadHash = string.Empty;
        if (Request.Headers.TryGetValue("Idempotency-Key", out var ikVal) && !string.IsNullOrWhiteSpace(ikVal))
        {
            idempotencyKey = ikVal.ToString().Trim();
            using var sha = SHA256.Create();
            var rawReq = JsonSerializer.Serialize(request);
            requestPayloadHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(rawReq))).ToLowerInvariant();

            var existingIdempotency = await _dbContext.MerchantIdempotencyKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.MerchantId == merchantId && k.Key == idempotencyKey && k.ExpiresAtUtc > DateTime.UtcNow, ct);

            if (existingIdempotency != null)
            {
                if (existingIdempotency.RequestHash != requestPayloadHash)
                {
                    return Conflict(new { error = "Idempotency conflict: A request with the same Idempotency-Key but different payload already exists." });
                }

                return Content(existingIdempotency.ResponsePayload, "application/json");
            }
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

        var responseDto = new InvoiceResponseDto
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

        // Persist Idempotency response atomically with invoice
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var serializedResponse = JsonSerializer.Serialize(responseDto);
            var record = new MerchantIdempotencyKey(
                merchantId,
                idempotencyKey,
                requestPayloadHash,
                serializedResponse,
                201,
                TimeSpan.FromHours(24)
            );
            _dbContext.MerchantIdempotencyKeys.Add(record);
        }

        await _dbContext.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return CreatedAtAction(nameof(GetInvoiceById), new { invoiceId = invoice.Id }, responseDto);
    }

    [HttpGet("{invoiceId:guid}")]
    [RequireMerchantSignature]
    public async Task<IActionResult> GetInvoiceById(Guid invoiceId, CancellationToken ct)
    {
        if (!HttpContext.Items.TryGetValue("ResolvedMerchantId", out var resolvedMerchantIdObj) || resolvedMerchantIdObj == null)
        {
            return Forbid(); // Missing or invalid tenant context
        }
        var merchantId = (Guid)resolvedMerchantIdObj;

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

    [HttpGet]
    [RequireMerchantSignature]
    public async Task<IActionResult> GetInvoices([FromQuery] InvoiceFilterRequest filter, CancellationToken ct)
    {
        if (!HttpContext.Items.TryGetValue("ResolvedMerchantId", out var resolvedMerchantIdObj) || resolvedMerchantIdObj == null)
            return Forbid();
        var merchantId = (Guid)resolvedMerchantIdObj;

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var query = _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.MerchantId == merchantId);

        if (!string.IsNullOrWhiteSpace(filter.Status) && Enum.TryParse<InvoiceStatus>(filter.Status, true, out var parsedStatus))
        {
            query = query.Where(i => i.Status == parsedStatus);
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(invoice => new InvoiceResponseDto
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
            })
            .ToListAsync(ct);

        return Ok(new PagedResultDto<InvoiceResponseDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        });
    }
}