using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// An issuer of the fiscal documents and its data (FIS-02, NF-82, NF-112, NF-113, NF-116, E11/F01/US01/T01, E17/F01/US01/T01): CasaZen is always the one who
/// issues (decision of 2026-11-14), and the profile says in whose name and with which data, in Italy or in Switzerland: the regime, the legal name and the
/// structured address, the tax ids, the bank accounts, the VAT treatments, the series, the automatic issue and the reminders of the wizard.
/// <list type="bullet">
/// <item><b>The profile of the organization</b> (<see cref="PropertyOwnerId"/> <c>null</c>): one for each organization (<see cref="OrgIndexName"/>), the issuer
/// of everything the agency issues in its own name.</item>
/// <item><b>The profile of an owner who issues</b> (<see cref="PropertyOwnerId"/> set, E17/F01/US01/T01, D12): one for each owner (<see cref="OwnerIndexName"/>).
/// In Italy the owner of the property is the issuer of the document of the stay (a receipt of a private owner, the FatturaPA of a company with the agency as
/// third-party issuer); CasaZen issues it on this profile, with the owner's own series (sezionale) and numbering.</item>
/// </list>
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>One source of truth for the Italian fiscal identity of the organization.</b> The Italian VAT number and the fiscal code of the organization stay on
/// <see cref="Org"/> (<c>PartitaIvaNumber</c>, <c>FiscalCode</c>): its profile never copies them and leaves <see cref="FiscalCode"/> and <see cref="VatNumber"/>
/// empty. An owner is no organization: its profile keeps its own. A Swiss issuer needs neither; its UID is <see cref="SwissVatNumber"/>.</item>
/// <item><b>Partly filled while the wizard is open.</b> Every text is optional and <see cref="DocumentRegime"/> is empty until the issuer says it; what is
/// still missing to issue is computed (<c>FiscalDocumentRules.MissingIssuerFields</c>) and <see cref="ConfirmedAt"/> is set only when the wizard is
/// completed with nothing missing.</item>
/// <item><b>Privacy.</b> The personal data of the issuer (an owner is often a private person) are encrypted at rest (<c>EncryptedColumns</c>): the legal name,
/// the fiscal code, the VAT number, the street, the house number, the postal code, the city and the PEC, and the bank accounts <see cref="Iban"/> and
/// <see cref="QrIban"/>. Those columns have no length; the lengths are checked on input (the constants below).</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org the profile is of (for an owner, the org of the owner), and the row is read
/// and written only there. The readers of the organization's own profile go through <see cref="OrgFiscalIssuerProfileQueries.OfOrganization"/>.</item>
/// </list>
/// </remarks>
[Table("OrgFiscalIssuerProfiles")]
public class OrgFiscalIssuerProfile : ITenantOwned
{
    /// <summary>Name of the unique index on <see cref="OrgId"/> among the profiles of the organizations (<see cref="PropertyOwnerId"/> <c>null</c>): one for each.</summary>
    public const string OrgIndexName = "UIX_OrgFiscalIssuerProfiles_OrgId";

    /// <summary>Name of the unique index on <see cref="PropertyOwnerId"/> where it is set: one profile for each owner who issues.</summary>
    public const string OwnerIndexName = "UIX_OrgFiscalIssuerProfiles_PropertyOwnerId";

    /// <summary>The longest <see cref="FiscalCode"/>: the 16 characters of the code of a person (a company has 11 digits).</summary>
    public const int FiscalCodeMaxLength = 16;

    /// <summary>The longest <see cref="VatNumber"/> as stored (without spaces; the country prefix of a foreign number included).</summary>
    public const int VatNumberMaxLength = 28;

    public const int LegalNameMaxLength = 200;
    public const int AddressStreetMaxLength = 200;
    public const int AddressCivicMaxLength = 20;
    public const int PostalCodeMaxLength = 10;
    public const int CityMaxLength = 100;
    public const int ProvinceMaxLength = 2;
    public const int CountryCodeMaxLength = 2;

    /// <summary>Longest <see cref="FatturaPaRegimeCode"/>: <c>RF01</c>, <c>RF19</c>.</summary>
    public const int FatturaPaRegimeCodeMaxLength = 4;

    /// <summary>Length of <see cref="IssuerSdiRecipientCode"/>: 7 letters or digits.</summary>
    public const int SdiRecipientCodeMaxLength = 7;

    public const int PecMaxLength = 254;

    /// <summary>Length of <see cref="SwissVatNumber"/> as stored: <c>CHE-123.456.789</c>.</summary>
    public const int SwissVatNumberMaxLength = 15;

    /// <summary>The longest IBAN (ISO 13616), checked on input: the encrypted columns have no length.</summary>
    public const int IbanMaxLength = 34;

    /// <summary>The longest series pattern: a series has at most 10 characters (<c>FiscalNumberFormat.SeriesMaxLength</c>).</summary>
    public const int SeriesMaxLength = 10;

    /// <summary>The longest <see cref="StampDutyReference"/>.</summary>
    public const int StampDutyReferenceMaxLength = 60;

    public const int UpdatedByUserIdMaxLength = 255;

    /// <summary>The country of an issuer that does not say: Italy.</summary>
    public const string DefaultCountryCode = "IT";

    /// <summary>The series pattern of the invoices of the property manager to the owners when the profile has none: <c>PM</c> and the year.</summary>
    public const string DefaultOwnerInvoiceSeries = "PM{year}";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (TN-2): the organization the profile is of (for an owner, the agency of the owner). Unique among the profiles of the organizations.</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// The owner whose profile this is (E17/F01/US01/T01); <c>null</c> for the profile of the organization itself. Unique where set (one profile for each
    /// owner); the row goes with its owner (cascade), unless a document was issued on it (a document never loses its issuer: restrict).
    /// </summary>
    public Guid? PropertyOwnerId { get; set; }

    /// <summary>
    /// Who issues the documents: always <see cref="DocumentIssuerMode.CasaZen"/> (decision of 2026-11-14). The retired values are refused by the API and the
    /// migration <c>AddSoleIssuerModel</c> moved every stored row to CasaZen.
    /// </summary>
    public DocumentIssuerMode IssuerMode { get; set; } = DocumentIssuerMode.CasaZen;

    /// <summary>The fiscal regime of the issuer; <c>null</c> until it says it. It must be one of its country (<see cref="CountryCode"/>).</summary>
    public DocumentRegime? DocumentRegime { get; set; }

    /// <summary>The legal name of the issuer: the denomination of the company, or «First Last». <b>Encrypted at rest</b> (at most <see cref="LegalNameMaxLength"/>).</summary>
    public string? LegalName { get; set; }

    /// <summary>
    /// The Italian fiscal code of an owner who issues (16 characters for a person, 11 digits for a company); <c>null</c> for the profile of the organization,
    /// whose code is on <see cref="Org"/>. <b>Encrypted at rest.</b>
    /// </summary>
    public string? FiscalCode { get; set; }

    /// <summary>
    /// The VAT number of an owner who issues (an Italian one is the 11 digits, without the <c>IT</c> prefix); <c>null</c> for the profile of the organization,
    /// whose number is on <see cref="Org"/>, and for a private owner. <b>Encrypted at rest.</b>
    /// </summary>
    public string? VatNumber { get; set; }

    /// <summary>The street of the structured address. <b>Encrypted at rest</b> (at most <see cref="AddressStreetMaxLength"/>).</summary>
    public string? AddressStreet { get; set; }

    /// <summary>The house number. <b>Encrypted at rest</b> (at most <see cref="AddressCivicMaxLength"/>).</summary>
    public string? AddressCivic { get; set; }

    /// <summary>The postal code. <b>Encrypted at rest</b> (at most <see cref="PostalCodeMaxLength"/>).</summary>
    public string? PostalCode { get; set; }

    /// <summary>The city or comune. <b>Encrypted at rest</b> (at most <see cref="CityMaxLength"/>).</summary>
    public string? City { get; set; }

    /// <summary>The two letters of the Italian province; <c>null</c> for an issuer abroad.</summary>
    [MaxLength(ProvinceMaxLength)]
    public string? Province { get; set; }

    /// <summary>ISO 3166-1 alpha-2 code of the country of the issuer: <c>IT</c> (the default) or <c>CH</c> decide the regimes and the tax rules.</summary>
    [Required, MaxLength(CountryCodeMaxLength)]
    public string CountryCode { get; set; } = DefaultCountryCode;

    /// <summary>
    /// The regime code of FatturaPA (<c>RF01</c>, <c>RF19</c>...). It comes from the tax rule of the regime and the accountant can override it here;
    /// <c>null</c> = the rule's. Italian issuers only.
    /// </summary>
    [MaxLength(FatturaPaRegimeCodeMaxLength)]
    public string? FatturaPaRegimeCode { get; set; }

    /// <summary>The code of the issuer's own SDI channel (7 letters or digits, upper case); <c>null</c> when it has none.</summary>
    [MaxLength(SdiRecipientCodeMaxLength)]
    public string? IssuerSdiRecipientCode { get; set; }

    /// <summary>The PEC of the issuer, lower case; <c>null</c> when it has none. <b>Encrypted at rest</b> (at most <see cref="PecMaxLength"/>).</summary>
    public string? IssuerPec { get; set; }

    /// <summary>
    /// The issuer is registered for the VAT. It follows the regime (an Italian VAT number, a Swiss MWST registration): a regime that says otherwise is
    /// refused; without a regime it is what the issuer answered.
    /// </summary>
    public bool VatRegistered { get; set; }

    /// <summary>The Swiss VAT number of a registered Swiss issuer: the UID, stored as <c>CHE-123.456.789</c> (format and check digit verified).</summary>
    [MaxLength(SwissVatNumberMaxLength)]
    public string? SwissVatNumber { get; set; }

    /// <summary>The IBAN the issuer is paid on (mod-97 checked, upper case, no spaces). <b>Encrypted at rest.</b></summary>
    public string? Iban { get; set; }

    /// <summary>The QR-IBAN of a Swiss issuer (an IBAN of CH or LI whose institution id is 30000 to 31999), for the QR-bill. <b>Encrypted at rest.</b></summary>
    public string? QrIban { get; set; }

    /// <summary>How the VAT of the stay is applied (⚖ I1): the rate of the rule, or the exemption of art. 10 (Italy).</summary>
    public StayVatTreatment StayVatTreatment { get; set; } = StayVatTreatment.Standard;

    /// <summary>How the final cleaning is taxed (⚖ C2): with the stay, or as a service.</summary>
    public CleaningFeeTreatment CleaningFeeTreatment { get; set; } = CleaningFeeTreatment.FollowsStay;

    /// <summary>
    /// The series pattern of the documents to the guests (<c>{year}</c> is the year of the issue): at most 10 characters; <c>null</c> = the default pattern,
    /// the year (<c>FiscalNumberFormat.DefaultPattern</c>).
    /// </summary>
    [MaxLength(SeriesMaxLength)]
    public string? GuestDocumentSeries { get; set; }

    /// <summary>The series pattern of the invoices to the owners (E14): at most 10 characters; <c>null</c> = <see cref="DefaultOwnerInvoiceSeries"/>.</summary>
    [MaxLength(SeriesMaxLength)]
    public string? OwnerInvoiceSeries { get; set; }

    /// <summary>
    /// Who holds the accounts of the listings on the portals, for the whole organization (E14/F01/US01/T01, ⚖ I10): the owner by default (V1); a listing
    /// and portal can say otherwise (<see cref="PropertyChannelAccountHolder"/>). Read through <c>IPortalAccountHolderResolver</c>.
    /// </summary>
    public PortalAccountHolder PortalAccountHolder { get; set; } = PortalAccountHolder.Owner;

    /// <summary>
    /// The commissions of the portals whose account the agency holds (V2) are recharged to the owner on the owner invoice, at the VAT of a recharge
    /// (E14/F01/US01/T01, ⚖ I11: the scheme of a mandate without representation). On by default.
    /// </summary>
    public bool RechargePortalCommissions { get; set; } = true;

    /// <summary>The documents to the guests are issued without the confirmation of a person (E12). Off by default.</summary>
    public bool AutoIssueGuestDocuments { get; set; }

    /// <summary>An issued document is sent to the guest by e-mail (E12/F01/US02). Off by default.</summary>
    public bool EmailGuestDocuments { get; set; }

    /// <summary>
    /// The channels whose portal collects the tourist tax itself, as a JSON array of <see cref="BookingSource"/> names (jsonb, column
    /// <c>PlatformCollectedTouristTaxChannels</c>). <c>null</c> = nothing confirmed yet (no channel is listed; for Switzerland the default of the
    /// configuration applies); <c>[]</c> = the issuer confirmed that no portal collects it (E11/F01/US01/T04). Read through
    /// <see cref="PlatformCollectedTouristTaxChannels"/>, confirmed through <see cref="ConfirmPlatformCollectedTouristTaxChannels"/>.
    /// </summary>
    [Column("PlatformCollectedTouristTaxChannels", TypeName = "jsonb")]
    public string? PlatformCollectedTouristTaxChannelsJson { get; set; }

    /// <summary>
    /// The channels whose portal collects the tourist tax itself, so the document of their bookings has no tourist tax line (empty for Italy; for
    /// Switzerland the default comes from the configuration of the tourist tax). Names that are no longer a <see cref="BookingSource"/> are skipped.
    /// Assigning an empty list leaves the choice as it is (never confirmed stays so): to say that none collects it, use
    /// <see cref="ConfirmPlatformCollectedTouristTaxChannels"/>.
    /// </summary>
    [NotMapped]
    public IReadOnlyList<BookingSource> PlatformCollectedTouristTaxChannels
    {
        get => ChannelsOf(PlatformCollectedTouristTaxChannelsJson);
        set => PlatformCollectedTouristTaxChannelsJson = value is { Count: > 0 }
            ? JsonSerializer.Serialize(value.Distinct().Order().Select(source => source.ToString()))
            : null;
    }

    /// <summary>
    /// True once the issuer confirmed the channels whose portal collects the tourist tax, even to say that none does; false while nothing was ever
    /// confirmed (the step «Tassa di soggiorno» of the billing setup, E11/F01/US01/T04). A reader that falls back to the Swiss default of the configuration
    /// does it only while this is false: a confirmed empty list means «no portal collects it», never «use the default».
    /// </summary>
    [NotMapped]
    public bool PlatformCollectedTouristTaxChannelsConfirmed => PlatformCollectedTouristTaxChannelsJson is not null;

    /// <summary>
    /// Confirms the channels whose portal collects the tourist tax itself: the list as it is (without duplicates), an empty one included, which is stored
    /// as <c>[]</c> and says that no portal collects it.
    /// </summary>
    public void ConfirmPlatformCollectedTouristTaxChannels(IEnumerable<BookingSource> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        PlatformCollectedTouristTaxChannelsJson = JsonSerializer.Serialize(channels.Distinct().Order().Select(source => source.ToString()));
    }

    /// <summary>
    /// The authorization number of the virtual stamp duty or the number of the physical stamp (⚖ I4, Italy): printed on the documents with a stamp duty;
    /// <c>null</c> = «imposta di bollo da assolvere».
    /// </summary>
    [MaxLength(StampDutyReferenceMaxLength)]
    public string? StampDutyReference { get; set; }

    /// <summary>
    /// The profile of a Swiss owner: the agency chose «emette il proprietario» for this owner (⚖ C1, direct representation, MWSTG art. 20), so the documents
    /// of the stays of the owner's Swiss properties are issued in the owner's name. Off by default: in Switzerland the agency issues (E11/E12). Not read in
    /// Italy, where the owner of the property always issues the document of the stay.
    /// </summary>
    public bool StayIssuedByOwner { get; set; }

    /// <summary>The issuer confirmed the agreement for the electronic conservation of its documents (Italy, a reminder of the wizard).</summary>
    public bool ConservationAgreementConfirmed { get; set; }

    /// <summary>The issuer confirmed how it pays the stamp duty (virtual authorization or physical stamp; Italy, a reminder of the wizard).</summary>
    public bool VirtualStampConfirmed { get; set; }

    /// <summary>
    /// When the wizard was completed with nothing missing (UTC); <c>null</c> while the profile is a draft. In the billing setup (E11/F01/US01/T04) it is also
    /// the confirmation of the step «Emissione» (the automatic issue has no «not chosen» state): set by saving that step while the profile can issue, and cleared
    /// by the issuer service whenever the profile stops being complete.
    /// </summary>
    public DateTime? ConfirmedAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The person who saved the profile last.</summary>
    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }

    /// <summary>True for the profile of an owner who issues (<see cref="PropertyOwnerId"/> set), false for the profile of the organization.</summary>
    [NotMapped]
    public bool IsOwnerProfile => PropertyOwnerId is not null;

    /// <summary>The channels of a stored JSON array of <see cref="BookingSource"/> names; an unreadable value is no channel.</summary>
    public static IReadOnlyList<BookingSource> ChannelsOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var names = JsonSerializer.Deserialize<string[]>(json) ?? [];
            return names
                .Select(name => Enum.TryParse<BookingSource>(name, ignoreCase: false, out var source) && Enum.IsDefined(source) ? source : (BookingSource?)null)
                .OfType<BookingSource>()
                .Distinct()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// The one way to read the profile of an organization itself (E17/F01/US01/T01): since owners have profiles in the same table, a reader that looked for «the
/// profile of the org» by <c>OrgId</c> alone could take the profile of one of its owners.
/// </summary>
public static class OrgFiscalIssuerProfileQueries
{
    /// <summary>The profile of the organization <paramref name="orgId"/> itself: its <c>OrgId</c> and no owner.</summary>
    public static IQueryable<OrgFiscalIssuerProfile> OfOrganization(this IQueryable<OrgFiscalIssuerProfile> profiles, Guid orgId)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.Where(profile => profile.OrgId == orgId && profile.PropertyOwnerId == null);
    }

    /// <summary>The profiles of organizations only (no owner's), of every org the query reaches.</summary>
    public static IQueryable<OrgFiscalIssuerProfile> OfOrganizations(this IQueryable<OrgFiscalIssuerProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.Where(profile => profile.PropertyOwnerId == null);
    }
}
