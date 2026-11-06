using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Application.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Gateway.Infrastructure.ExchangeRates;

// Coinbase Exchange public spot ticker: USDT-USD, no API credential.
// Deliberately fail closed if the feed is missing, stale or depegged.
public sealed class CoinbaseUsdtUsdRateProvider : IExchangeRateProvider
{
    private const string TickerEndpoint =
        "https://api.exchange.coinbase.com/products/USDT-USD/ticker";
    private const string CacheKey = "exchange-rate:USDT-USD:coinbase";
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _maximumAge;
    private readonly TimeSpan _cacheDuration;
    private readonly decimal _minimumRate;
    private readonly decimal _maximumRate;

    public CoinbaseUsdtUsdRateProvider(
        HttpClient http, IMemoryCache cache, IConfiguration config)
    {
        _http = http;
        _cache = cache;
        var maxAgeSeconds = config.GetValue("ExchangeRates:MaximumAgeSeconds", 120);
        var cacheSeconds = config.GetValue("ExchangeRates:CacheSeconds", 15);
        _minimumRate = config.GetValue("ExchangeRates:MinimumUsdtUsd", 0.97m);
        _maximumRate = config.GetValue("ExchangeRates:MaximumUsdtUsd", 1.03m);
        if (maxAgeSeconds is < 1 or > 3600 || cacheSeconds < 0 ||
            cacheSeconds > maxAgeSeconds || _minimumRate <= 0 ||
            _maximumRate < _minimumRate)
            throw new InvalidOperationException("Invalid USDT/USD exchange rate configuration.");

        _maximumAge = TimeSpan.FromSeconds(maxAgeSeconds);
        _cacheDuration = TimeSpan.FromSeconds(cacheSeconds);
    }

    public async Task<ExchangeRateQuote> GetUsdtUsdAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out ExchangeRateQuote? cached) && cached is not null)
        {
            Validate(cached);
            return cached;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TickerEndpoint);
            request.Headers.TryAddWithoutValidation("User-Agent", "CryptoPaymentGateway/1.0");
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (!root.TryGetProperty("price", out var priceElement) ||
                !decimal.TryParse(priceElement.GetString(), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var rate) ||
                !root.TryGetProperty("time", out var timeElement) ||
                !DateTimeOffset.TryParse(timeElement.GetString(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                    out var observed))
                throw new ExchangeRateUnavailableException("Rate provider response is invalid.");

            var quote = new ExchangeRateQuote(rate, "CoinbaseExchange:USDT-USD",
                observed.ToUniversalTime());
            Validate(quote);

            if (_cacheDuration > TimeSpan.Zero)
                _cache.Set(CacheKey, quote, _cacheDuration);
            return quote;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ExchangeRateUnavailableException("Rate provider timed out.");
        }
        catch (HttpRequestException)
        {
            throw new ExchangeRateUnavailableException("Rate provider unavailable.");
        }
        catch (JsonException)
        {
            throw new ExchangeRateUnavailableException("Rate provider returned invalid JSON.");
        }
    }

    private void Validate(ExchangeRateQuote quote)
    {
        var age = DateTimeOffset.UtcNow - quote.ObservedAtUtc;
        if (quote.UsdPerUsdt <= 0 || age > _maximumAge ||
            age < TimeSpan.FromSeconds(-30) || quote.UsdPerUsdt < _minimumRate ||
            quote.UsdPerUsdt > _maximumRate)
            throw new ExchangeRateUnavailableException(
                "USDT/USD rate is stale, invalid or outside configured depeg bounds.");
    }
}
