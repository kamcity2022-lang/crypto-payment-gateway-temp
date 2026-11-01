using System;

namespace Gateway.Domain.Entities;

public class MerchantNonce
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public string Nonce { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }

    private MerchantNonce() { }

    public MerchantNonce(Guid merchantId, string nonce, TimeSpan validity)
    {
        Id = Guid.NewGuid();
        MerchantId = merchantId;
        Nonce = nonce;
        CreatedAtUtc = DateTime.UtcNow;
        ExpiresAtUtc = CreatedAtUtc.Add(validity);
    }
}