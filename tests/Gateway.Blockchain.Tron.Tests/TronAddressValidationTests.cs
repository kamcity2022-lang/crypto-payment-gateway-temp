using System;
using FluentAssertions;
using Xunit;

namespace Gateway.Blockchain.Tron.Tests;

public class TronAddressValidationTests
{
    [Fact]
    public void Base58CheckDecode_ValidAddress_ShouldSucceed()
    {
        var validAddress = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH";
        var act = () => InvokeBase58CheckDecode(validAddress);
        act.Should().NotThrow();
    }

    [Fact]
    public void Base58CheckDecode_InvalidChecksum_ShouldThrow()
    {
        // Corrupt the last character to break the checksum
        var invalidAddress = "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdX"; 
        var act = () => InvokeBase58CheckDecode(invalidAddress);
        act.Should().Throw<FormatException>().WithMessage("*checksum*");
    }

    [Fact]
    public void Base58CheckDecode_WrongPrefix_ShouldThrow()
    {
        // Valid Bitcoin address (prefix 0x00), not TRON (0x41)
        var btcAddress = "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa";
        var act = () => InvokeBase58CheckDecode(btcAddress);
        act.Should().Throw<FormatException>().WithMessage("*prefix*");
    }

    [Fact]
    public void Base58CheckDecode_InvalidLength_ShouldThrow()
    {
        var shortAddress = "TShort";
        var act = () => InvokeBase58CheckDecode(shortAddress);
        act.Should().Throw<FormatException>();
    }

    // Reflection helper to test the private static method in TronScannerService
    private byte[] InvokeBase58CheckDecode(string base58)
    {
        var method = typeof(Gateway.Blockchain.Tron.TronScannerService).GetMethod(
            "Base58CheckDecode", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        if (method == null) throw new Exception("Method not found");
        
        try 
        {
            return (byte[])method.Invoke(null, new object[] { base58 })!;
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            throw ex.InnerException!;
        }
    }
}