using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The issuer of the fiscal documents of an organization and its data (FIS-02, NF-82, NF-112, NF-113, E11/F01/US01/T01): CasaZen is always the issuer
/// (decision of 2026-11-14), so the profile says with which data it issues for the organization, in Italy or in Switzerland: the regime, the legal name and
/// the structured address, the Swiss VAT number, the bank accounts, the VAT treatments, the series, the automatic issue and the reminders of the wizard.
/// One row for each organization (unique <see cref="OrgId"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>One source of truth for the Italian fiscal identity.</b> The Italian VAT number and the fiscal code of the issuer stay on <see cref="Org"/>
/// (<c>PartitaIvaNumber</c>, <c>FiscalCode</c>): the profile never copies them. A Swiss issuer needs neither; its UID is <see cref="SwissVatNumber"/>.</item>
/// <item><b>Partly filled while the wizard is open.</b> Every text is optional and <see cref="DocumentRegime"/> is empty until the issuer says it; what is
/// still missing to issue is computed (<c>FiscalDocumentRules.MissingIssuerFields</c>) and <see cref="ConfirmedAt"/> is set only when the wizard is
/// completed with nothing missing.</item>
/// <item><b>Privacy.</b> <see cref="Iban"/> and <see cref="QrIban"/> are encrypted at rest (<c>EncryptedColumns</c>): the columns have no length, the
/// lengths are checked on input.</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org the profile is of, and the row is read and written only there.</item>
/// </list>
/// </remarks>
[Table("OrgFiscalIssuerProfiles")]
public class OrgFiscalIssuerProfile : ITenantOwned
{
    /// <summary>Name of the unique index on <see cref="OrgId"/>: one profile for each organization.</summary>
    public const string OrgIndexName = "UIX_OrgFiscalIssuerProfiles_OrgId";

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

    /// <summary>Tenant key (TN-2): the organization the profile is of. Unique.</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// Who issues the documents: always <see cref="DocumentIssuerMode.CasaZen"/> (decision of 2026-11-14). The retired values are refused by the API and the
    /// migration <c>AddSoleIssuerModel</c> moved every stored row to CasaZen.
    /// </summary>
    public DocumentIssuerMode IssuerMode { get; set; } = DocumentIssuerMode.CasaZen;

    /// <summary>The fiscal regime of the issuer; <c>null</c> until it says it. It must be one of its country (<see cref="CountryCode"/>).</summary>
    public DocumentRegime? DocumentRegime { get; set; }

    /// <summary>The legal name of the issuer: the denomination of the company, or «First Last».</summary>
    [MaxLength(LegalNameMaxLength)]
    public string? LegalName { get; set; }

    [MaxLength(AddressStreetMaxLength)]
    public string? AddressStreet { get; set; }

    [MaxLength(AddressCivicMaxLength)]
    public string? AddressCivic { get; set; }

    [MaxLength(PostalCodeMaxLength)]
    public string? PostalCode { get; set; }

    [MaxLength(CityMaxLength)]
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

    /// <summary>The PEC of the issuer, lower case; <c>null</c> when it has none.</summary>
    [MaxLength(PecMaxLength)]
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

    /// <summary>The documents to the guests are issued without the confirmation of a person (E12). Off by default.</summary>
    public bool AutoIssueGuestDocuments { get; set; }

    /// <summary>An issued document is sent to the guest by e-mail (E12/F01/US02). Off by default.</summary>
    public bool EmailGuestDocuments { get; set; }

    /// <summary>
    /// The channels whose portal collects the tourist tax itself, as a JSON array of <see cref="BookingSource"/> names (jsonb, column
    /// <c>PlatformCollectedTouristTaxChannels</c>); <c>null</c> = none. Read and written through <see cref="PlatformCollectedTouristTaxChannels"/>.
    /// </summary>
    [Column("PlatformCollectedTouristTaxChannels", TypeName = "jsonb")]
    public string? PlatformCollectedTouristTaxChannelsJson { get; set; }

    /// <summary>
    /// The channels whose portal collects the tourist tax itself, so the document of their bookings has no tourist tax line (empty for Italy; for
    /// Switzerland the default comes from the configuration of the tourist tax). Names that are no longer a <see cref="BookingSource"/> are skipped.
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
    /// The authorization number of the virtual stamp duty or the number of the physical stamp (⚖ I4, Italy): printed on the documents with a stamp duty;
    /// <c>null</c> = «imposta di bollo da assolvere».
    /// </summary>
    [MaxLength(StampDutyReferenceMaxLength)]
    public string? StampDutyReference { get; set; }

    /// <summary>The issuer confirmed the agreement for the electronic conservation of its documents (Italy, a reminder of the wizard).</summary>
    public bool ConservationAgreementConfirmed { get; set; }

    /// <summary>The issuer confirmed how it pays the stamp duty (virtual authorization or physical stamp; Italy, a reminder of the wizard).</summary>
    public bool VirtualStampConfirmed { get; set; }

    /// <summary>When the wizard was completed with nothing missing (UTC); <c>null</c> while the profile is a draft.</summary>
    public DateTime? ConfirmedAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The person who saved the profile last.</summary>
    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }

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
