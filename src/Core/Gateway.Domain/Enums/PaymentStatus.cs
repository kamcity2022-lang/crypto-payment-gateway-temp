namespace Gateway.Domain.Enums;

public enum PaymentStatus
{
    Observed = 1,
    Confirming = 2,
    Confirmed = 3,
    Failed = 4,
    Ignored = 5
}