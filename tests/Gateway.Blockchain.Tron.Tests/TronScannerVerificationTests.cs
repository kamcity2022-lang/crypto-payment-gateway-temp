using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Gateway.Blockchain.Tron;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gateway.Blockchain.Tron.Tests;

public class TronScannerVerificationTests
{
    private const string UsdtContract = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
    private const string DepositAddress = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";
    private const string TxHash = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    // یک HttpMessageHandler ساده برای شبیه‌سازی پاسخ‌های HTTP بدون نیاز به کتابخانه خارجی
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public MockHttpMessageHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }

    private TronScannerService CreateService(HttpResponseMessage response)
    {
        var handler = new MockHttpMessageHandler(response);
        var httpClient = new HttpClient(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string, string?>("Tron:UsdtContractAddress", UsdtContract) })
            .Build();
        return new TronScannerService(null!, httpClient, config, NullLogger<TronScannerService>.Instance);
    }

    private string BuildSolidityNodeResponse(bool success, string contractHex, string recipientHex, string amountHex)
    {
        var log = new
        {
            address = contractHex,
            topics = new[]
            {
                "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef",
                "0x0000000000000000000000000000000000000000000000000000000000000000",
                "0x000000000000000000000000" + recipientHex
            },
            data = "0x" + amountHex
        };
        return JsonSerializer.Serialize(new { blockNumber = 123456, receipt = new { result = success ? "SUCCESS" : "FAILED" }, log = new[] { log } });
    }

    [Fact]
    public async Task VerifySolidifiedTransaction_CorrectEvent_ShouldSucceed()
    {
        var expectedAmount = 10_000_000L;
        var amountHex = expectedAmount.ToString("x").PadLeft(64, '0');
        var contractHex = TronAddressToHexTest(UsdtContract);
        var recipientHex = TronAddressToHexTest(DepositAddress);
        var responseBody = BuildSolidityNodeResponse(true, contractHex, recipientHex, amountHex);

        var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) });
        var result = await service.VerifySolidifiedTransactionAsync(TxHash, DepositAddress, expectedAmount, 0, CancellationToken.None);

        result.IsSolidified.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
        result.BlockNumber.Should().Be(123456);
    }

    [Fact]
    public async Task VerifySolidifiedTransaction_WrongRecipient_ShouldFail()
    {
        var expectedAmount = 10_000_000L;
        var amountHex = expectedAmount.ToString("x").PadLeft(64, '0');
        var contractHex = TronAddressToHexTest(UsdtContract);
        var wrongRecipient = "0000000000000000000000000000000000000000";
        var responseBody = BuildSolidityNodeResponse(true, contractHex, wrongRecipient, amountHex);

        var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) });
        var result = await service.VerifySolidifiedTransactionAsync(TxHash, DepositAddress, expectedAmount, 0, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task VerifySolidifiedTransaction_WrongContract_ShouldFail()
    {
        var expectedAmount = 10_000_000L;
        var amountHex = expectedAmount.ToString("x").PadLeft(64, '0');
        var wrongContract = "0000000000000000000000000000000000000000";
        var recipientHex = TronAddressToHexTest(DepositAddress);
        var responseBody = BuildSolidityNodeResponse(true, wrongContract, recipientHex, amountHex);

        var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) });
        var result = await service.VerifySolidifiedTransactionAsync(TxHash, DepositAddress, expectedAmount, 0, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task VerifySolidifiedTransaction_FailedReceipt_ShouldFail()
    {
        var expectedAmount = 10_000_000L;
        var amountHex = expectedAmount.ToString("x").PadLeft(64, '0');
        var contractHex = TronAddressToHexTest(UsdtContract);
        var recipientHex = TronAddressToHexTest(DepositAddress);
        var responseBody = BuildSolidityNodeResponse(false, contractHex, recipientHex, amountHex);

        var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) });
        var result = await service.VerifySolidifiedTransactionAsync(TxHash, DepositAddress, expectedAmount, 0, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task VerifySolidifiedTransaction_AmbiguousMultipleMatches_ShouldFail()
    {
        var expectedAmount = 10_000_000L;
        var amountHex = expectedAmount.ToString("x").PadLeft(64, '0');
        var contractHex = TronAddressToHexTest(UsdtContract);
        var recipientHex = TronAddressToHexTest(DepositAddress);

        var log = new
        {
            address = contractHex,
            topics = new[]
            {
                "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef",
                "0x0000000000000000000000000000000000000000000000000000000000000000",
                "0x000000000000000000000000" + recipientHex
            },
            data = "0x" + amountHex
        };
        var responseBody = JsonSerializer.Serialize(new { blockNumber = 123456, receipt = new { result = "SUCCESS" }, log = new[] { log, log } });

        var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) });
        var result = await service.VerifySolidifiedTransactionAsync(TxHash, DepositAddress, expectedAmount, 0, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    private static string TronAddressToHexTest(string base58Address)
    {
        var bytes = Base58CheckDecodeTest(base58Address);
        return Convert.ToHexString(bytes.AsSpan(1)).ToLowerInvariant();
    }

    private static byte[] Base58CheckDecodeTest(string base58)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var intData = System.Numerics.BigInteger.Zero;
        for (var i = 0; i < base58.Length; i++)
        {
            var index = alphabet.IndexOf(base58[i]);
            if (index < 0) throw new FormatException();
            intData = intData * 58 + index;
        }
        var bytes = intData.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = 0;
        for (var i = 0; i < base58.Length && base58[i] == '1'; i++) leadingZeros++;
        var result = new byte[leadingZeros + bytes.Length];
        Buffer.BlockCopy(bytes, 0, result, leadingZeros, bytes.Length);
        var payload = new byte[result.Length - 4];
        Buffer.BlockCopy(result, 0, payload, 0, payload.Length);
        return payload;
    }
    
        [Fact]
    public async Task VerifySolidifiedTransaction_EventIndexOutOfBounds_ShouldFail()
    {
        var expectedAmount = 10_000_000L;
        var amountHex = expectedAmount.ToString("x").PadLeft(64, '0');
        var contractHex = TronAddressToHexTest(UsdtContract);
        var recipientHex = TronAddressToHexTest(DepositAddress);
        var responseBody = BuildSolidityNodeResponse(true, contractHex, recipientHex, amountHex); // Only 1 log in array

        var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) });
        // Requesting event_index 5, but array only has length 1
        var result = await service.VerifySolidifiedTransactionAsync(TxHash, DepositAddress, expectedAmount, 5, CancellationToken.None);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task VerifySolidifiedTransaction_CorrectLogAtWrongIndex_ShouldFail()
    {
        var expectedAmount = 10_000_000L;
        var amountHex = expectedAmount.ToString("x").PadLeft(64, '0');
        var contractHex = TronAddressToHexTest(UsdtContract);
        var recipientHex = TronAddressToHexTest(DepositAddress);

        var correctLog = new {
            address = contractHex,
            topics = new[] { "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef", "0x00", "0x000000000000000000000000" + recipientHex },
            data = "0x" + amountHex
        };
        var dummyLog = new { address = "0000", topics = new[] { "0x11", "0x22", "0x33" }, data = "0x00" };
        
        // Correct log is at index 1, dummy log is at index 0
        var responseBody = JsonSerializer.Serialize(new { blockNumber = 123456, receipt = new { result = "SUCCESS" }, log = new[] { dummyLog, correctLog } });

        var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) });
        // Requesting event_index 0, which points to the dummy log
        var result = await service.VerifySolidifiedTransactionAsync(TxHash, DepositAddress, expectedAmount, 0, CancellationToken.None);
        result.IsSuccess.Should().BeFalse();
    }
    
    [Fact]
    public async Task VerifySolidifiedTransaction_CorrectEventAtIndex1_ShouldSucceed()
    {
        var expectedAmount = 10_000_000L;
        var amountHex = expectedAmount.ToString("x").PadLeft(64, '0');
        var contractHex = TronAddressToHexTest(UsdtContract);
        var recipientHex = TronAddressToHexTest(DepositAddress);

        var dummyLog = new { address = "0000", topics = new[] { "0x11", "0x22", "0x33" }, data = "0x00" };
        var correctLog = new {
            address = contractHex,
            topics = new[] { "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef", "0x00", "0x000000000000000000000000" + recipientHex },
            data = "0x" + amountHex
        };
        
        var responseBody = JsonSerializer.Serialize(new { blockNumber = 123456, receipt = new { result = "SUCCESS" }, log = new[] { dummyLog, correctLog } });
        var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) });
        
        var result = await service.VerifySolidifiedTransactionAsync(TxHash, DepositAddress, expectedAmount, 1, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task VerifySolidifiedTransaction_MatchingTransferAtWrongIndex_ShouldFail()
    {
        var expectedAmount = 10_000_000L;
        var amountHex = expectedAmount.ToString("x").PadLeft(64, '0');
        var contractHex = TronAddressToHexTest(UsdtContract);
        var recipientHex = TronAddressToHexTest(DepositAddress);

        var correctLog = new {
            address = contractHex,
            topics = new[] { "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef", "0x00", "0x000000000000000000000000" + recipientHex },
            data = "0x" + amountHex
        };
        var dummyLog = new { address = "0000", topics = new[] { "0x11", "0x22", "0x33" }, data = "0x00" };
        
        // Correct log is at index 0, but we request index 1
        var responseBody = JsonSerializer.Serialize(new { blockNumber = 123456, receipt = new { result = "SUCCESS" }, log = new[] { correctLog, dummyLog } });
        var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) });
        
        var result = await service.VerifySolidifiedTransactionAsync(TxHash, DepositAddress, expectedAmount, 1, CancellationToken.None);
        result.IsSuccess.Should().BeFalse();
    }
    
}