using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// Whether one property has a lock code, as <c>GET /api/properties/access-codes</c> lists it (NF-50, E03/F01/US03/T01): the property, a
/// flag and the date the code was last set. There is no field for the code, nor for anything derived from it, on purpose: the code is
/// released to a guest after a passed verification and nowhere else (ADR-013).
/// </summary>
public sealed class AccessCodeStatusDto
{
    public Guid PropertyId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    /// <summary>True when a lock code was set for the property.</summary>
    public bool AccessCodeSet { get; init; }

    /// <summary>When the code was last set (UTC); <c>null</c> when none is set.</summary>
    public DateTime? UpdatedAt { get; init; }

    public static AccessCodeStatusDto From(AccessCodeStatusItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new AccessCodeStatusDto
        {
            PropertyId = item.PropertyId,
            Name = item.Name,
            City = item.City,
            AccessCodeSet = item.AccessCodeSet,
            UpdatedAt = item.UpdatedAt,
        };
    }
}
