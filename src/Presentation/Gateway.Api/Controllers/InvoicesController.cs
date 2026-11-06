using System;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Api.Authentication;
using Gateway.Application.Contracts;
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
    private readonly IExchangeRateProvider _exchangeRateProvider;

    public InvoicesController(GatewayDbContext dbContext, IExchangeRateProvider exchangeRateProvider)
    {
        _dbContext = dbContext;
        _exchangeRateProvider = exchangeRateProvider;
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
        var requestPayloadHash = string.Empty;
        if (Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyHeader) &&
            !string.IsNullOrWhiteSpace(idempotencyHeader))
        {
            idempotencyKey = idempotencyHeader.ToString().Trim();
            var serializedRequest = JsonSerializer.Serialize(request);
            requestPayloadHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(serializedRequest))
            ).ToLowerInvariant();
        }

        // Optimization only: an already-committed successful request can be replayed
        // even if the wallet or subscription has since changed. The check is repeated
        // under the wallet lock below to handle concurrent requests safely.
        if (idempotencyKey is not null)
        {
            var cached = await _dbContext.MerchantIdempotencyKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(k =>
                    k.MerchantId == merchantId &&
                    k.Key == idempotencyKey &&
                    k.ExpiresAtUtc > DateTime.UtcNow, ct);

            if (cached is not null)
            {
                return ReturnIdempotencyResult(cached, requestPayloadHash);
            }
        }

        // Obtain a validated market quote before acquiring DB row locks. A
        // provider outage must not consume quota, address indices or invoices.
        ExchangeRateQuote quote;
        try
        {
            quote = await _exchangeRateProvider.GetUsdtUsdAsync(ct);
        }
        catch (ExchangeRateUnavailableException)
        {
            return StatusCode(503, new
            {
                error = "EXCHANGE_RATE_UNAVAILABLE",
                message = "A fresh USDT/USD rate is not currently available."
            });
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // Always acquire locks in this order. The wallet lock serializes
            // invoice creation for this merchant, including same-key retries.
            var wallet = await _dbContext.Wallets
                .FromSqlInterpolated($"SELECT * FROM \"Wallets\" WHERE \"MerchantId\" = {merchantId} AND \"Network\" = 'TRON' FOR UPDATE")
                .FirstOrDefaultAsync(ct);

            if (wallet is null)
            {
                await tx.RollbackAsync(ct);
                return BadRequest(new { error = "Merchant TRON wallet is not configured." });
            }

            // Check again inside the serialized section *before* requiring an active
            // subscription, so a valid replay never consumes or requires quota.
            MerchantIdempotencyKey? idempotencyRecord = null;
            if (idempotencyKey is not null)
            {
                idempotencyRecord = await _dbContext.MerchantIdempotencyKeys
                    .FirstOrDefaultAsync(k => k.MerchantId == merchantId && k.Key == idempotencyKey, ct);

                if (idempotencyRecord is not null && idempotencyRecord.ExpiresAtUtc > DateTime.UtcNow)
                {
                    await tx.RollbackAsync(ct);
                    return ReturnIdempotencyResult(idempotencyRecord, requestPayloadHash);
                }
            }

            var subscription = await _dbContext.Subscriptions
                .FromSqlInterpolated($"SELECT * FROM \"Subscriptions\" WHERE \"MerchantId\" = {merchantId} AND \"Status\" = 1 FOR UPDATE")
                .FirstOrDefaultAsync(ct);

            if (subscription is null)
            {
                await tx.RollbackAsync(ct);
                return StatusCode(403, new
                {
                    error = "SUBSCRIPTION_NOT_ACTIVE",
                    message = "Merchant does not have an active subscription."
                });
            }

            subscription.ResetPeriodIfExpired();

            var plan = await _dbContext.Plans.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == subscription.PlanId, ct);
            if (plan is null)
            {
                await tx.RollbackAsync(ct);
                return StatusCode(500, new
                {
                    error = "SUBSCRIPTION_PLAN_NOT_FOUND",
                    message = "Subscription plan is missing."
                });
            }

            var effectiveInvoiceLimit = subscription.CustomInvoiceLimit ?? plan.MonthlyInvoiceLimit;
            var effectiveVolumeLimit = subscription.CustomVolumeLimitUsd ?? plan.MonthlyVolumeLimitUsd;

            if (subscription.CurrentPeriodInvoiceCount >= effectiveInvoiceLimit)
            {
                await tx.RollbackAsync(ct);
                return StatusCode(403, new
                {
                    error = "SUBSCRIPTION_INVOICE_LIMIT_REACHED",
                    message = $"Monthly invoice limit of {effectiveInvoiceLimit} has been reached."
                });
            }

            if (effectiveVolumeLimit > 0 &&
                subscription.CurrentPeriodVolumeUsd + request.FiatAmount > effectiveVolumeLimit)
            {
                await tx.RollbackAsync(ct);
                return StatusCode(403, new
                {
                    error = "SUBSCRIPTION_VOLUME_LIMIT_REACHED",
                    message = $"Monthly volume limit of {effectiveVolumeLimit} USD has been reached."
                });
            }

            // The quota update, wallet index allocation, invoice and idempotency
            // response are persisted in the same transaction.
            subscription.RecordInvoiceCreation(request.FiatAmount);

            var derivationService = new TronAddressDerivationService(wallet.AccountXPub);
            var derivedAddressStr = derivationService.DeriveAddress(wallet.LastDerivationIndex);
            var derivedAddressEntity = wallet.AllocateNextAddress(derivedAddressStr);
            // Explicitly mark the newly allocated address as Added.
            // Otherwise EF Core can assume an entity with a populated generated GUID key already exists.
            _dbContext.DerivedAddresses.Add(derivedAddressEntity);

            var exchangeRate = quote.UsdPerUsdt;
            var cryptoAmount = request.FiatAmount / exchangeRate;
            var expectedUnits = (long)Math.Round(cryptoAmount * 1_000_000m, MidpointRounding.AwayFromZero);
            const long underpaymentThreshold = 50_000L;
            var checkoutToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

            var invoice = new Invoice(
                merchantId: merchantId,
                derivedAddressId: derivedAddressEntity.Id,
                depositAddress: derivedAddressStr,
                fiatAmount: request.FiatAmount,
                fiatCurrency: fiatCurrency,
                exchangeRate: exchangeRate,
                exchangeRateSource: quote.Source,
                exchangeRateTimestampUtc: quote.ObservedAtUtc.UtcDateTime,
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

            if (idempotencyKey is not null)
            {
                var serializedResponse = JsonSerializer.Serialize(responseDto);
                if (idempotencyRecord is not null)
                {
                    // Expired record: the unique (MerchantId, Key) row is reused.
                    // The update is not committed unless invoice creation succeeds.
                    idempotencyRecord.UpdateForReuse(requestPayloadHash, TimeSpan.FromHours(24));
                    idempotencyRecord.RecordSuccess(serializedResponse, 201);
                }
                else
                {
                    _dbContext.MerchantIdempotencyKeys.Add(new MerchantIdempotencyKey(
                        merchantId,
                        idempotencyKey,
                        requestPayloadHash,
                        serializedResponse,
                        201,
                        TimeSpan.FromHours(24)));
                }
            }

            await _dbContext.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return CreatedAtAction(nameof(GetInvoiceById), new { invoiceId = invoice.Id }, responseDto);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException pgEx &&
            pgEx.SqlState == PostgresErrorCodes.UniqueViolation &&
            pgEx.ConstraintName == "IX_MerchantIdempotencyKeys_MerchantId_Key")
        {
            await tx.RollbackAsync(ct);
            return Conflict(new
            {
                error = "Idempotency conflict: A request with the same Idempotency-Key already exists."
            });
        }
    }

    private IActionResult ReturnIdempotencyResult(MerchantIdempotencyKey record, string requestPayloadHash)
    {
        if (record.RequestHash != requestPayloadHash)
        {
            return Conflict(new
            {
                error = "Idempotency conflict: A request with the same Idempotency-Key but different payload already exists."
            });
        }

        return new ContentResult
        {
            Content = record.ResponsePayload,
            ContentType = "application/json",
            StatusCode = record.ResponseStatusCode
        };
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

        if (!string.IsNullOrWhiteSpace(filter.Status) &&
            Enum.TryParse<InvoiceStatus>(filter.Status, true, out var parsedStatus))
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
