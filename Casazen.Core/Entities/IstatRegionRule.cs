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
    public const int RegionIstatCodeLength = 2;

    public const int ProvinceCodeLength = 2;

    public const int RegionNameMaxLength = 100;

    public const int SystemMaxLength = 200;

    public const int UrlMaxLength = 500;

    public const int RequiredGuestFieldsMaxLength = 80;

    public const int NotesMaxLength = 1000;

    public const int UserIdMaxLength = 255;

    public const string RegionIndexName = "UIX_IstatRegionRules_Region";

    public const string RegionProvinceIndexName = "UIX_IstatRegionRules_Region_Province";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ISTAT code of the region, two digits (<c>03</c> Lombardia, <c>04</c> for both autonomous provinces).</summary>
    [Required, MaxLength(RegionIstatCodeLength)]
    public string RegionIstatCode { get; set; } = string.Empty;

    [MaxLength(ProvinceCodeLength)]
    public string? ProvinceCode { get; set; }

    [Required, MaxLength(RegionNameMaxLength)]
    public string RegionName { get; set; } = string.Empty;

    /// <summary>The system of the portal, free text (<c>ROSS1000</c>, <c>Sinfonia Turismo SMART</c>); null when the source does not name one.</summary>
    [MaxLength(SystemMaxLength)]
    public string? System { get; set; }

    [MaxLength(UrlMaxLength)]
    public string? PortalUrl { get; set; }

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

    [Required, MaxLength(UrlMaxLength)]
    public string SourceUrl { get; set; } = string.Empty;

    public DateTime? VerifiedAt { get; set; }

    [MaxLength(NotesMaxLength)]
    public string? Notes { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

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
