using System;
using System.ComponentModel.DataAnnotations;

namespace Gateway.Application.DTOs;

public class CreatePlanRequest
{
    [Required, StringLength(50)]
    public string Name { get; set; } = string.Empty;
    
    [StringLength(255)]
    public string Description { get; set; } = string.Empty;
    
    [Range(1, int.MaxValue)]
    public int MonthlyInvoiceLimit { get; set; }
    
    [Range(0, double.MaxValue)] // 0 means unlimited
    public decimal MonthlyVolumeLimitUsd { get; set; }
    
    [Range(0, double.MaxValue)]
    public decimal PriceUsd { get; set; }
    
    public bool IsDefault { get; set; }
}

public class UpdatePlanRequest
{
    [Required, StringLength(50)]
    public string Name { get; set; } = string.Empty;
    
    [StringLength(255)]
    public string Description { get; set; } = string.Empty;
    
    [Range(1, int.MaxValue)]
    public int MonthlyInvoiceLimit { get; set; }
    
    [Range(0, double.MaxValue)]
    public decimal MonthlyVolumeLimitUsd { get; set; }
    
    [Range(0, double.MaxValue)]
    public decimal PriceUsd { get; set; }
}

public class PlanResponseDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int MonthlyInvoiceLimit { get; set; }
    public decimal MonthlyVolumeLimitUsd { get; set; }
    public decimal PriceUsd { get; set; }
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class OverrideLimitsRequest
{
    public int? CustomInvoiceLimit { get; set; }
    public decimal? CustomVolumeLimitUsd { get; set; }
}