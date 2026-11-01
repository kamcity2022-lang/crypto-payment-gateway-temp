using System;
using System.Collections.Generic;

namespace Gateway.Application.DTOs;

public class InvoiceFilterRequest
{
    public string? Status { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class PagedResultDto<T>
{
    public IReadOnlyCollection<T> Items { get; set; } = Array.Empty<T>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}

public class TestWebhookRequest
{
    public string? DestinationUrl { get; set; }
}

public class TestWebhookResponse
{
    public Guid EventId { get; set; }
    public string DestinationUrl { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime DispatchedAtUtc { get; set; }
}