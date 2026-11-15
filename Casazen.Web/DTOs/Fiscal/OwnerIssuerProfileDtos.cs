using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// The issuer profile of an owner as the API gives it (<c>GET/PUT api/owners/{ownerId}/issuer-profile</c>, E17/F01/US01/T01). The personal data (the legal name,
/// the fiscal code, the VAT number, the street, the house number, the postal code and the PEC) are in clear only for a caller who may change the profile (the
/// whole organization and <c>agency.owner.financial.manage</c>); any other caller gets them masked (<c>••••1234</c>) and <c>masked: true</c>.
/// </summary>
public sealed class OwnerIssuerProfileDto
{
    public Guid OwnerId { get; init; }

    /// <summary>The profile; absent while the owner has none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ProfileId { get; init; }

    /// <summary>The owner has a profile.</summary>
    public bool Exists { get; init; }

    /// <summary>The profile is complete: CasaZen can issue the documents of the owner's stays on it.</summary>
    public bool CanIssue { get; init; }

    /// <summary>The personal data are masked for this caller.</summary>
    public bool Masked { get; init; }

    /// <summary><c>PrivatePerson</c>, <c>VatOrdinary</c>, <c>VatFlatRate</c>, <c>SwissVatRegistered</c> or <c>SwissNotRegistered</c>; absent until it is said.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Regime { get; init; }

    public string? LegalName { get; init; }

    public string? FiscalCode { get; init; }

    public string? VatNumber { get; init; }

    public string? AddressStreet { get; init; }

    public string? AddressCivic { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public string? Province { get; init; }

    public required string CountryCode { get; init; }

    /// <summary>The regime code of FatturaPA the owner gave; absent = the rule's.</summary>
    public string? FatturaPaRegimeCode { get; init; }

    /// <summary>The regime code that would be used (the profile's own, else the rule's).</summary>
    public string? EffectiveRegimeCode { get; init; }

    /// <summary><c>Standard</c> or <c>ExemptArt10</c> (⚖ I1).</summary>
    public required string StayVatTreatment { get; init; }

    /// <summary>The series pattern of the owner's documents (its sezionale); absent = the default, the year.</summary>
    public string? GuestDocumentSeries { get; init; }

    /// <summary>The default series pattern, shown while the owner has none of its own.</summary>
    public required string DefaultGuestDocumentSeries { get; init; }

    /// <summary>The authorization of the virtual stamp duty or the number of the physical stamp of the owner (⚖ I4).</summary>
    public string? StampDutyReference { get; init; }

    public string? IssuerSdiRecipientCode { get; init; }

    public string? IssuerPec { get; init; }

    public string? SwissVatNumber { get; init; }

    /// <summary>A Swiss owner issues the documents of its stays (⚖ C1).</summary>
    public bool StayIssuedByOwner { get; init; }

    /// <summary>What is still missing before a document is issued on the profile (the field names of the request).</summary>
    public required IReadOnlyList<string> MissingFields { get; init; }

    /// <summary>The tax rule in force today for the owner; absent while no regime is chosen or no rule covers it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaxRuleDto? Rule { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? UpdatedAt { get; init; }

    /// <summary>The DTO of <paramref name="view"/>: the personal data in clear when <paramref name="full"/>, masked otherwise.</summary>
    public static OwnerIssuerProfileDto From(OwnerIssuerProfileView view, bool full)
    {
        ArgumentNullException.ThrowIfNull(view);

        var issuer = view.Issuer;
        return new OwnerIssuerProfileDto
        {
            OwnerId = view.OwnerId,
            ProfileId = view.ProfileId,
            Exists = view.ProfileId is not null,
            CanIssue = view.CanIssue,
            Masked = !full,
            Regime = view.Regime?.ToString(),
            LegalName = Shown(issuer.LegalName, full),
            FiscalCode = Shown(view.FiscalCode, full),
            VatNumber = Shown(view.VatNumber, full),
            AddressStreet = Shown(issuer.AddressStreet, full),
            AddressCivic = Shown(issuer.AddressCivic, full),
            PostalCode = Shown(issuer.PostalCode, full),
            City = issuer.City,
            Province = issuer.Province,
            CountryCode = issuer.CountryCode,
            FatturaPaRegimeCode = issuer.FatturaPaRegimeCode,
            EffectiveRegimeCode = view.EffectiveRegimeCode,
            StayVatTreatment = issuer.StayVatTreatment.ToString(),
            GuestDocumentSeries = issuer.GuestDocumentSeries,
            DefaultGuestDocumentSeries = FiscalNumberFormat.DefaultPattern,
            StampDutyReference = issuer.StampDutyReference,
            IssuerSdiRecipientCode = issuer.IssuerSdiRecipientCode,
            IssuerPec = Shown(issuer.IssuerPec, full),
            SwissVatNumber = issuer.SwissVatNumber,
            StayIssuedByOwner = view.StayIssuedByOwner,
            MissingFields = view.MissingFields,
            Rule = view.Rule is { } rule ? TaxRuleDto.From(rule) : null,
            UpdatedAt = view.UpdatedAt,
        };
    }

    private static string? Shown(string? value, bool full) => full ? value : OwnerFiscalMasking.Mask(value);
}

/// <summary>
/// The answers of the profile of an owner (<c>PUT api/owners/{ownerId}/issuer-profile</c>): the whole profile, what is left out is cleared. A regime or a
/// treatment that is not one of its names is 422 <c>issuer_field_invalid</c> on its field.
/// </summary>
public sealed class SaveOwnerIssuerProfileRequest
{
    /// <summary><c>PrivatePerson</c>, <c>VatOrdinary</c>, <c>VatFlatRate</c> (Italy), <c>SwissVatRegistered</c> or <c>SwissNotRegistered</c> (Switzerland).</summary>
    public string? Regime { get; init; }

    public string? LegalName { get; init; }

    public string? FiscalCode { get; init; }

    public string? VatNumber { get; init; }

    public string? FatturaPaRegimeCode { get; init; }

    public string? AddressStreet { get; init; }

    public string? AddressCivic { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public string? Province { get; init; }

    public string? CountryCode { get; init; }

    /// <summary><c>Standard</c> or <c>ExemptArt10</c>; absent = <c>Standard</c>.</summary>
    public string? StayVatTreatment { get; init; }

    public string? GuestDocumentSeries { get; init; }

    public string? StampDutyReference { get; init; }

    public string? IssuerSdiRecipientCode { get; init; }

    public string? IssuerPec { get; init; }

    public string? SwissVatNumber { get; init; }

    public bool StayIssuedByOwner { get; init; }

    /// <summary>The command of the service.</summary>
    public SaveOwnerIssuerProfileCommand ToCommand()
    {
        var failures = new List<InvoiceDataFailure>();
        var regime = ParseEnum<DocumentRegime>(Regime, IssuerFields.Regime, failures);
        var stay = ParseEnum<StayVatTreatment>(StayVatTreatment, IssuerFields.StayVatTreatment, failures);
        if (failures.Count > 0)
        {
            throw new DomainRuleException(DocumentIssuerErrorCodes.FieldInvalid, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.FieldInvalid))
            {
                Extensions = new Dictionary<string, object?>
                {
                    ["field"] = failures[0].Field,
                    ["errors"] = failures.Select(f => new { field = f.Field, code = f.Code }).ToList(),
                },
            };
        }

        return new SaveOwnerIssuerProfileCommand
        {
            Regime = regime,
            LegalName = LegalName,
            FiscalCode = FiscalCode,
            VatNumber = VatNumber,
            FatturaPaRegimeCode = FatturaPaRegimeCode,
            AddressStreet = AddressStreet,
            AddressCivic = AddressCivic,
            PostalCode = PostalCode,
            City = City,
            Province = Province,
            CountryCode = CountryCode,
            StayVatTreatment = stay,
            GuestDocumentSeries = GuestDocumentSeries,
            StampDutyReference = StampDutyReference,
            IssuerSdiRecipientCode = IssuerSdiRecipientCode,
            IssuerPec = IssuerPec,
            SwissVatNumber = SwissVatNumber,
            StayIssuedByOwner = StayIssuedByOwner,
        };
    }

    /// <summary>The value named by <paramref name="text"/>, <c>null</c> when absent; a name that is not one (or a number) is a failure on <paramref name="field"/>.</summary>
    private static T? ParseEnum<T>(string? text, string field, List<InvoiceDataFailure> failures)
        where T : struct, Enum
    {
        var name = text?.Trim();
        if (string.IsNullOrEmpty(name))
            return null;

        if (!Enum.TryParse<T>(name, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || name.All(char.IsAsciiDigit))
        {
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, field));
            return null;
        }

        return parsed;
    }
}
