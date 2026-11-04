using System;

namespace Gateway.Domain.Entities;

public class Plan
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public int MonthlyInvoiceLimit { get; private set; }
    public decimal MonthlyVolumeLimitUsd { get; private set; }
    public decimal PriceUsd { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsDefault { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private Plan() { }

    public Plan(string name, string description, int monthlyInvoiceLimit, decimal monthlyVolumeLimitUsd, decimal priceUsd, bool isDefault = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Plan name cannot be null, empty, or whitespace.", nameof(name));
        if (monthlyInvoiceLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(monthlyInvoiceLimit), "Monthly invoice limit must be greater than zero.");
        if (monthlyVolumeLimitUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(monthlyVolumeLimitUsd), "Monthly volume limit cannot be negative. Use 0 for unlimited.");
        if (priceUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(priceUsd), "Price cannot be negative.");

        Id = Guid.NewGuid();
        Name = name.Trim();
        Description = description.Trim();
        MonthlyInvoiceLimit = monthlyInvoiceLimit;
        MonthlyVolumeLimitUsd = monthlyVolumeLimitUsd;
        PriceUsd = priceUsd;
        IsActive = true;
        IsDefault = isDefault;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public void Update(string name, string description, int monthlyInvoiceLimit, decimal monthlyVolumeLimitUsd, decimal priceUsd)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Plan name cannot be null, empty, or whitespace.", nameof(name));
        if (monthlyInvoiceLimit <= 0) throw new ArgumentOutOfRangeException(nameof(monthlyInvoiceLimit));
        if (monthlyVolumeLimitUsd < 0) throw new ArgumentOutOfRangeException(nameof(monthlyVolumeLimitUsd));
        if (priceUsd < 0) throw new ArgumentOutOfRangeException(nameof(priceUsd));

        Name = name.Trim();
        Description = description.Trim();
        MonthlyInvoiceLimit = monthlyInvoiceLimit;
        MonthlyVolumeLimitUsd = monthlyVolumeLimitUsd;
        PriceUsd = priceUsd;
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;
    public void SetAsDefault(bool isDefault) => IsDefault = isDefault;
}