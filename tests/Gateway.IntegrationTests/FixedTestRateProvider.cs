using System;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.Contracts;

namespace Gateway.IntegrationTests;

internal sealed class FixedTestRateProvider : IExchangeRateProvider
{
    public Task<ExchangeRateQuote> GetUsdtUsdAsync(CancellationToken ct) =>
        Task.FromResult(new ExchangeRateQuote(
            1.0m, "Test:FixedRate", DateTimeOffset.UtcNow));
}
