using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Owners;

/// <summary>
/// Property owner row of <c>GET /api/owners</c> (ADR-012): list of owners for the agency.
/// IBAN is masked (last 4 chars only); fiscal code is not included in the list. Besides the person it carries what the screen «Proprietari» needs to draw a row without
/// reading anything else (FIX/RES-OWNERS): the number of properties, the kind and the settlement currency of the fiscal profile in force, and the mandate in force today.
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

    /// <summary>The properties the owner holds today or will hold (an ownership not ended, on a property that still exists), each counted once.</summary>
    public int PropertyCount { get; init; }

    /// <summary>
    /// <c>NaturalPerson</c> (a private person) or <c>LegalEntity</c> (a company), from the fiscal profile in force today (the newest when none is in force, as the page of the
    /// owner reads it); <c>null</c> for an owner with no profile.
    /// </summary>
    public OwnerEntityKind? EntityKind { get; init; }

    /// <summary>The settlement currency (ISO 4217) of that profile; <c>null</c> when it has none or the owner has no profile.</summary>
    public string? SettlementCurrency { get; init; }

    /// <summary>The mandate of the owner in force today; <c>null</c> when there is none.</summary>
    public OwnerMandateInForceDto? MandateInForce { get; init; }
}

/// <summary>The mandate in force of an owner as the list shows it: the commission and what it is a percentage of, whether the VAT is in it, and its days.</summary>
public sealed record OwnerMandateInForceDto(
    Guid Id,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    decimal CommissionPercent,
    CommissionBase CommissionBase,
    bool CommissionIncludesVat);

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

    public static PropertyOwnerSummaryDto ToSummary(OwnerDirectoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new()
        {
            Id = row.Id,
            FirstName = row.FirstName,
            LastName = row.LastName,
            Email = row.Email,
            Phone = row.Phone,
            TaxRegime = row.TaxRegime,
            IbanMasked = MaskIban(row.Iban),
            CreatedAt = row.CreatedAt,
            PropertyCount = row.PropertyCount,
            EntityKind = row.EntityKind,
            SettlementCurrency = row.SettlementCurrency,
            MandateInForce = row.MandateInForce is { } mandate
                ? new OwnerMandateInForceDto(mandate.Id, mandate.ValidFrom, mandate.ValidTo, mandate.CommissionPercent, mandate.CommissionBase, mandate.CommissionIncludesVat)
                : null,
        };
    }

    public static PropertyOwnerIbanDto ToIbanDto(PropertyOwner owner) => new()
    {
        OwnerId = owner.Id,
        Iban = owner.Iban,
    };
}
