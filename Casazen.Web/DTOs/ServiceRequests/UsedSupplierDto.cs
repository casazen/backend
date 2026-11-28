using Casazen.Core.Services;

namespace Casazen.Web.DTOs.ServiceRequests;

public sealed class UsedSupplierDto
{
    public Guid SupplierOrgId { get; init; }

    public string SupplierName { get; init; } = string.Empty;

    public int RequestsCount { get; init; }

    public int OpenCount { get; init; }

    public DateTime LastRequestAt { get; init; }

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
