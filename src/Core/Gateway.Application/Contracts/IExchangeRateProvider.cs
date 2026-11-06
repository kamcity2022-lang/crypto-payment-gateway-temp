using System;
using System.Threading;
using System.Threading.Tasks;

namespace Gateway.Application.Contracts;

// USDT/USD: USD value of one USDT; decimal precision and observation time
// are persisted in every invoice snapshot.
public sealed record ExchangeRateQuote(
    decimal UsdPerUsdt, string Source, DateTimeOffset ObservedAtUtc);

public interface IExchangeRateProvider
{
    Task<ExchangeRateQuote> GetUsdtUsdAsync(CancellationToken cancellationToken);
}

public sealed class ExchangeRateUnavailableException : Exception
{
    public ExchangeRateUnavailableException(string message) : base(message) { }
}
