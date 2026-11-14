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
    /// <summary>«Chi emette»: the country, the legal name, the structured address and the tax ids (the Italian ones are those of the organization, the UID is the Swiss one).</summary>
    public const string Issuer = "issuer";

    /// <summary>«IVA»: the regime (and with it the VAT registration), the FatturaPA regime code, the VAT of the stay and of the cleaning.</summary>
    public const string Vat = "vat";

    /// <summary>«Numerazione»: the series of the documents to the guests and of the invoices to the owners.</summary>
    public const string Numbering = "numbering";

    /// <summary>«Tassa di soggiorno»: for a Swiss issuer, which portals collect the tourist tax themselves.</summary>
    public const string TouristTax = "touristTax";

    /// <summary>«Commissioni dei canali»: the commissions of the portals (agencies; added by the epic of the owner ledger, E13/E14).</summary>
    public const string Channels = "channels";

    /// <summary>«Mandato standard»: the mandate of the owners (agencies; added by the epic of the owner ledger, E13).</summary>
    public const string Mandates = "mandates";

    /// <summary>«Emissione»: automatic issue and e-mail to the guest.</summary>
    public const string Automation = "automation";

    /// <summary>«Italia»: for an Italian issuer, the reminders of the electronic conservation and of the stamp duty (and, with E17, the owner issuers and the SDI).</summary>
    public const string Italy = "italy";

    /// <summary>The eight sections in the order of the wizard: the first section that has a missing item is where the guide reopens it.</summary>
    public static IReadOnlyList<string> All { get; } = [Issuer, Vat, Numbering, TouristTax, Channels, Mandates, Automation, Italy];

    /// <summary>
    /// The sections <c>PUT api/billing-setup/{section}</c> saves: the ones kept on the profile of the issuer. <see cref="Channels"/> and
    /// <see cref="Mandates"/> are saved by the endpoints of the epic that owns them.
    /// </summary>
    public static IReadOnlyList<string> Writable { get; } = [Issuer, Vat, Numbering, TouristTax, Automation, Italy];

    /// <summary>The section named by <paramref name="name"/> (once trimmed, without regard to case), as the constant spells it; <c>null</c> for anything else.</summary>
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
    // ── issuer ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The organization has saved nothing yet: the issuer has not said which country it issues from (reason <see cref="BillingSetupReasons.Required"/>).</summary>
    public const string CountryMissing = "country_missing";

    /// <summary>The legal name of the issuer is missing.</summary>
    public const string LegalNameMissing = "legal_name_missing";

    /// <summary>The structured address is incomplete: the street, the postal code, the city and, in Italy, the province.</summary>
    public const string AddressMissing = "address_missing";

    /// <summary>Italy, a regime with a VAT number: the organization has none on its tax profile (the tax profile is where it is saved).</summary>
    public const string VatNumberMissing = "vat_number_missing";

    /// <summary>Italy, a private person: the organization has no fiscal code on its tax profile.</summary>
    public const string FiscalCodeMissing = "fiscal_code_missing";

    /// <summary>Switzerland, registered for the VAT: the UID number of the issuer is missing.</summary>
    public const string SwissUidMissing = "swiss_uid_missing";

    // ── vat ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The regime of the issuer is not chosen, and with it neither the VAT registration nor the rule of the documents.</summary>
    public const string RegimeMissing = "regime_missing";

    /// <summary>Italy, a regime with a VAT number: no FatturaPA regime code (the rule of the regime has none and the issuer gave none).</summary>
    public const string RegimeCodeMissing = "regime_code_missing";

    /// <summary>
    /// The regime is chosen and no tax rule of the catalog (<c>FiscalDocuments:TaxRules</c>) is in force today for it: nothing is issued with a rate nobody
    /// approved (reason <see cref="BillingSetupReasons.Unavailable"/>: the administrator of the installation adds the rule).
    /// </summary>
    public const string TaxRuleMissing = "tax_rule_missing";

    // ── numbering ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The series of the documents to the guests was never saved: the default is shown, and saved with the step.</summary>
    public const string GuestSeriesUnconfirmed = "guest_series_unconfirmed";

    /// <summary>The series of the invoices to the owners was never saved.</summary>
    public const string OwnerSeriesUnconfirmed = "owner_series_unconfirmed";

    // ── touristTax ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Switzerland: the portals that collect the tourist tax themselves were never confirmed, not even to say that none does.</summary>
    public const string TouristTaxChannelsUnconfirmed = "tourist_tax_channels_unconfirmed";

    // ── automation ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The choice of the automatic issue (yes or no, and the e-mail to the guest) was never confirmed. It is confirmed by saving the step while the profile of
    /// the issuer is complete; a profile that stops being complete asks for the confirmation again.
    /// </summary>
    public const string AutoIssueUnconfirmed = "auto_issue_unconfirmed";

    // ── italy ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Italy: the free agreement for the electronic conservation of the documents was not confirmed.</summary>
    public const string ConservationAgreementUnconfirmed = "conservation_agreement_unconfirmed";

    /// <summary>Italy: neither the reference of the stamp duty (the authorization of the virtual stamp or the number of the physical one) nor the confirmation of the virtual stamp was given.</summary>
    public const string StampDutyUnconfirmed = "stamp_duty_unconfirmed";
}

/// <summary>Why a billing setup item is missing (<see cref="BillingSetupItem.Reason"/>): a small set of stable codes the client translates, never renamed.</summary>
public static class BillingSetupReasons
{
    /// <summary>A value the issuer has to give is absent.</summary>
    public const string Required = "required";

    /// <summary>The issuer has not confirmed a choice yet (a default is shown, but nobody said it was right).</summary>
    public const string Unconfirmed = "unconfirmed";

    /// <summary>The user cannot fix it: something of the installation is missing (a tax rule of the catalog).</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// The names of the fields of a section as the API says them (camel case), in the answer of <c>GET api/billing-setup</c> (the <c>values</c> of a section) and
/// in the body of <c>PUT api/billing-setup/{section}</c>, and in the field codes of a refusal. The names of the issuer are those of
/// <see cref="IssuerFields"/>.
/// </summary>
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
    /// <summary>True when nothing is missing in the section.</summary>
    public bool IsComplete => Missing.Count == 0;
}

/// <summary>The billing setup of an organization (<c>GET api/billing-setup</c>): every section with its values and its missing items.</summary>
/// <param name="Sections">The sections in the order of <see cref="BillingSetupSections.All"/> (the ones of an epic that added its own after them).</param>
public sealed record BillingSetup(IReadOnlyList<BillingSetupSection> Sections)
{
    /// <summary>Every missing item, in the order of the sections and of the wizard.</summary>
    public IReadOnlyList<BillingSetupItem> Missing => [.. Sections.SelectMany(section => section.Missing)];

    /// <summary>True when some section applies and no item is missing in any section (a setup nobody provides is never complete).</summary>
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
    /// <summary>The first missing item (where the guide reopens the wizard); <c>null</c> when nothing is missing.</summary>
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

    /// <summary>The names of the <see cref="BookingSource"/> whose portal collects the tourist tax; an empty list confirms that none does.</summary>
    public IReadOnlyList<string>? PlatformCollectedTouristTaxChannels { get; init; }

    public bool? AutoIssueGuestDocuments { get; init; }

    public bool? EmailGuestDocuments { get; init; }

    public string? StampDutyReference { get; init; }

    public bool? ConservationAgreementConfirmed { get; init; }

    public bool? VirtualStampConfirmed { get; init; }

    /// <summary>The names (<see cref="BillingSetupFields"/>) of the fields this payload carries, in the order of the fields.</summary>
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
