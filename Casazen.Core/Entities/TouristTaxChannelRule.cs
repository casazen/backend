using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>Who collects the tourist tax of a stay from the guest and pays it to the comune (ADR-037-R3). Stored as an integer.</summary>
public enum TouristTaxCollector
{
    /// <summary>The channel (e.g. Airbnb in some comuni) collects it and pays the comune: the host must not collect it again.</summary>
    Channel = 1,

    /// <summary>The host collects it at the property and pays the comune.</summary>
    Host = 2,
}

/// <summary>
/// That a channel collects the tourist tax of a comune and pays it itself (NF-46, ADR-037-R3): platform reference data, one row per
/// channel and comune, not tenant-owned. There is NO seed: no pair of channel and comune is documented in the repository (the source
/// requirements only say that "for some municipalities Airbnb collects and pays"), so an administrator adds each row from an official
/// source, which is required (<see cref="SourceUrl"/>).
/// </summary>
[Table("TouristTaxChannelRules")]
public class TouristTaxChannelRule
{
    /// <summary>Length of <see cref="IstatCode"/>.</summary>
    public const int IstatCodeMaxLength = 6;

    /// <summary>Length of <see cref="SourceUrl"/>.</summary>
    public const int SourceUrlMaxLength = 500;

    /// <summary>Length of <see cref="Notes"/>.</summary>
    public const int NotesMaxLength = 500;

    /// <summary>Length of <see cref="UpdatedByUserId"/>: the one of <c>User.Id</c>.</summary>
    public const int UserIdMaxLength = 255;

    /// <summary>Name of the unique index on (<see cref="Channel"/>, <see cref="IstatCode"/>).</summary>
    public const string ChannelComuneIndexName = "UIX_TouristTaxChannelRules_Channel_IstatCode";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The channel of the bookings the rule is about.</summary>
    public BookingSource Channel { get; set; }

    /// <summary>ISTAT code (6 digits) of the comune.</summary>
    [Required, MaxLength(IstatCodeMaxLength)]
    public string IstatCode { get; set; } = string.Empty;

    /// <summary>Who collects the tax of the stays of <see cref="Channel"/> in the comune.</summary>
    public TouristTaxCollector Collector { get; set; }

    /// <summary>The official page or act the rule was read from: required, a rule never exists without its source.</summary>
    [Required, MaxLength(SourceUrlMaxLength)]
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>When an administrator last checked the rule against <see cref="SourceUrl"/>.</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>False: the rule no longer applies (kept for the stays it decided).</summary>
    public bool IsActive { get; set; } = true;

    [MaxLength(NotesMaxLength)]
    public string? Notes { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The administrator who last changed the row.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
