using Casazen.Core.Entities;

namespace Casazen.Core.DTOs;

/// <summary>
/// Who runs the booking site, as its footer shows it (DB-03b-a): the legal name, the registered office and the VAT number of the
/// org (the transparency duty of the provider of an information-society service, to be confirmed by the lawyer before the
/// production release). Only what the host entered is here: a member is <c>null</c> when it is empty, never an empty string, and
/// <see cref="PublicOrgDto.Operator"/> itself is <c>null</c> when none of the three is there. This endpoint is anonymous, so
/// nothing else of the org's fiscal profile (the codice fiscale, the retention date) is ever read into it.
/// </summary>
public class PublicOperatorDto
{
    /// <summary>The company name or the name of the person who carries on the activity (<c>Org.LegalName</c>), or null.</summary>
    public string? LegalName { get; set; }

    /// <summary>
    /// The VAT number (partita IVA, 11 digits) of the fiscal profile of the org, only while the org declares it has one
    /// (<c>Org.HasPartitaIva</c>); otherwise null.
    /// </summary>
    public string? VatNumber { get; set; }

    /// <summary>The registered office ("sede legale") in one line (<c>Org.RegisteredOffice</c>), or null.</summary>
    public string? RegisteredOffice { get; set; }

    /// <summary>The operator data of <paramref name="org"/>, or <c>null</c> when it has none to show.</summary>
    public static PublicOperatorDto? FromOrg(Org org)
    {
        var legalName = NullIfBlank(org.LegalName);
        var vatNumber = org.HasPartitaIva ? NullIfBlank(org.PartitaIvaNumber) : null;
        var registeredOffice = NullIfBlank(org.RegisteredOffice);
        if (legalName is null && vatNumber is null && registeredOffice is null)
            return null;

        return new PublicOperatorDto { LegalName = legalName, VatNumber = vatNumber, RegisteredOffice = registeredOffice };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
