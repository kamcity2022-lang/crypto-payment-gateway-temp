using System;
using System.Security.Cryptography;
using System.Text;

namespace Gateway.Domain.Entities;

public class MerchantClaimToken
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public string HashedToken { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public bool IsUsed { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private MerchantClaimToken() { }

    public MerchantClaimToken(Guid merchantId, string plainToken, TimeSpan validity)
    {
        Id = Guid.NewGuid();
        MerchantId = merchantId;
        HashedToken = HashToken(plainToken);
        ExpiresAtUtc = DateTime.UtcNow.Add(validity);
        CreatedAtUtc = DateTime.UtcNow;
        IsUsed = false;
    }

    public bool Validate(string plainToken)
    {
        if (IsUsed || DateTime.UtcNow > ExpiresAtUtc) return false;
        return HashedToken == HashToken(plainToken);
    }

    public void MarkAsUsed()
    {
        IsUsed = true;
    }

    private static string HashToken(string token)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
}