using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

/// <summary>A page of <c>GET api/supplier/clients</c> (SP-16a): the customers of the supplier, the one with the latest activity first.</summary>
public class SupplierClientsResponse
{
    public IEnumerable<SupplierClientDto> Items { get; set; } = [];

    /// <summary>How many customers the filter selects, whatever the page.</summary>
    public int Total { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }
}

/// <summary>
/// A customer of the supplier: a host of CasaZen or a private customer of its public showcase, with what they did together. Private data follow the
/// line of the inbox (decision D9): "Nome C." until a job of theirs is taken, the contact only then. <b>Nothing here is exported</b> (decision SP-D25).
/// </summary>
public class SupplierClientDto
{
    /// <summary>
    /// The id the requests carry (<c>clientId</c> of the inbox): <c>GET api/supplier/inbox?clientId=</c> lists this customer's requests, for a host and for a private
    /// customer alike.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary><c>host</c> (a host of CasaZen) or <c>private</c> (a customer of the public showcase).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Where the customer comes from, as in the inbox: <c>casazen</c> or <c>showcase</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The name the supplier may see: the host org's name; for a private customer "Nome C." until a job is taken, then the full name. Null when the
    /// customer was removed by the retention of the personal data (<see cref="Anonymized"/>).
    /// </summary>
    public string? Label { get; set; }

    /// <summary>The private customer was removed: the console says "Cliente rimosso".</summary>
    public bool Anonymized { get; set; }

    /// <summary>Requests the supplier took and that are still jobs (taken, in progress, completed or paid).</summary>
    public int JobsAccepted { get; set; }

    /// <summary>Of those, completed or paid.</summary>
    public int JobsCompleted { get; set; }

    /// <summary>The latest job already begun (in progress, completed or paid); null when none.</summary>
    public DateTime? LastJobAt { get; set; }

    /// <summary>The first request still ahead (waiting for the answer, or taken and not begun); null when none.</summary>
    public DateTime? NextJobAt { get; set; }

    /// <summary>The total of the jobs marked paid, in cents (what the payer paid; a refund does not reduce it: see the earnings).</summary>
    public long GrossPaidCents { get; set; }

    /// <summary>A "regular" customer: at least three jobs taken (the same threshold as the automatic acceptance of regular hosts).</summary>
    public bool Regular { get; set; }

    /// <summary>How to reach the customer: <b>only once a job of theirs was taken</b>; null before, and for a removed customer.</summary>
    public SupplierClientContactDto? Contact { get; set; }
}

/// <summary>How to reach a customer after the take: the host org's name and contact address, or the private customer's name, e-mail and phone.</summary>
public class SupplierClientContactDto
{
    public string? Name { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }
}

/// <summary><c>GET api/supplier/clients/{id}</c>: the customer and its latest six requests.</summary>
public class SupplierClientDetailDto : SupplierClientDto
{
    /// <summary>The latest six requests of the customer that are open or taken, the latest work first (each links to <c>GET api/supplier/inbox/{id}</c>).</summary>
    public IEnumerable<SupplierClientJobDto> RecentJobs { get; set; } = [];
}

/// <summary>One of the latest requests of a customer.</summary>
public class SupplierClientJobDto
{
    public Guid RequestId { get; set; }

    /// <summary>The name of the service when the request was made; null for a request by category only.</summary>
    public string? ServiceName { get; set; }

    public string Category { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    /// <summary>The phase of the console (<c>phase</c> of the inbox: an open set).</summary>
    public string Phase { get; set; } = string.Empty;

    public DateTime? ScheduledStart { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>The price to show: the final total, else the quote, else the estimate; null is "da concordare".</summary>
    public int? AmountCents { get; set; }
}

/// <summary>Maps the supplier's customers to the API.</summary>
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
