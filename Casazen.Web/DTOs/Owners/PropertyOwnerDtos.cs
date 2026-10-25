using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Owners;

/// <summary>
/// Property owner row of <c>GET /api/owners</c> (ADR-012): list of owners for the agency.
/// IBAN is masked (last 4 chars only); fiscal code is not included in the list.
/// </summary>
public sealed class PropertyOwnerSummaryDto
{
    public Guid Id { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public OwnerTaxRegime TaxRegime { get; init; }

    /// <summary>IBAN masked as <c>****1234</c> (last 4 chars); null when there is none (ADR-012-R3).</summary>
    public string? IbanMasked { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Full IBAN of a property owner from <c>GET /api/owners/{id}/iban</c> (ADR-012-R3).
/// Requires <c>agency.owner.financial.read</c> permission and is audit-logged.
/// </summary>
public sealed class PropertyOwnerIbanDto
{
    public Guid OwnerId { get; init; }
    public string? Iban { get; init; }
}

public static class PropertyOwnerDtoMapper
{
    /// <summary>Masks IBAN to last 4 characters: <c>IT60X0542811101000000123456</c> becomes <c>****3456</c>.</summary>
    public static string? MaskIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban))
            return null;

        var trimmed = iban.Trim();
        if (trimmed.Length <= 4)
            return "****";

        return "****" + trimmed[^4..];
    }

    public static PropertyOwnerSummaryDto ToSummary(PropertyOwner owner) => new()
    {
        Id = owner.Id,
        FirstName = owner.FirstName,
        LastName = owner.LastName,
        Email = owner.Email,
        Phone = owner.Phone,
        TaxRegime = owner.TaxRegime,
        IbanMasked = MaskIban(owner.Iban),
        CreatedAt = owner.CreatedAt,
    };

    public static PropertyOwnerIbanDto ToIbanDto(PropertyOwner owner) => new()
    {
        OwnerId = owner.Id,
        Iban = owner.Iban,
    };
}
