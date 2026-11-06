using System;

namespace Gateway.Domain.Entities;

// The raw key is never persisted; only its SHA-256 hash is stored.
// The HMAC secret is encrypted at rest and is returned only on creation/rotation.
public sealed class MerchantApiKey
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string ApiKeyHash { get; private set; } = string.Empty;
    public string EncryptedSecret { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    private MerchantApiKey() { }

    public MerchantApiKey(Guid merchantId, string name, string apiKeyHash, string encryptedSecret)
    {
        if (merchantId == Guid.Empty) throw new ArgumentException("Merchant is required.", nameof(merchantId));
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new ArgumentException("Name must be 1 to 100 characters.", nameof(name));
        if (string.IsNullOrWhiteSpace(apiKeyHash) || apiKeyHash.Length != 64)
            throw new ArgumentException("API key SHA-256 hash must be 64 characters.", nameof(apiKeyHash));
        if (string.IsNullOrWhiteSpace(encryptedSecret))
            throw new ArgumentException("Encrypted API secret is required.", nameof(encryptedSecret));

        Id = Guid.NewGuid();
        MerchantId = merchantId;
        Name = name.Trim();
        ApiKeyHash = apiKeyHash;
        EncryptedSecret = encryptedSecret;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public void Revoke() => RevokedAtUtc ??= DateTime.UtcNow;
}
