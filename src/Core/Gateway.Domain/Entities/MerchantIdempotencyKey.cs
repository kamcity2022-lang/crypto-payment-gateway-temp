
namespace Gateway.Domain.Entities;

public class MerchantIdempotencyKey
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public string ResponsePayload { get; private set; } = string.Empty;
    public int ResponseStatusCode { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }

    private MerchantIdempotencyKey() { }

    public MerchantIdempotencyKey(
        Guid merchantId,
        string key,
        string requestHash,
        string responsePayload,
        int responseStatusCode,
        TimeSpan validityDuration)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Idempotency key cannot be empty.", nameof(key));
        
        Id = Guid.NewGuid();
        MerchantId = merchantId;
        Key = key.Trim();
        RequestHash = requestHash;
        ResponsePayload = responsePayload;
        ResponseStatusCode = responseStatusCode;
        CreatedAtUtc = DateTime.UtcNow;
        ExpiresAtUtc = CreatedAtUtc.Add(validityDuration);
    }

    // ✅ NEW: Safely reuse an expired idempotency record to respect unique constraints
    public void UpdateForReuse(string newRequestHash, TimeSpan validityDuration)
    {
        RequestHash = newRequestHash;
        ResponsePayload = string.Empty;
        ResponseStatusCode = 0;
        CreatedAtUtc = DateTime.UtcNow;
        ExpiresAtUtc = CreatedAtUtc.Add(validityDuration);
    }

    // ✅ NEW: Record success payload atomically
    public void RecordSuccess(string payload, int statusCode)
    {
        ResponsePayload = payload;
        ResponseStatusCode = statusCode;
    }
}