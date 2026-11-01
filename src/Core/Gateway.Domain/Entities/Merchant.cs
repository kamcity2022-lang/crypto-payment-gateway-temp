using System;

namespace Gateway.Domain.Entities;

public class Merchant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string ApiKeyHash { get; private set; } = string.Empty;
    public string EncryptedApiKeySecret { get; private set; } = string.Empty;
    public string EncryptedWebhookSecret { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public bool IsActive { get; private set; }

    private Merchant() { } // Required for ORM

    public Merchant(Guid id, string name, string apiKeyHash, string encryptedApiKeySecret, string encryptedWebhookSecret)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Name = name;
        ApiKeyHash = apiKeyHash;
        EncryptedApiKeySecret = encryptedApiKeySecret;
        EncryptedWebhookSecret = encryptedWebhookSecret;
        CreatedAtUtc = DateTime.UtcNow;
        IsActive = true;
    }
}