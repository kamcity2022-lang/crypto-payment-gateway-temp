using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Gateway.Examples.MerchantIntegration;

/// <summary>
/// An executable reference for the gateway's API HMAC and webhook HMAC.
/// Sign exact UTF-8 bytes sent over HTTP; do not reserialize JSON after signing.
/// </summary>
public static class GatewaySignatures
{
    public static string SignApiRequest(
        string method, string path, string rawQuery,
        string timestamp, string nonce, ReadOnlySpan<byte> body, string apiSecret)
    {
        var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = string.Join("\n",
            method.ToUpperInvariant(), path, rawQuery, timestamp, nonce, hash);
        return ComputeHex(Encoding.UTF8.GetBytes(canonical), apiSecret);
    }

    public static string SignWebhook(string timestamp, ReadOnlySpan<byte> body, string webhookSecret)
    {
        // The dispatcher signs $"{timestamp}.{payload}". Build the same
        // bytes without decoding/re-encoding the received raw HTTP body.
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var signedBytes = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signedBytes, 0);
        body.CopyTo(signedBytes.AsSpan(prefix.Length));
        return ComputeHex(signedBytes, webhookSecret);
    }

    public static bool VerifyWebhook(
        string timestamp, string signatureHex, ReadOnlySpan<byte> rawBody,
        string webhookSecret, DateTimeOffset now, long allowedSkewSeconds = 300)
    {
        if (allowedSkewSeconds < 0 ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture,
                out var epoch))
            return false;

        var current = now.ToUnixTimeSeconds();
        if (epoch < current - allowedSkewSeconds || epoch > current + allowedSkewSeconds ||
            signatureHex.Length != 64)
            return false;

        byte[] provided;
        try { provided = Convert.FromHexString(signatureHex); }
        catch (FormatException) { return false; }

        var expected = Convert.FromHexString(SignWebhook(timestamp, rawBody, webhookSecret));
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static string ComputeHex(byte[] data, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
    }
}
