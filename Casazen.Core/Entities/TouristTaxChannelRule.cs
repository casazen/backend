using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

public enum TouristTaxCollector
{
    /// <summary>The channel (e.g. Airbnb in some comuni) collects it and pays the comune: the host must not collect it again.</summary>
    Channel = 1,

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
    public const int IstatCodeMaxLength = 6;

    public const int SourceUrlMaxLength = 500;

    public const int NotesMaxLength = 500;

    public const int UserIdMaxLength = 255;

    public const string ChannelComuneIndexName = "UIX_TouristTaxChannelRules_Channel_IstatCode";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public BookingSource Channel { get; set; }

    [Required, MaxLength(IstatCodeMaxLength)]
    public string IstatCode { get; set; } = string.Empty;

    public TouristTaxCollector Collector { get; set; }

    [Required, MaxLength(SourceUrlMaxLength)]
    public string SourceUrl { get; set; } = string.Empty;

    public DateTime? VerifiedAt { get; set; }

    public bool IsActive { get; set; } = true;

    [MaxLength(NotesMaxLength)]
    public string? Notes { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
