using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Application.Contracts;
using Gateway.Infrastructure.ExchangeRates;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Gateway.IntegrationTests;

public sealed class ExchangeRateProviderTests
{
    private sealed class TickerHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _response;
        public int Calls { get; private set; }
        public TickerHandler(Func<HttpResponseMessage> response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.ToString()
                .Should().Be("https://api.exchange.coinbase.com/products/USDT-USD/ticker");
            Calls++;
            return Task.FromResult(_response());
        }
    }

    private static CoinbaseUsdtUsdRateProvider Create(TickerHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new System.Collections.Generic.Dictionary<string, string?>
            {
                ["ExchangeRates:MaximumAgeSeconds"] = "120",
                ["ExchangeRates:CacheSeconds"] = "15",
                ["ExchangeRates:MinimumUsdtUsd"] = "0.97",
                ["ExchangeRates:MaximumUsdtUsd"] = "1.03"
            }).Build();
        return new CoinbaseUsdtUsdRateProvider(
            new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), configuration);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Quote(decimal price, DateTimeOffset time) =>
        "{\"price\":\"" + price.ToString(CultureInfo.InvariantCulture) +
        "\",\"time\":\"" + time.ToString("O", CultureInfo.InvariantCulture) + "\"}";

    [Fact]
    public async Task FreshRate_ReturnsQuoteAndUsesShortCache()
    {
        var handler = new TickerHandler(() => Json(Quote(0.995m, DateTimeOffset.UtcNow)));
        var provider = Create(handler);
        var first = await provider.GetUsdtUsdAsync(CancellationToken.None);
        var second = await provider.GetUsdtUsdAsync(CancellationToken.None);
        first.UsdPerUsdt.Should().Be(0.995m);
        first.Source.Should().Be("CoinbaseExchange:USDT-USD");
        second.Should().Be(first);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task StaleRate_IsRejected()
    {
        var handler = new TickerHandler(() =>
            Json(Quote(1.0m, DateTimeOffset.UtcNow.AddMinutes(-10))));
        Func<Task> act = () => Create(handler).GetUsdtUsdAsync(CancellationToken.None);
        await act.Should().ThrowAsync<ExchangeRateUnavailableException>();
    }

    [Theory]
    [InlineData("0.95")]
    [InlineData("1.05")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task DepegOrInvalidRate_IsRejected(string rate)
    {
        var handler = new TickerHandler(() =>
            Json("{\"price\":\"" + rate + "\",\"time\":\"" +
                 DateTimeOffset.UtcNow.ToString("O") + "\"}"));
        Func<Task> act = () => Create(handler).GetUsdtUsdAsync(CancellationToken.None);
        await act.Should().ThrowAsync<ExchangeRateUnavailableException>();
    }

    [Fact]
    public async Task HttpFailure_IsRejected()
    {
        var handler = new TickerHandler(() => Json("{}", HttpStatusCode.ServiceUnavailable));
        Func<Task> act = () => Create(handler).GetUsdtUsdAsync(CancellationToken.None);
        await act.Should().ThrowAsync<ExchangeRateUnavailableException>();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("{\"price\":\"NaN\",\"time\":\"2020-01-01T00:00:00Z\"}")]
    public async Task MalformedRate_IsRejected(string body)
    {
        var handler = new TickerHandler(() => Json(body));
        Func<Task> act = () => Create(handler).GetUsdtUsdAsync(CancellationToken.None);
        await act.Should().ThrowAsync<ExchangeRateUnavailableException>();
    }

    [Fact]
    public async Task FutureTimestamp_IsRejected()
    {
        var handler = new TickerHandler(() =>
            Json(Quote(1.0m, DateTimeOffset.UtcNow.AddMinutes(5))));
        Func<Task> act = () => Create(handler).GetUsdtUsdAsync(CancellationToken.None);
        await act.Should().ThrowAsync<ExchangeRateUnavailableException>();
    }
}
