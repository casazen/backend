using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Who issues the fiscal documents of an organization, and with which data (FIS-02, NF-82, PO-05, E06/F02/US02/T01): the choice of the wizard «Come
/// emetti i documenti?» (Octorate, CasaZen for the direct bookings, or nobody here), the regime of the issuer, its legal name and structured address,
/// and the FatturaPA data that goes with them. One row for each organization (unique <see cref="OrgId"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>One source of truth for the fiscal identity.</b> The VAT number and the fiscal code of the issuer stay on <see cref="Org"/>
/// (<c>PartitaIvaNumber</c>, <c>FiscalCode</c>): the profile never copies them.</item>
/// <item><b>Partly filled while the wizard is open.</b> Every text is optional and <see cref="DocumentRegime"/> is empty until the issuer says it;
/// <see cref="ConfirmedAt"/> is set when the wizard is completed. Whether the data is enough to issue is decided by the service, which refuses the
/// mode <see cref="DocumentIssuerMode.CasaZenDirectOnly"/> while it is not (422 <c>issuer_profile_incomplete</c>, with the missing fields).</item>
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
    public const int DefaultSeriesMaxLength = 20;
    public const int UpdatedByUserIdMaxLength = 255;

    /// <summary>The country of an issuer that does not say: Italy.</summary>
    public const string DefaultCountryCode = "IT";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (TN-2): the organization the profile is of. Unique.</summary>
    public Guid OrgId { get; set; }

    /// <summary>Who issues the documents. The safe default is nobody: CasaZen issues only when the organization chose it.</summary>
    public DocumentIssuerMode IssuerMode { get; set; } = DocumentIssuerMode.Accountant;

    /// <summary>The fiscal regime of the issuer; <c>null</c> until it says it. It decides the rules of the documents.</summary>
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

    /// <summary>ISO 3166-1 alpha-2 code of the country of the issuer; <c>IT</c> by default.</summary>
    [Required, MaxLength(CountryCodeMaxLength)]
    public string CountryCode { get; set; } = DefaultCountryCode;

    /// <summary>
    /// The regime code of FatturaPA (<c>RF01</c>, <c>RF19</c>...). It comes from the rule of the regime (<c>FiscalDocumentRulesOptions</c>) and the
    /// accountant can override it here; <c>null</c> = the rule's.
    /// </summary>
    [MaxLength(FatturaPaRegimeCodeMaxLength)]
    public string? FatturaPaRegimeCode { get; set; }

    /// <summary>The code of the issuer's own SDI channel (7 letters or digits, upper case); <c>null</c> when it has none.</summary>
    [MaxLength(SdiRecipientCodeMaxLength)]
    public string? IssuerSdiRecipientCode { get; set; }

    /// <summary>The PEC of the issuer, lower case; <c>null</c> when it has none.</summary>
    [MaxLength(PecMaxLength)]
    public string? IssuerPec { get; set; }

    /// <summary>The series the documents are numbered in by default (<c>FiscalDocumentRulesOptions</c> gives the pattern when this is empty).</summary>
    [MaxLength(DefaultSeriesMaxLength)]
    public string? DefaultSeries { get; set; }

    /// <summary>When the wizard was completed (UTC); <c>null</c> while the profile is a draft.</summary>
    public DateTime? ConfirmedAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The person who saved the profile last.</summary>
    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
