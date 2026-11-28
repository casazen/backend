using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// The billing setup of the organization (<c>GET api/billing-setup</c>, NF-82, NF-104, NF-118, E11/F01/US01/T04): the sections of the wizard «Fatturazione» in
/// the order of its steps, each with what is saved in it and what is still missing. <c>complete</c> is true when something applies and nothing is missing.
/// </summary>
public sealed class BillingSetupDto
{
    public bool Complete { get; init; }

    public required IReadOnlyList<BillingSetupSectionDto> Sections { get; init; }

    /// <summary>
    /// The setup as the API answers it. <paramref name="orgWide"/> says the caller reaches the whole organization: a caller who reaches only some of its properties gets the setup without
    /// the members that sum up all of them (<see cref="BillingSetupSectionDto.OrgWideValues"/>), never the whole organization's properties by the way. It is <b>false by default</b> (a
    /// caller that does not say who it answers is not taken to reach everything): only a caller that checked the reach of the whole organization passes <c>true</c>.
    /// </summary>
    public static BillingSetupDto From(BillingSetup setup, bool orgWide = false)
    {
        ArgumentNullException.ThrowIfNull(setup);
        return new BillingSetupDto { Complete = setup.IsComplete, Sections = [.. setup.Sections.Select(section => BillingSetupSectionDto.From(section, orgWide))] };
    }
}

/// <summary>
/// One section: <c>section</c> (<c>issuer</c>, <c>vat</c>, <c>numbering</c>, <c>touristTax</c>, <c>channels</c>, <c>mandates</c>, <c>automation</c>,
/// <c>italy</c>), <c>applies</c> (false when it has nothing to do for this organization: it has no missing item and the guide does not count it), <c>values</c>
/// (what is saved, by the field names of the section; a value that is not saved is left out) and <c>missing</c> (the items still to do, in the order of the
/// wizard). The IBAN and the QR-IBAN of the issuer are in no section.
/// </summary>
public sealed class BillingSetupSectionDto
{
    public required string Section { get; init; }

    public bool Applies { get; init; }

    public bool Complete { get; init; }

    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    public required IReadOnlyList<BillingSetupItemDto> Missing { get; init; }

    public static IReadOnlySet<string> OrgWideValues { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ItalianTouristTaxSetupKeys.Proposals,
        ItalianTouristTaxSetupKeys.ChannelDataConflict,
        ItalianTouristTaxSetupKeys.ConfirmedComuni,
    };

    public static BillingSetupSectionDto From(BillingSetupSection section, bool orgWide = false)
    {
        ArgumentNullException.ThrowIfNull(section);
        return new BillingSetupSectionDto
        {
            Section = section.Section,
            Applies = section.Applies,
            Complete = section.IsComplete,
            Values = section.Values
                .Where(value => value.Value is not null && (orgWide || !OrgWideValues.Contains(value.Key)))
                .ToDictionary(value => value.Key, value => value.Value),
            Missing = [.. section.Missing.Select(BillingSetupItemDto.From)],
        };
    }
}

public sealed record BillingSetupItemDto(
    string Key,
    string Section,
    string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null)
{
    public static BillingSetupItemDto From(BillingSetupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new BillingSetupItemDto(item.Key, item.Section, item.Reason, item.Count);
    }
}

/// <summary>
/// What a step of the wizard saves (<c>PUT api/billing-setup/{section}</c>): only the fields it sends. A field that is absent or <c>null</c> keeps its saved value
/// (a draft save never clears what the step did not carry); a text sent empty clears it; a list, even an empty one, is sent (an empty
/// <c>platformCollectedTouristTaxChannels</c> confirms that no portal collects the tax). The fields of each section are:
/// <c>issuer</c>: <c>countryCode</c>, <c>legalName</c>, <c>addressStreet</c>, <c>addressCivic</c>, <c>postalCode</c>, <c>city</c>, <c>province</c>,
/// <c>swissVatNumber</c>; <c>vat</c>: <c>regime</c>, <c>vatRegistered</c>, <c>swissVatNumber</c>, <c>fatturaPaRegimeCode</c>, <c>stayVatTreatment</c>,
/// <c>cleaningFeeTreatment</c>; <c>numbering</c>: <c>guestDocumentSeries</c>, <c>ownerInvoiceSeries</c>; <c>touristTax</c>:
/// <c>platformCollectedTouristTaxChannels</c>; <c>automation</c>: <c>autoIssueGuestDocuments</c>, <c>emailGuestDocuments</c>; <c>italy</c>:
/// <c>stampDutyReference</c>, <c>conservationAgreementConfirmed</c>, <c>virtualStampConfirmed</c>. A field of another section is 422
/// <c>billing_setup_field_not_in_section</c>. The Italian VAT number and the fiscal code are not sent: they live on the organization (its tax profile).
/// </summary>
public sealed class SaveBillingSetupSectionRequest
{
    public string? CountryCode { get; init; }

    public string? LegalName { get; init; }

    public string? AddressStreet { get; init; }

    public string? AddressCivic { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public string? Province { get; init; }

    /// <summary>The UID of a registered Swiss issuer (<c>CHE-123.456.789</c>, with or without separators or the suffix <c>MWST</c>/<c>TVA</c>/<c>IVA</c>).</summary>
    public string? SwissVatNumber { get; init; }

    public string? Regime { get; init; }

    /// <summary>
    /// Registered for the VAT. For a Swiss issuer, or an Italian one that is not registered, it tells the regime when none is sent; sent with a regime it must
    /// agree with it (422 on <c>vatRegistered</c>).
    /// </summary>
    public bool? VatRegistered { get; init; }

    public string? FatturaPaRegimeCode { get; init; }

    public string? StayVatTreatment { get; init; }

    public string? CleaningFeeTreatment { get; init; }

    public string? GuestDocumentSeries { get; init; }

    public string? OwnerInvoiceSeries { get; init; }

    public IReadOnlyList<string>? PlatformCollectedTouristTaxChannels { get; init; }

    public bool? AutoIssueGuestDocuments { get; init; }

    public bool? EmailGuestDocuments { get; init; }

    public string? StampDutyReference { get; init; }

    public bool? ConservationAgreementConfirmed { get; init; }

    public bool? VirtualStampConfirmed { get; init; }

    public BillingSetupPayload ToPayload()
    {
        var failures = new List<InvoiceDataFailure>();
        var regime = ParseEnum<DocumentRegime>(Regime, BillingSetupFields.Regime, failures);
        var stay = ParseEnum<StayVatTreatment>(StayVatTreatment, BillingSetupFields.StayVatTreatment, failures);
        var cleaning = ParseEnum<CleaningFeeTreatment>(CleaningFeeTreatment, BillingSetupFields.CleaningFeeTreatment, failures);
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

        return new BillingSetupPayload
        {
            CountryCode = CountryCode,
            LegalName = LegalName,
            AddressStreet = AddressStreet,
            AddressCivic = AddressCivic,
            PostalCode = PostalCode,
            City = City,
            Province = Province,
            SwissVatNumber = SwissVatNumber,
            Regime = regime,
            VatRegistered = VatRegistered,
            FatturaPaRegimeCode = FatturaPaRegimeCode,
            StayVatTreatment = stay,
            CleaningFeeTreatment = cleaning,
            GuestDocumentSeries = GuestDocumentSeries,
            OwnerInvoiceSeries = OwnerInvoiceSeries,
            PlatformCollectedTouristTaxChannels = PlatformCollectedTouristTaxChannels,
            AutoIssueGuestDocuments = AutoIssueGuestDocuments,
            EmailGuestDocuments = EmailGuestDocuments,
            StampDutyReference = StampDutyReference,
            ConservationAgreementConfirmed = ConservationAgreementConfirmed,
            VirtualStampConfirmed = VirtualStampConfirmed,
        };
    }

    private static T? ParseEnum<T>(string? name, string field, List<InvoiceDataFailure> failures)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (EnumNames.TryParseDefined<T>(name, out var parsed))
            return parsed;

        failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, field));
        return null;
    }
}

/// <summary>
/// The portals the organization confirms for one comune each (<c>PUT api/billing-setup/touristTax/portal-comuni</c>, FIX/BE-IT-TOURISTTAX-DEFAULTS): the proposal «Airbnb incassa a Milano» of the
/// setup, confirmed by a person, is <c>{ "comuni": [{ "channel": "Airbnb", "comuneIstat": "015146" }] }</c>. The list is the whole answer: it replaces the one the organization had, an empty one takes
/// every confirmation away, and a request without <c>comuni</c> is 422 (never «every comune»). It counts only for the properties in that comune and never touches
/// <c>platformCollectedTouristTaxChannels</c>, the list of the portals that collect it everywhere.
/// </summary>
public sealed class ConfirmPortalComuniRequest
{
    public IReadOnlyList<PortalComuneRequest>? Comuni { get; init; }

    public IReadOnlyList<PortalComune> ToComuni()
    {
        var comuni = new List<PortalComune>();
        var valid = Comuni is not null;
        foreach (var item in Comuni ?? [])
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Channel) || !EnumNames.TryParseDefined<BookingSource>(item.Channel, out var channel) || string.IsNullOrWhiteSpace(item.ComuneIstat))
            {
                valid = false;
                break;
            }

            comuni.Add(new PortalComune(channel, item.ComuneIstat.Trim()));
        }

        if (valid)
            return comuni;

        throw new DomainRuleException(DocumentIssuerErrorCodes.FieldInvalid, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.FieldInvalid))
        {
            Extensions = new Dictionary<string, object?>
            {
                ["field"] = FieldName,
                ["errors"] = new[] { new { field = FieldName, code = DocumentIssuerErrorCodes.FieldInvalid } },
            },
        };
    }

    private const string FieldName = "portalTouristTaxComuni";
}

public sealed class PortalComuneRequest
{
    public string? Channel { get; init; }

    public string? ComuneIstat { get; init; }
}
