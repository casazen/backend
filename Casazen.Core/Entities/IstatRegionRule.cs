using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// What the portal of a region wants for the ISTAT communication of its tourist flows (E21/F01/US01/T01, decision O6, NF-44, NF-52): the system, the
/// deadline, the statistical fields of the guest it requires and whether the channel manager covers the region. Platform reference data, the same for
/// every org and not tenant-owned (like <see cref="TouristTaxChannelRule"/>): one row for each region and one for each of the two autonomous provinces
/// of Trentino-Alto Adige, seeded from the matrix of <c>docs/runbooks/istat-flussi-regionali.md</c> and edited only by the platform staff, each change
/// leaving a line in <see cref="RegulatoryDataAuditEntry"/>.
/// </summary>
/// <remarks>
/// <para><b>Nothing is guessed.</b> A value the source does not state stays «da verificare»: <see cref="System"/> and <see cref="PortalUrl"/> null,
/// <see cref="DeadlineKind"/> <see cref="IstatDeadlineKind.Unknown"/>, <see cref="RequiredGuestFields"/> null and <see cref="OctorateCoverage"/>
/// <see cref="IstatCoverage.Unknown"/> (the list of the regions the channel manager covers is not public, D15, so no seeded row says covered). The row is
/// never without its <see cref="SourceUrl"/>, and <see cref="VerifiedAt"/> is the day a person of the staff checked it against that source: the seed leaves
/// it empty.</para>
/// <para><b>No personal data.</b> Nothing here is about a guest, a host or a stay.</para>
/// </remarks>
[Table("IstatRegionRules")]
public class IstatRegionRule
{
    /// <summary>Length of <see cref="RegionIstatCode"/>: the «Codice Regione» of the official list of the comuni.</summary>
    public const int RegionIstatCodeLength = 2;

    /// <summary>Length of <see cref="ProvinceCode"/>: the plate code of the province.</summary>
    public const int ProvinceCodeLength = 2;

    /// <summary>Longest <see cref="RegionName"/>.</summary>
    public const int RegionNameMaxLength = 100;

    /// <summary>Longest <see cref="System"/>.</summary>
    public const int SystemMaxLength = 200;

    /// <summary>Longest <see cref="PortalUrl"/> and <see cref="SourceUrl"/>.</summary>
    public const int UrlMaxLength = 500;

    /// <summary>Longest <see cref="RequiredGuestFields"/>: the three names of <c>IstatGuestFieldLists</c> with their separators.</summary>
    public const int RequiredGuestFieldsMaxLength = 80;

    /// <summary>Longest <see cref="Notes"/>.</summary>
    public const int NotesMaxLength = 1000;

    /// <summary>Longest <see cref="UpdatedByUserId"/>: the one of <c>User.Id</c>.</summary>
    public const int UserIdMaxLength = 255;

    /// <summary>Name of the unique index on a region without a province (the row of a region).</summary>
    public const string RegionIndexName = "UIX_IstatRegionRules_Region";

    /// <summary>Name of the unique index on a region and a province (the rows of the autonomous provinces).</summary>
    public const string RegionProvinceIndexName = "UIX_IstatRegionRules_Region_Province";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ISTAT code of the region, two digits (<c>03</c> Lombardia, <c>04</c> for both autonomous provinces).</summary>
    [Required, MaxLength(RegionIstatCodeLength)]
    public string RegionIstatCode { get; set; } = string.Empty;

    /// <summary>
    /// Plate code of the autonomous province this row is about (<c>BZ</c> Bolzano, <c>TN</c> Trento); null for a region. The two provinces have their
    /// own portal, so Trentino-Alto Adige has no row of its own: a comune of <c>04</c> takes the row of its province.
    /// </summary>
    [MaxLength(ProvinceCodeLength)]
    public string? ProvinceCode { get; set; }

    /// <summary>Name of the region or of the autonomous province, as shown.</summary>
    [Required, MaxLength(RegionNameMaxLength)]
    public string RegionName { get; set; } = string.Empty;

    /// <summary>The system of the portal, free text (<c>ROSS1000</c>, <c>Sinfonia Turismo SMART</c>); null when the source does not name one.</summary>
    [MaxLength(SystemMaxLength)]
    public string? System { get; set; }

    /// <summary>The address (https) of the portal the host uses, or of its official guide; null when the source does not give one.</summary>
    [MaxLength(UrlMaxLength)]
    public string? PortalUrl { get; set; }

    /// <summary>How often the portal wants the movement. Only <see cref="IstatDeadlineKind.Monthly"/> has a <see cref="DeadlineDay"/>.</summary>
    public IstatDeadlineKind DeadlineKind { get; set; }

    /// <summary>The day of the following month the movement of a month is due by (1 to 31); null unless <see cref="DeadlineKind"/> is monthly.</summary>
    public int? DeadlineDay { get; set; }

    /// <summary>
    /// The statistical fields the region requires of every guest, the names of <c>IstatGuestFieldLists</c> separated by a comma in the order of the
    /// lists (<c>TourismType,TravelWay,EducationLevel</c>). Null: not verified, so none is asked. Empty: verified, the region requires none. Never
    /// read by hand: <c>IstatRegionResolver</c> reads and writes it.
    /// </summary>
    [MaxLength(RequiredGuestFieldsMaxLength)]
    public string? RequiredGuestFields { get; set; }

    /// <summary>Whether the channel manager covers the region. Never <see cref="IstatCoverage.Covered"/> unless a person of the staff said so.</summary>
    public IstatCoverage OctorateCoverage { get; set; } = IstatCoverage.Unknown;

    /// <summary>The official page or act the row was read from (https): required, a row never exists without its source.</summary>
    [Required, MaxLength(UrlMaxLength)]
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>When a person of the staff last checked the row against <see cref="SourceUrl"/>; null: never («da verificare»).</summary>
    public DateTime? VerifiedAt { get; set; }

    [MaxLength(NotesMaxLength)]
    public string? Notes { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The staff member who last changed the row (the Auth0 subject); null for the seed.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }

    /// <summary>
    /// The day of the following month the ISTAT communication of a month is due by, when the row says one: only a monthly deadline with a day from
    /// 1 to 31. Null in every other case: a deadline is never guessed.
    /// </summary>
    [NotMapped]
    public int? MonthlyDeadlineDay =>
        DeadlineKind == IstatDeadlineKind.Monthly && DeadlineDay is >= 1 and <= 31 ? DeadlineDay : null;
}
