using System;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.Contracts;

namespace Gateway.IntegrationTests;

internal sealed class FixedTestRateProvider : IExchangeRateProvider
{
    private readonly ExchangeRateQuote _quote;
    public FixedTestRateProvider(ExchangeRateQuote? quote = null) =>
        _quote = quote ?? new ExchangeRateQuote(1.0m, "Test:FixedRate", DateTimeOffset.UtcNow);

    public Task<ExchangeRateQuote> GetUsdtUsdAsync(CancellationToken ct) =>
        Task.FromResult(_quote);
}

internal sealed class ThrowingRateProvider : IExchangeRateProvider
{
    public Task<ExchangeRateQuote> GetUsdtUsdAsync(CancellationToken ct) =>
        throw new ExchangeRateUnavailableException("Provider outage simulated by test.");
}
