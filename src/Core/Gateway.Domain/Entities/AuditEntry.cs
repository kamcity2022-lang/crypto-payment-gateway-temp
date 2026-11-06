using System;

namespace Gateway.Domain.Entities;

// Append-only operational metadata; never persist keys, secrets, tokens or request bodies.
public sealed class AuditEntry
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid? MerchantId { get; private set; }
    public string? ActorUserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string TargetType { get; private set; } = string.Empty;
    public string TargetId { get; private set; } = string.Empty;
    public DateTime OccurredAtUtc { get; private set; } = DateTime.UtcNow;

    private AuditEntry() { }
    public AuditEntry(Guid? merchantId, string? actorUserId,
        string action, string targetType, string targetId)
    {
        if (string.IsNullOrWhiteSpace(action) || action.Length > 100)
            throw new ArgumentException("Invalid audit action.", nameof(action));
        if (string.IsNullOrWhiteSpace(targetType) || targetType.Length > 50)
            throw new ArgumentException("Invalid audit target type.", nameof(targetType));
        if (string.IsNullOrWhiteSpace(targetId) || targetId.Length > 100)
            throw new ArgumentException("Invalid audit target ID.", nameof(targetId));
        MerchantId = merchantId;
        ActorUserId = string.IsNullOrWhiteSpace(actorUserId) ? null : actorUserId;
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
    }
}
