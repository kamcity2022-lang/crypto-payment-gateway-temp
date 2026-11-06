using System;
using System.Data;
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
using Npgsql;

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

        if (!HttpContext.Items.TryGetValue("MerchantId", out var merchantIdObj) || merchantIdObj is not Guid merchantId)
        {
            return Forbid();
        }

        var fiatCurrency = request.FiatCurrency.Trim().ToUpperInvariant();
        if (fiatCurrency != "USD")
        {
            return BadRequest(new { error = $"Unsupported fiat currency '{fiatCurrency}'. Only 'USD' is supported currently." });
        }

        string? idempotencyKey = null;
        string requestPayloadHash = string.Empty;
        if (Request.Headers.TryGetValue("Idempotency-Key", out var ikVal) && !string.IsNullOrWhiteSpace(ikVal))
        {
            idempotencyKey = ikVal.ToString().Trim();
            using var sha = SHA256.Create();
            var rawReq = JsonSerializer.Serialize(request);
            requestPayloadHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(rawReq))).ToLowerInvariant();
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            // 1. Exclusive row lock on the wallet row (existing behavior)
            var wallet = await _dbContext.Wallets
                .FromSqlInterpolated($"SELECT * FROM \"Wallets\" WHERE \"MerchantId\" = {merchantId} AND \"Network\" = 'TRON' FOR UPDATE")
                .FirstOrDefaultAsync(ct);

            if (wallet == null)
            {
                await tx.RollbackAsync(ct);
                return BadRequest(new { error = "Merchant TRON wallet is not configured." });
            }

            // 2. Exclusive row lock on the active subscription for usage check
            var subscription = await _dbContext.Subscriptions
                .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
                .FirstOrDefaultAsync(ct);

            if (subscription == null)
            {
                await tx.RollbackAsync(ct);
                return StatusCode(403, new { error = "SUBSCRIPTION_NOT_ACTIVE", message = "Merchant does not have an active subscription." });
            }

            // Reset period if expired (lazy evaluation)
            subscription.ResetPeriodIfExpired();

            // 3. Load Plan (no lock needed - read-only)
            var plan = await _dbContext.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == subscription.PlanId, ct);
            if (plan == null)
            {
                await tx.RollbackAsync(ct);
                return StatusCode(500, new { error = "SUBSCRIPTION_PLAN_NOT_FOUND", message = "Subscription plan is missing." });
            }

            // 4. Re-check Idempotency INSIDE the transaction to prevent race conditions and double quota consumption
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                var existingIdempotency = await _dbContext.MerchantIdempotencyKeys
                    .FirstOrDefaultAsync(k => k.MerchantId == merchantId && k.Key == idempotencyKey, ct);
                    
                if (existingIdempotency != null)
                {
                    if (existingIdempotency.RequestHash != requestPayloadHash)
                    {
                        await tx.RollbackAsync(ct);
                        return Conflict(new { error = "Idempotency conflict: A request with the and different payload already exists." });
                    }
                    await tx.RollbackAsync(ct);
                    return Content(existingIdempotency.ResponsePayload, "application/json");
                }
            }

            var effectiveInvoiceLimit = subscription.CustomInvoiceLimit ?? plan.MonthlyInvoiceLimit;
            var effectiveVolumeLimit = subscription.CustomVolumeLimitUsd ?? plan.MonthlyVolumeLimitUsd;

            // 5. Check Quotas
            if (subscription.CurrentPeriodInvoiceCount >= effectiveInvoiceLimit)
            {
                await tx.RollbackAsync(ct);
                return StatusCode(403, new { error = "SUBSCRIPTION_INVOICE_LIMIT_REACHED", message = $"Monthly invoice limit of {effectiveInvoiceLimit} has been reached." });
            }

            if (effectiveVolumeLimit > 0 && (subscription.CurrentPeriodVolumeUsd + request.FiatAmount) > effectiveVolumeLimit)
            {
                await tx.RollbackAsync(ct);
                return StatusCode(403, new { error = "SUBSCRIPTION_VOLUME_LIMIT_REACHED", message = $"Monthly volume limit of {effectiveVolumeLimit} USD has been reached." });
            }

            // 6. Increment Usage atomically using the Domain method (Fixes CS0200)
            subscription.RecordInvoiceCreation(request.FiatAmount);

            // 7. Derive address and create invoice (existing behavior)
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

            // 8. Persist Idempotency response atomically with invoice
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
        {
            await tx.RollbackAsync(ct);
            return Conflict(new { error = "Idempotency conflict: A request with the same Idempotency-Key is currently being processed or already exists." });
        }
    }

    [HttpGet("{invoiceId:guid}")]
    [RequireMerchantSignature]
    public async Task<IActionResult> GetInvoiceById(Guid invoiceId, CancellationToken ct)
    {
        if (!HttpContext.Items.TryGetValue("MerchantId", out var merchantIdObj) || merchantIdObj is not Guid merchantId)
        {
            return Forbid();
        }

        var invoice = await _dbContext.Invoices
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.MerchantId == merchantId, ct);

        if (invoice is null)
        {
            return NotFound(new { error = "Invoice not found." });
        }

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
        if (!HttpContext.Items.TryGetValue("MerchantId", out var merchantIdObj) || merchantIdObj is not Guid merchantId)
        {
            return Forbid();
        }

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var query = _dbContext.Invoices.AsNoTracking().Where(i => i.MerchantId == merchantId);

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