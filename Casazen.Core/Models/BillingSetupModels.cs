using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Core.Models;

/// <summary>
/// The sections of the billing setup (NF-82, NF-104, NF-118, E11/F01/US01/T04): the wizard «Fatturazione» of the onboarding guide, one section for each of
/// its steps, in the order of the steps. The names are the ones the API speaks (<c>GET api/billing-setup</c>, <c>PUT api/billing-setup/{section}</c>) and
/// are never renamed; the wizard of the client maps every one of them, and every item key of <see cref="BillingSetupItemKeys"/>, to its step.
/// </summary>
public static class BillingSetupSections
{
    public const string Issuer = "issuer";

    /// <summary>«IVA»: the regime (and with it the VAT registration), the FatturaPA regime code, the VAT of the stay and of the cleaning.</summary>
    public const string Vat = "vat";

    public const string Numbering = "numbering";

    /// <summary>«Tassa di soggiorno»: for a Swiss issuer, which portals collect the tourist tax themselves.</summary>
    public const string TouristTax = "touristTax";

    public const string Channels = "channels";

    public const string Mandates = "mandates";

    public const string Automation = "automation";

    /// <summary>«Italia»: for an Italian issuer, the reminders of the electronic conservation and of the stamp duty (and, with E17, the owner issuers and the SDI).</summary>
    public const string Italy = "italy";

    public static IReadOnlyList<string> All { get; } = [Issuer, Vat, Numbering, TouristTax, Channels, Mandates, Automation, Italy];

    public static IReadOnlyList<string> Writable { get; } = [Issuer, Vat, Numbering, TouristTax, Automation, Italy];

    public static string? Of(string? name) =>
        All.FirstOrDefault(section => string.Equals(section, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The names of the fields <c>PUT api/billing-setup/{section}</c> accepts for <paramref name="section"/> (<see cref="BillingSetupFields"/>); empty for a
    /// section this service does not save. A field of another section is refused (422 <c>billing_setup_field_not_in_section</c>), except the Swiss UID, which
    /// is a tax id of the issuer and the VAT registration of the regime, so both steps take it.
    /// </summary>
    public static IReadOnlyList<string> FieldsOf(string? section) => Of(section) switch
    {
        Issuer =>
        [
            BillingSetupFields.CountryCode,
            BillingSetupFields.LegalName,
            BillingSetupFields.AddressStreet,
            BillingSetupFields.AddressCivic,
            BillingSetupFields.PostalCode,
            BillingSetupFields.City,
            BillingSetupFields.Province,
            BillingSetupFields.SwissVatNumber,
        ],
        Vat =>
        [
            BillingSetupFields.Regime,
            BillingSetupFields.VatRegistered,
            BillingSetupFields.SwissVatNumber,
            BillingSetupFields.FatturaPaRegimeCode,
            BillingSetupFields.StayVatTreatment,
            BillingSetupFields.CleaningFeeTreatment,
        ],
        Numbering => [BillingSetupFields.GuestDocumentSeries, BillingSetupFields.OwnerInvoiceSeries],
        TouristTax => [BillingSetupFields.PlatformCollectedTouristTaxChannels],
        Automation => [BillingSetupFields.AutoIssueGuestDocuments, BillingSetupFields.EmailGuestDocuments],
        Italy => [BillingSetupFields.StampDutyReference, BillingSetupFields.ConservationAgreementConfirmed, BillingSetupFields.VirtualStampConfirmed],
        _ => [],
    };
}

/// <summary>
/// The keys of the items that can be missing in the billing setup (E11/F01/US01/T04): stable codes, never renamed, spelled <c>snake_case</c>. The key of the
/// first missing item is the reason of the part «Fatturazione» of the guide, and the client opens the wizard at the step the key belongs to. The epics that
/// add their own contributors add their own keys here or next to their contributor (<c>channel_commissions_unconfirmed</c> and <c>owners_without_mandate</c>
/// of the owner ledger, <c>owner_issuer_missing</c> of the Italian owner issuers).
/// </summary>
public static class BillingSetupItemKeys
{
    public const string CountryMissing = "country_missing";

    public const string LegalNameMissing = "legal_name_missing";

    public const string AddressMissing = "address_missing";

    /// <summary>Italy, a regime with a VAT number: the organization has none on its tax profile (the tax profile is where it is saved).</summary>
    public const string VatNumberMissing = "vat_number_missing";

    public const string FiscalCodeMissing = "fiscal_code_missing";

    /// <summary>Switzerland, registered for the VAT: the UID number of the issuer is missing.</summary>
    public const string SwissUidMissing = "swiss_uid_missing";

    /// <summary>The regime of the issuer is not chosen, and with it neither the VAT registration nor the rule of the documents.</summary>
    public const string RegimeMissing = "regime_missing";

    /// <summary>Italy, a regime with a VAT number: no FatturaPA regime code (the rule of the regime has none and the issuer gave none).</summary>
    public const string RegimeCodeMissing = "regime_code_missing";

    public const string TaxRuleMissing = "tax_rule_missing";

    public const string GuestSeriesUnconfirmed = "guest_series_unconfirmed";

    public const string OwnerSeriesUnconfirmed = "owner_series_unconfirmed";

    public const string TouristTaxChannelsUnconfirmed = "tourist_tax_channels_unconfirmed";

    public const string AutoIssueUnconfirmed = "auto_issue_unconfirmed";

    public const string ConservationAgreementUnconfirmed = "conservation_agreement_unconfirmed";

    public const string StampDutyUnconfirmed = "stamp_duty_unconfirmed";

    public const string OwnerIssuerMissing = "owner_issuer_missing";

    /// <summary>
    /// Italy, the SDI channel is connected (E17/F01/US02/T02): the provider refused the submission of the electronic invoices of some issuers because their VAT number is not
    /// enabled at the provider yet (reason <see cref="BillingSetupReasons.Pending"/>, with the count of the issuers). It clears by itself when a later submission of the issuer is
    /// accepted. Nothing is asked of the user: the registration is CasaZen's, and the documents wait as «to send» (or are declared sent by hand).
    /// </summary>
    public const string SdiIssuerRegistrationPending = "sdi_issuer_registration_pending";
}

public static class BillingSetupReasons
{
    public const string Required = "required";

    public const string Unconfirmed = "unconfirmed";

    public const string Unavailable = "unavailable";

    /// <summary>Somebody else has to do something and the user waits (the enabling of a VAT number at the SDI channel, E17/F01/US02/T02).</summary>
    public const string Pending = "pending";
}

public static class BillingSetupFields
{
    public const string CountryCode = IssuerFields.CountryCode;
    public const string LegalName = IssuerFields.LegalName;
    public const string AddressStreet = IssuerFields.AddressStreet;
    public const string AddressCivic = IssuerFields.AddressCivic;
    public const string PostalCode = IssuerFields.PostalCode;
    public const string City = IssuerFields.City;
    public const string Province = IssuerFields.Province;
    public const string SwissVatNumber = IssuerFields.SwissVatNumber;
    public const string Regime = IssuerFields.Regime;
    public const string VatRegistered = IssuerFields.VatRegistered;
    public const string FatturaPaRegimeCode = IssuerFields.FatturaPaRegimeCode;
    public const string StayVatTreatment = IssuerFields.StayVatTreatment;
    public const string CleaningFeeTreatment = IssuerFields.CleaningFeeTreatment;
    public const string GuestDocumentSeries = IssuerFields.GuestDocumentSeries;
    public const string OwnerInvoiceSeries = IssuerFields.OwnerInvoiceSeries;
    public const string PlatformCollectedTouristTaxChannels = IssuerFields.PlatformCollectedTouristTaxChannels;
    public const string AutoIssueGuestDocuments = "autoIssueGuestDocuments";
    public const string EmailGuestDocuments = "emailGuestDocuments";
    public const string StampDutyReference = IssuerFields.StampDutyReference;
    public const string ConservationAgreementConfirmed = "conservationAgreementConfirmed";
    public const string VirtualStampConfirmed = "virtualStampConfirmed";
}

/// <summary>
/// One thing still to do in the billing setup (E11/F01/US01/T04): what is missing, the step it belongs to and why. Codes and counts only, no personal data.
/// </summary>
/// <param name="Key">One of <see cref="BillingSetupItemKeys"/> (or the key an epic added): the client maps it to its wizard step and its text.</param>
/// <param name="Section">One of <see cref="BillingSetupSections"/>.</param>
/// <param name="Reason">One of <see cref="BillingSetupReasons"/>.</param>
/// <param name="Count">How many things are missing when the item counts something (the owners without a mandate); <c>null</c> otherwise.</param>
public sealed record BillingSetupItem(string Key, string Section, string Reason, int? Count = null);

/// <summary>One section of the billing setup: its values, and what is still missing in it.</summary>
/// <param name="Section">One of <see cref="BillingSetupSections"/>.</param>
/// <param name="Applies">
/// False when the section has nothing to do for this organization (the Swiss tourist tax of an Italian issuer, the Italian reminders of a Swiss one, a section no
/// epic provides yet): it has no missing item and the part «Fatturazione» does not count it.
/// </param>
/// <param name="Values">What is saved in the section, by the field names of <see cref="BillingSetupFields"/> (a value that is not saved is <c>null</c>).</param>
/// <param name="Missing">The items still missing, in the order the wizard asks for them.</param>
public sealed record BillingSetupSection(
    string Section,
    bool Applies,
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<BillingSetupItem> Missing)
{
    public bool IsComplete => Missing.Count == 0;
}

public sealed record BillingSetup(IReadOnlyList<BillingSetupSection> Sections)
{
    public IReadOnlyList<BillingSetupItem> Missing => [.. Sections.SelectMany(section => section.Missing)];

    public bool IsComplete => Sections.Any(section => section.Applies) && Sections.All(section => section.IsComplete);
}

/// <summary>
/// How much of the billing setup is done (what the part «Fatturazione» of the guide and the flow «Documents» read): the sections that apply to the
/// organization, how many of them have nothing missing, and the missing items.
/// </summary>
/// <param name="Done">The sections that apply and have no missing item.</param>
/// <param name="Total">The sections that apply.</param>
/// <param name="Missing">The missing items of the sections that were asked, in the order of the wizard.</param>
public sealed record BillingSetupProgress(int Done, int Total, IReadOnlyList<BillingSetupItem> Missing)
{
    public BillingSetupItem? FirstMissing => Missing.Count > 0 ? Missing[0] : null;

    /// <summary>True when at least one section applies and no item is missing: the only way the part «Fatturazione» is done (nothing provided is never done).</summary>
    public bool IsComplete => Total > 0 && Missing.Count == 0;
}

/// <summary>
/// What the wizard saves in a section (<c>PUT api/billing-setup/{section}</c>): only the fields it sends. A field that is <c>null</c> is not sent and the saved
/// value stays (a draft save of the step never clears what the step did not carry); a text that is empty is sent and clears the value; a list, even an empty
/// one, is sent. The field names are <see cref="BillingSetupFields"/>; the fields of a section are <see cref="BillingSetupSections.FieldsOf"/>.
/// </summary>
public sealed record BillingSetupPayload
{
    public string? CountryCode { get; init; }

    public string? LegalName { get; init; }

    public string? AddressStreet { get; init; }

    public string? AddressCivic { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public string? Province { get; init; }

    public string? SwissVatNumber { get; init; }

    public DocumentRegime? Regime { get; init; }

    public bool? VatRegistered { get; init; }

    public string? FatturaPaRegimeCode { get; init; }

    public StayVatTreatment? StayVatTreatment { get; init; }

    public CleaningFeeTreatment? CleaningFeeTreatment { get; init; }

    public string? GuestDocumentSeries { get; init; }

    public string? OwnerInvoiceSeries { get; init; }

    public IReadOnlyList<string>? PlatformCollectedTouristTaxChannels { get; init; }

    public bool? AutoIssueGuestDocuments { get; init; }

    public bool? EmailGuestDocuments { get; init; }

    public string? StampDutyReference { get; init; }

    public bool? ConservationAgreementConfirmed { get; init; }

    public bool? VirtualStampConfirmed { get; init; }

    public IReadOnlyList<string> ProvidedFields()
    {
        var provided = new List<string>();
        Add(provided, CountryCode is not null, BillingSetupFields.CountryCode);
        Add(provided, LegalName is not null, BillingSetupFields.LegalName);
        Add(provided, AddressStreet is not null, BillingSetupFields.AddressStreet);
        Add(provided, AddressCivic is not null, BillingSetupFields.AddressCivic);
        Add(provided, PostalCode is not null, BillingSetupFields.PostalCode);
        Add(provided, City is not null, BillingSetupFields.City);
        Add(provided, Province is not null, BillingSetupFields.Province);
        Add(provided, SwissVatNumber is not null, BillingSetupFields.SwissVatNumber);
        Add(provided, Regime is not null, BillingSetupFields.Regime);
        Add(provided, VatRegistered is not null, BillingSetupFields.VatRegistered);
        Add(provided, FatturaPaRegimeCode is not null, BillingSetupFields.FatturaPaRegimeCode);
        Add(provided, StayVatTreatment is not null, BillingSetupFields.StayVatTreatment);
        Add(provided, CleaningFeeTreatment is not null, BillingSetupFields.CleaningFeeTreatment);
        Add(provided, GuestDocumentSeries is not null, BillingSetupFields.GuestDocumentSeries);
        Add(provided, OwnerInvoiceSeries is not null, BillingSetupFields.OwnerInvoiceSeries);
        Add(provided, PlatformCollectedTouristTaxChannels is not null, BillingSetupFields.PlatformCollectedTouristTaxChannels);
        Add(provided, AutoIssueGuestDocuments is not null, BillingSetupFields.AutoIssueGuestDocuments);
        Add(provided, EmailGuestDocuments is not null, BillingSetupFields.EmailGuestDocuments);
        Add(provided, StampDutyReference is not null, BillingSetupFields.StampDutyReference);
        Add(provided, ConservationAgreementConfirmed is not null, BillingSetupFields.ConservationAgreementConfirmed);
        Add(provided, VirtualStampConfirmed is not null, BillingSetupFields.VirtualStampConfirmed);
        return provided;
    }

    private static void Add(List<string> provided, bool carried, string field)
    {
        if (carried)
            provided.Add(field);
    }
}
