namespace Gateway.Domain.Enums;

public enum InvoiceStatus
{
    Pending = 1,
    PartiallyPaid = 2,
    Paid = 3,
    Overpaid = 4,
    Underpaid = 5,
    Expired = 6,
    Cancelled = 7
}