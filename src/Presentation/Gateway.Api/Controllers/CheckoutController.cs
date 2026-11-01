using System;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.DTOs;
using Gateway.Domain.Enums;
using Gateway.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Controllers;

[ApiController]
[Route("api/v1/checkout")]
public class CheckoutController : ControllerBase
{
    private readonly GatewayDbContext _dbContext;

    public CheckoutController(GatewayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> GetCheckoutStatus(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new { error = "Checkout token is required." });

        var invoice = await _dbContext.Invoices
            .Include(i => i.Transfers)
            .FirstOrDefaultAsync(i => i.CheckoutToken == token, ct);

        if (invoice == null)
            return NotFound(new { error = "Payment checkout session not found." });

        // Update expiry in memory for display
        invoice.EvaluateStatus();

        var remainingSeconds = Math.Max(0, (long)(invoice.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds);

        return Ok(new CheckoutStatusDto
        {
            DepositAddress = invoice.DepositAddress,
            ExpectedAmount = invoice.ExpectedAmountUnits / 1_000_000m,
            PaidAmount = invoice.PaidAmountUnits / 1_000_000m,
            Currency = invoice.Currency,
            Network = invoice.Network,
            Status = invoice.Status.ToString(),
            RemainingSeconds = remainingSeconds
        });
    }
}