using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

public class SupplierClientsResponse
{
    public IEnumerable<SupplierClientDto> Items { get; set; } = [];

    public int Total { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }
}

public class SupplierClientDto
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string? Label { get; set; }

    public bool Anonymized { get; set; }

    public int JobsAccepted { get; set; }

    public int JobsCompleted { get; set; }

    public DateTime? LastJobAt { get; set; }

    public DateTime? NextJobAt { get; set; }

    public long GrossPaidCents { get; set; }

    public bool Regular { get; set; }

    public SupplierClientContactDto? Contact { get; set; }
}

public class SupplierClientContactDto
{
    public string? Name { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }
}

public class SupplierClientDetailDto : SupplierClientDto
{
    public IEnumerable<SupplierClientJobDto> RecentJobs { get; set; } = [];
}

public class SupplierClientJobDto
{
    public Guid RequestId { get; set; }

    public string? ServiceName { get; set; }

    public string Category { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Phase { get; set; } = string.Empty;

    public DateTime? ScheduledStart { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int? AmountCents { get; set; }
}

public static class SupplierClientsMapper
{
    public static SupplierClientsResponse ToResponse(SupplierClientsPage page, int pageNumber, int pageSize) => new()
    {
        Items = page.Items.Select(ToDto).ToList(),
        Total = page.Total,
        Page = pageNumber,
        PageSize = pageSize,
    };

    public static SupplierClientDetailDto ToDetailDto(SupplierClientDetail detail)
    {
        var dto = Fill(new SupplierClientDetailDto(), detail.Client);
        dto.RecentJobs = detail.RecentJobs.Select(job => new SupplierClientJobDto
        {
            RequestId = job.RequestId,
            ServiceName = job.ServiceName,
            Category = job.Category,
            Status = job.Status.ToString(),
            Phase = job.Phase,
            ScheduledStart = job.ScheduledStart,
            CompletedAt = job.CompletedAt,
            AmountCents = job.AmountCents,
        }).ToList();
        return dto;
    }

    private static SupplierClientDto ToDto(SupplierClientSummary client) => Fill(new SupplierClientDto(), client);

    private static T Fill<T>(T dto, SupplierClientSummary client)
        where T : SupplierClientDto
    {
        dto.Id = client.Id;
        dto.Kind = client.IsPrivate ? "private" : "host";
        dto.Source = client.IsPrivate ? SupplierRequestSources.Showcase : SupplierRequestSources.CasaZen;
        dto.Label = client.Label;
        dto.Anonymized = client.Anonymized;
        dto.JobsAccepted = client.JobsAccepted;
        dto.JobsCompleted = client.JobsCompleted;
        dto.LastJobAt = client.LastJobAt;
        dto.NextJobAt = client.NextJobAt;
        dto.GrossPaidCents = client.GrossPaidCents;
        dto.Regular = client.Regular;
        dto.Contact = client.Contact is null
            ? null
            : new SupplierClientContactDto { Name = client.Contact.Name, Email = client.Contact.Email, Phone = client.Contact.Phone };
        return dto;
    }
}
