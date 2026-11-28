using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The issuer profiles of the owners (NF-116, D12, E17/F01/US01/T01): in Italy the owner of a property is the issuer of the document of the stay (the receipt
/// of a private owner, the FatturaPA of a company with the agency as third-party issuer), and CasaZen issues it on the owner's profile, with the owner's own
/// series (the sezionale) and numbering. A Swiss owner may have one too, used only when the agency chose «emette il proprietario» for it (⚖ C1).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Behind the flag.</b> Every save needs <c>Features:FiscalDocumentIssuance</c> (422 <c>document_issuance_not_available</c>); the endpoints are 404 while
/// it is off.</item>
/// <item><b>Reach.</b> A read is for the owners the caller reaches: the whole org, or an owner whose every ownership is on a property of the caller's reach (an
/// owner without any ownership is in nobody's partial reach); any other owner, or one of another org, is 404 <c>owner_not_found</c>. The web layer decides who
/// may save (<c>agency.owner.financial.manage</c> and the whole organization).</item>
/// <item><b>Privacy.</b> The personal data of the profile are encrypted at rest; the logs carry ids and counts, never a value.</item>
/// </list>
/// </remarks>
public interface IOwnerIssuerProfileService
{
    Task<OwnerIssuerProfileView> GetAsync(HostScope scope, Guid ownerId, CancellationToken ct = default);

    Task<OwnerIssuerProfileView> SaveAsync(Guid orgId, Guid ownerId, SaveOwnerIssuerProfileCommand command, string? userId, CancellationToken ct = default);
}

/// <summary>
/// The answers of the profile of an owner (<c>PUT api/owners/{ownerId}/issuer-profile</c>). A save replaces the profile as a whole: a text that is left out
/// (<c>null</c> or blank) is cleared. The data that does not belong to the country or the regime is dropped: the Italian codes (fiscal code, VAT number, regime
/// code, SDI code, PEC) of a Swiss owner, the UID of an Italian one, the VAT number of a private person.
/// </summary>
public sealed record SaveOwnerIssuerProfileCommand
{
    public DocumentRegime? Regime { get; init; }

    public string? LegalName { get; init; }

    public string? FiscalCode { get; init; }

    /// <summary>The Italian VAT number of a company or of a professional owner (11 digits, with or without <c>IT</c>).</summary>
    public string? VatNumber { get; init; }

    public string? FatturaPaRegimeCode { get; init; }

    public string? AddressStreet { get; init; }

    public string? AddressCivic { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public string? Province { get; init; }

    public string? CountryCode { get; init; }

    public StayVatTreatment? StayVatTreatment { get; init; }

    public string? GuestDocumentSeries { get; init; }

    public string? StampDutyReference { get; init; }

    public string? IssuerSdiRecipientCode { get; init; }

    public string? IssuerPec { get; init; }

    public string? SwissVatNumber { get; init; }

    public bool StayIssuedByOwner { get; init; }
}

/// <summary>The profile of an owner as it is now (<see cref="IOwnerIssuerProfileService.GetAsync"/>).</summary>
/// <param name="OwnerId">The owner.</param>
/// <param name="ProfileId">The profile; <c>null</c> while the owner has none.</param>
/// <param name="Regime">The regime of the owner; <c>null</c> until it is said.</param>
/// <param name="Issuer">The data of the owner as issuer (the same shape as the organization's).</param>
/// <param name="FiscalCode">The Italian fiscal code of the owner.</param>
/// <param name="VatNumber">The VAT number of the owner.</param>
/// <param name="StayIssuedByOwner">A Swiss owner issues the documents of its stays (⚖ C1).</param>
/// <param name="EffectiveRegimeCode">The FatturaPA regime code that would be used: the profile's own, else the rule's.</param>
/// <param name="MissingFields">What the profile still needs before a document is issued on it (<see cref="IssuerFields"/>); empty when complete.</param>
/// <param name="Rule">The tax rule in force today for the country, the regime and the treatments of the owner, or <c>null</c>.</param>
/// <param name="UpdatedAt">When the profile was saved last.</param>
public sealed record OwnerIssuerProfileView(
    Guid OwnerId,
    Guid? ProfileId,
    DocumentRegime? Regime,
    IssuerData Issuer,
    string? FiscalCode,
    string? VatNumber,
    bool StayIssuedByOwner,
    string? EffectiveRegimeCode,
    IReadOnlyList<string> MissingFields,
    FiscalTaxRule? Rule,
    DateTime? UpdatedAt)
{
    public bool CanIssue => ProfileId is not null && MissingFields.Count == 0;
}

/// <summary>
/// What an owner's profile needs before CasaZen issues on it (E17/F01/US01/T01): the same rule as the organization's (<see cref="FiscalDocumentRules.MissingIssuerFields"/>),
/// with the owner's own fiscal code and VAT number in place of the organization's. The one definition of the document plan, of the billing setup and of the API.
/// </summary>
public static class OwnerIssuerProfileRules
{
    public static IReadOnlyList<string> MissingFields(OrgFiscalIssuerProfile profile, string? ruleRegimeCode)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var swiss = FiscalDocumentRules.IsSwiss(profile.CountryCode);
        var effectiveRegimeCode = swiss ? null : Text(profile.FatturaPaRegimeCode) ?? Text(ruleRegimeCode);
        return FiscalDocumentRules.MissingIssuerFields(DataOf(profile), profile.DocumentRegime, effectiveRegimeCode, profile.VatNumber, profile.FiscalCode);
    }

    public static IssuerData DataOf(OrgFiscalIssuerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new IssuerData(
            profile.LegalName,
            profile.AddressStreet,
            profile.AddressCivic,
            profile.PostalCode,
            profile.City,
            profile.Province,
            profile.CountryCode,
            profile.FatturaPaRegimeCode,
            profile.IssuerSdiRecipientCode,
            profile.IssuerPec,
            profile.GuestDocumentSeries)
        {
            VatRegistered = profile.VatRegistered,
            SwissVatNumber = profile.SwissVatNumber,
            Iban = profile.Iban,
            QrIban = profile.QrIban,
            StayVatTreatment = profile.StayVatTreatment,
            CleaningFeeTreatment = profile.CleaningFeeTreatment,
            OwnerInvoiceSeries = profile.OwnerInvoiceSeries,
            AutoIssueGuestDocuments = profile.AutoIssueGuestDocuments,
            EmailGuestDocuments = profile.EmailGuestDocuments,
            PlatformCollectedTouristTaxChannels = profile.PlatformCollectedTouristTaxChannels,
            PlatformCollectedTouristTaxChannelsConfirmed = profile.PlatformCollectedTouristTaxChannelsConfirmed,
            StampDutyReference = profile.StampDutyReference,
            ConservationAgreementConfirmed = profile.ConservationAgreementConfirmed,
            VirtualStampConfirmed = profile.VirtualStampConfirmed,
        };
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
