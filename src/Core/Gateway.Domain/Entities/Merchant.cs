using System;
using Gateway.Domain.Enums;

namespace Gateway.Domain.Entities;

public class Merchant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string ApiKeyHash { get; private set; } = string.Empty;
    public string EncryptedApiKeySecret { get; private set; } = string.Empty;
    public string EncryptedWebhookSecret { get; private set; } = string.Empty;
    public string? SettlementAddress { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public bool IsActive { get; private set; }

    private Merchant() { } // Required for ORM

    public string? DefaultWebhookUrl { get; private set; }
    
    private readonly List<Subscription> _subscriptions = new();
    public IReadOnlyCollection<Subscription> Subscriptions => _subscriptions.AsReadOnly();
    

    public void AddSubscription(Subscription subscription)
    {
        if (subscription == null) throw new ArgumentNullException(nameof(subscription));
        _subscriptions.Add(subscription);
    }

    public Subscription? GetActiveSubscription()
    {
        return _subscriptions.FirstOrDefault(s => s.Status == SubscriptionStatus.Active);
    }

    public void SetDefaultWebhookUrl(string webhookUrl)
    {
        if (!string.IsNullOrWhiteSpace(webhookUrl))
        {
            DefaultWebhookUrl = webhookUrl.Trim();
        }
    }
    
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

    public void SetSettlementAddress(string settlementAddress)
    {
        if (string.IsNullOrWhiteSpace(settlementAddress))
            throw new ArgumentException("Settlement address cannot be empty.", nameof(settlementAddress));

        var trimmed = settlementAddress.Trim();
        if (trimmed.Length != 34 || !trimmed.StartsWith("T", StringComparison.Ordinal))
            throw new ArgumentException("Invalid TRON settlement address format.", nameof(settlementAddress));

        const string base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        foreach (var c in trimmed)
        {
            if (!base58Alphabet.Contains(c))
                throw new ArgumentException("Settlement address contains invalid Base58 characters.", nameof(settlementAddress));
        }

        SettlementAddress = trimmed;
    }
    
    public void UpdateName(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Merchant name cannot be empty.", nameof(newName));

        var trimmed = newName.Trim();
        if (trimmed.Length < 3 || trimmed.Length > 100)
            throw new ArgumentException("Merchant name must be between 3 and 100 characters.", nameof(newName));

        Name = trimmed;
    }
    
    public void Activate()
    {
        IsActive = true;
    }

    public void Deactivate()
    {
        IsActive = false;
    }
}