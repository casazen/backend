using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Owners;

public sealed class PropertyOwnerSummaryDto
{
    public Guid Id { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public OwnerTaxRegime TaxRegime { get; init; }

    public string? IbanMasked { get; init; }

    public DateTime CreatedAt { get; init; }

    public int PropertyCount { get; init; }

    public OwnerEntityKind? EntityKind { get; init; }

    public string? SettlementCurrency { get; init; }

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

public sealed class PropertyOwnerIbanDto
{
    public Guid OwnerId { get; init; }
    public string? Iban { get; init; }
}

public static class PropertyOwnerDtoMapper
{
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
