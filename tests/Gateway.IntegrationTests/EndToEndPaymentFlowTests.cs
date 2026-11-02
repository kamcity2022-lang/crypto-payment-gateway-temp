using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Blockchain.Tron;
using Gateway.Domain.Entities;
using Gateway.Domain.Enums;
using Gateway.Infrastructure.Security;
using Gateway.Infrastructure.Webhooks;
using Gateway.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gateway.IntegrationTests;

public class EndToEndPaymentFlowTests : IAsyncLifetime
{
    #region Mock Handlers

    private sealed class TronGridMockHttpHandler : HttpMessageHandler
    {
        private readonly ulong _solidifiedBlockNumber;
        private readonly Dictionary<string, string> _transactionResponses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _accountResponses = new(StringComparer.OrdinalIgnoreCase);

        public TronGridMockHttpHandler(ulong solidifiedBlockNumber) => _solidifiedBlockNumber = solidifiedBlockNumber;

        public void AddTransactionResponse(string txHash, string responseJson) => _transactionResponses[txHash] = responseJson;
        public void AddAccountResponse(string address, string responseJson) => _accountResponses[address] = responseJson;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (url.Contains("getnowblock"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"block_header\":{{\"raw_data\":{{\"number\":{_solidifiedBlockNumber}}}}}}}")
                };
            }

            if (url.Contains("/transactions/trc20"))
            {
                foreach (var kvp in _accountResponses)
                {
                    if (url.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(kvp.Value)
                        };
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") };
            }

            if (url.Contains("gettransactioninfobyid") && request.Content != null)
            {
                var body = await request.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("value", out var v))
                {
                    var hash = v.GetString();
                    if (hash != null && _transactionResponses.TryGetValue(hash, out var r))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(r) };
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    #endregion

    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("test_e2e_db").WithUsername("postgres").WithPassword("postgres_test_password").Build();

    private GatewayDbContext _dbContext = null!;
    private AesGcmSecretProtector _secretProtector = null!;

    private const string ValidDepositAddress = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";
    private const string UsdtContract = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new[]
        {
            new KeyValuePair<string, string?>("Security:MasterEncryptionKey", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"),
            new KeyValuePair<string, string?>("Tron:UsdtContractAddress", UsdtContract)
        }).Build();

        _secretProtector = new AesGcmSecretProtector(cfg);
        var opts = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(_dbContainer.GetConnectionString()).Options;
        _dbContext = new GatewayDbContext(opts);
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    private (Merchant merchant, Wallet wallet) CreateMerchantAndWallet()
    {
        var mid = Guid.NewGuid();
        var m = new Merchant(mid, "Test", "hash_" + Guid.NewGuid().ToString("N"), _secretProtector.Encrypt("api"), _secretProtector.Encrypt("whsec"));
        var w = new Wallet(mid, "xpub6BosfCnifzxcFwrSzQiqu2DBVTshkCXacvNsWGYJVVhhawA7d4R5WSWGFNbi8Aw6ZRc1brxMyWMzG3DSSSSoekkudhUd9yLb6qx39T9nMdj", "TRON");
        _dbContext.Merchants.Add(m);
        _dbContext.Wallets.Add(w);
        return (m, w);
    }

    private Invoice CreateInvoice(Merchant m, Wallet w, long units = 10_000_000L, TimeSpan? val = null)
    {
        var da = w.AllocateNextAddress(ValidDepositAddress);
        _dbContext.DerivedAddresses.Add(da);
        var inv = new Invoice(m.Id, da.Id, ValidDepositAddress, 10m, "USD", 1m, "Peg", DateTime.UtcNow,
            units, 50_000L, "https://merchant.example.com/webhook", Guid.NewGuid().ToString(), val ?? TimeSpan.FromMinutes(30));
        _dbContext.Invoices.Add(inv);
        return inv;
    }

    private string BuildAccountResp(string tx, int ei, long amt, long ts) => JsonSerializer.Serialize(new
    {
        data = new[] { new { transaction_id = tx, to = ValidDepositAddress, from = "TFrom", value = amt.ToString(), event_index = ei, block_timestamp = ts } }
    });

    private string BuildReceiptResp(ulong bn, string ch, string rh, string ah, int ei)
    {
        var logs = new List<object>();
        for (int i = 0; i <= ei; i++)
        {
            if (i == ei)
            {
                logs.Add(new
                {
                    address = "41" + ch,
                    topics = new[]
                    {
                        "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef",
                        "0x0000000000000000000000000000000000000000000000000000000000000000",
                        "0x000000000000000000000000" + rh
                    },
                    data = "0x" + ah.PadLeft(64, '0')
                });
            }
            else
            {
                logs.Add(new { address = "0000", topics = new[] { "0x11", "0x22", "0x33" }, data = "0x00" });
            }
        }
        return JsonSerializer.Serialize(new { blockNumber = bn, receipt = new { result = "SUCCESS" }, log = logs.ToArray() });
    }

    [Fact]
    public async Task EndToEnd_FullPaymentFlow_ShouldSucceed()
    {
        var (m, w) = CreateMerchantAndWallet();
        var inv = CreateInvoice(m, w);
        await _dbContext.SaveChangesAsync();

        var tx = "abc123def456";
        var amt = 10_000_000L;
        var ch = THex(UsdtContract);
        var rh = THex(ValidDepositAddress);
        var ah = amt.ToString("x");

        var mock = new TronGridMockHttpHandler(100_000_000UL);
        mock.AddAccountResponse(ValidDepositAddress, BuildAccountResp(tx, 0, amt, 1700000000));
        mock.AddTransactionResponse(tx, BuildReceiptResp(99_999_900UL, ch, rh, ah, 0));

        using var hc = new HttpClient(mock);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Tron:UsdtContractAddress", UsdtContract) }).Build();
        var scanner = new TronScannerService(_dbContext, hc, cfg, NullLogger<TronScannerService>.Instance);

        await scanner.ScanPendingDepositsAsync(CancellationToken.None);

        var ui = await _dbContext.Invoices.AsNoTracking().FirstAsync(i => i.Id == inv.Id);
        ui.Status.Should().Be(InvoiceStatus.Paid);
        ui.PaidAmountUnits.Should().Be(amt);

        var tr = await _dbContext.PaymentTransfers.AsNoTracking().FirstOrDefaultAsync(t => t.TransactionHash == tx);
        tr.Should().NotBeNull();

        var oe = await _dbContext.OutboxEvents.AsNoTracking().FirstOrDefaultAsync(o => o.AggregateId == inv.Id);
        oe.Should().NotBeNull();
        oe!.IsProcessed.Should().BeFalse();

        var wh = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var wc = new HttpClient(wh);
        var disp = new WebhookDispatcher(_dbContext, wc, _secretProtector, NullLogger<WebhookDispatcher>.Instance);
        await disp.DispatchPendingWebhooksAsync(CancellationToken.None);

        wh.Captured.Should().HaveCount(1);
        var (req, body) = wh.Captured[0];
        body.Should().Equal(Encoding.UTF8.GetBytes(oe.Payload));
        
        var ts = req.Headers.GetValues("X-CryptoGateway-Timestamp").First();
        var sig = req.Headers.GetValues("X-CryptoGateway-Signature").First();
        sig.Should().Be(WebhookDispatcher.ComputeHmacSha256($"{ts}.{Encoding.UTF8.GetString(body)}", "whsec"));
    }

    [Fact]
    public async Task ScannerIdempotency_ProcessingSameTransferTwice_ShouldNotDuplicate()
    {
        var (m, w) = CreateMerchantAndWallet();
        var inv = CreateInvoice(m, w);
        await _dbContext.SaveChangesAsync();

        var tx = "idem123";
        var amt = 10_000_000L;
        var ch = THex(UsdtContract);
        var rh = THex(ValidDepositAddress);
        var ah = amt.ToString("x");

        var mock = new TronGridMockHttpHandler(100_000_000UL);
        mock.AddAccountResponse(ValidDepositAddress, BuildAccountResp(tx, 0, amt, 1700000000));
        mock.AddTransactionResponse(tx, BuildReceiptResp(99_999_900UL, ch, rh, ah, 0));

        using var hc = new HttpClient(mock);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Tron:UsdtContractAddress", UsdtContract) }).Build();
        var scanner = new TronScannerService(_dbContext, hc, cfg, NullLogger<TronScannerService>.Instance);

        await scanner.ScanPendingDepositsAsync(CancellationToken.None);
        await scanner.ScanPendingDepositsAsync(CancellationToken.None);

        var transfers = await _dbContext.PaymentTransfers.AsNoTracking().Where(t => t.TransactionHash == tx).ToListAsync();
        transfers.Should().HaveCount(1);

        var events = await _dbContext.OutboxEvents.AsNoTracking().Where(o => o.AggregateId == inv.Id).ToListAsync();
        events.Should().HaveCount(1);
    }

    [Fact]
    public async Task PartialPayment_TwoTransfers_ShouldTransitionCorrectly()
    {
        var (m, w) = CreateMerchantAndWallet();
        var inv = CreateInvoice(m, w, units: 10_000_000L);
        await _dbContext.SaveChangesAsync();

        var tx1 = "p1"; var tx2 = "p2";
        var a1 = 5_000_000L; var a2 = 5_000_000L;
        var ch = THex(UsdtContract); var rh = THex(ValidDepositAddress);

        var mock = new TronGridMockHttpHandler(100_000_000UL);
        mock.AddAccountResponse(ValidDepositAddress, BuildAccountResp(tx1, 0, a1, 1700000000));
        mock.AddTransactionResponse(tx1, BuildReceiptResp(99_999_900UL, ch, rh, a1.ToString("x"), 0));

        using var hc = new HttpClient(mock);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Tron:UsdtContractAddress", UsdtContract) }).Build();
        var scanner = new TronScannerService(_dbContext, hc, cfg, NullLogger<TronScannerService>.Instance);

        await scanner.ScanPendingDepositsAsync(CancellationToken.None);

        var i1 = await _dbContext.Invoices.AsNoTracking().FirstAsync(i => i.Id == inv.Id);
        i1.Status.Should().Be(InvoiceStatus.PartiallyPaid);

        mock.AddAccountResponse(ValidDepositAddress, JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { transaction_id = tx1, to = ValidDepositAddress, from = "T", value = a1.ToString(), event_index = 0, block_timestamp = 1700000000L },
                new { transaction_id = tx2, to = ValidDepositAddress, from = "T", value = a2.ToString(), event_index = 0, block_timestamp = 1700000100L }
            }
        }));
        mock.AddTransactionResponse(tx2, BuildReceiptResp(99_999_901UL, ch, rh, a2.ToString("x"), 0));

        await scanner.ScanPendingDepositsAsync(CancellationToken.None);

        var i2 = await _dbContext.Invoices.AsNoTracking().FirstAsync(i => i.Id == inv.Id);
        i2.Status.Should().Be(InvoiceStatus.Paid);
        i2.PaidAmountUnits.Should().Be(a1 + a2);
    }

    [Fact]
    public async Task LatePayment_ExpiredInvoice_ShouldTransitionToPaid()
    {
        var (m, w) = CreateMerchantAndWallet();
        var inv = CreateInvoice(m, w, val: TimeSpan.FromSeconds(-10));
        await _dbContext.SaveChangesAsync();

        var tx = "late"; var amt = 10_000_000L;
        var ch = THex(UsdtContract); var rh = THex(ValidDepositAddress);

        var mock = new TronGridMockHttpHandler(100_000_000UL);
        mock.AddAccountResponse(ValidDepositAddress, BuildAccountResp(tx, 0, amt, 1700000000));
        mock.AddTransactionResponse(tx, BuildReceiptResp(99_999_900UL, ch, rh, amt.ToString("x"), 0));

        using var hc = new HttpClient(mock);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Tron:UsdtContractAddress", UsdtContract) }).Build();
        var scanner = new TronScannerService(_dbContext, hc, cfg, NullLogger<TronScannerService>.Instance);

        await scanner.ScanPendingDepositsAsync(CancellationToken.None);

        var ui = await _dbContext.Invoices.AsNoTracking().FirstAsync(i => i.Id == inv.Id);
        ui.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task WebhookRetry_FailedThenSuccess_ShouldEventuallySucceed()
    {
        var es = _secretProtector.Encrypt("whsec_retry");
        var ob = new OutboxEvent(Guid.NewGuid(), "invoice.paid", "{}", "https://example.com/wh", es);
        _dbContext.OutboxEvents.Add(ob);
        await _dbContext.SaveChangesAsync();

        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxEvents\" SET \"NextAttemptAtUtc\" = {DateTime.UtcNow.AddMinutes(-5)} WHERE \"Id\" = {ob.Id}");

        var fh = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var fc = new HttpClient(fh);
        var d1 = new WebhookDispatcher(_dbContext, fc, _secretProtector, NullLogger<WebhookDispatcher>.Instance);
        await d1.DispatchPendingWebhooksAsync(CancellationToken.None);

        var af = await _dbContext.OutboxEvents.AsNoTracking().FirstAsync(o => o.Id == ob.Id);
        af.IsProcessed.Should().BeFalse(); af.AttemptCount.Should().Be(1);

        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OutboxEvents\" SET \"NextAttemptAtUtc\" = {DateTime.UtcNow.AddMinutes(-5)} WHERE \"Id\" = {ob.Id}");

        var sh = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var sc = new HttpClient(sh);
        var d2 = new WebhookDispatcher(_dbContext, sc, _secretProtector, NullLogger<WebhookDispatcher>.Instance);
        await d2.DispatchPendingWebhooksAsync(CancellationToken.None);

        var as2 = await _dbContext.OutboxEvents.AsNoTracking().FirstAsync(o => o.Id == ob.Id);
        as2.IsProcessed.Should().BeTrue(); as2.AttemptCount.Should().Be(2);
    }

    private static string THex(string b58)
    {
        var bytes = B58(b58);
        return Convert.ToHexString(bytes.AsSpan(1)).ToLowerInvariant();
    }

    private static byte[] B58(string b58)
    {
        const string alpha = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var id = System.Numerics.BigInteger.Zero;
        foreach (var c in b58) { var ix = alpha.IndexOf(c); if (ix < 0) throw new FormatException(); id = id * 58 + ix; }
        var bs = id.ToByteArray(isUnsigned: true, isBigEndian: true);
        var lz = 0; foreach (var c in b58) { if (c == '1') lz++; else break; }
        var res = new byte[lz + bs.Length]; Buffer.BlockCopy(bs, 0, res, lz, bs.Length);
        var pl = new byte[res.Length - 4]; Buffer.BlockCopy(res, 0, pl, 0, pl.Length);
        return pl;
    }
}