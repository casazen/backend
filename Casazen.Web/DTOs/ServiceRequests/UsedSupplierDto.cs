using Casazen.Core.Services;

namespace Casazen.Web.DTOs.ServiceRequests;

/// <summary>
/// A supplier the host's org already asked for a job, for «Le mie app» of the marketplace (<c>GET api/service-requests/suppliers</c>,
/// MKT, SR-09c): how many requests, how many are not over, when the last was made and for what. The name and nothing else about the
/// supplier: its contact is on its page.
/// </summary>
public sealed class UsedSupplierDto
{
    public Guid SupplierOrgId { get; init; }

    public string SupplierName { get; init; } = string.Empty;

    public int RequestsCount { get; init; }

    /// <summary>The requests that are not over: every one that is not refused, paid or cancelled.</summary>
    public int OpenCount { get; init; }

    public DateTime LastRequestAt { get; init; }

    /// <summary>The category of the latest request: a code of <c>GET api/service-categories</c>.</summary>
    public string LastCategory { get; init; } = string.Empty;

    public static UsedSupplierDto From(HostUsedSupplier supplier) => new()
    {
        SupplierOrgId = supplier.SupplierOrgId,
        SupplierName = supplier.SupplierName,
        RequestsCount = supplier.RequestsCount,
        OpenCount = supplier.OpenCount,
        LastRequestAt = supplier.LastRequestAt,
        LastCategory = supplier.LastCategory,
    };
}
