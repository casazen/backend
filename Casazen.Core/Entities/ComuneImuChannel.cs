using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("ComuneImuChannels")]
public class ComuneImuChannel
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(100)]
    public string Comune { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Region { get; set; } = string.Empty;

    [Required, MaxLength(300)]
    public string RecipientOffice { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Email { get; set; }

    [MaxLength(300)]
    public string? Pec { get; set; }

    [MaxLength(300)]
    public string? PostalAddress { get; set; }

    [MaxLength(2000)]
    public string? Instructions { get; set; }

    [Precision(7, 3)]
    public decimal? RatePercent { get; set; }

    [Precision(7, 3)]
    public decimal? EffectiveRatePercent { get; set; }

    public int? RateYear { get; set; }

    public ImuRateKind? RateKind { get; set; }

    [MaxLength(1000)]
    public string? RateNotes { get; set; }

    [MaxLength(500)]
    public string? RateSourceUrl { get; set; }

    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    public DataCompleteness DataCompleteness { get; set; } = DataCompleteness.Missing;

    public DateTime? LastVerifiedAt { get; set; }

    [MaxLength(500)]
    public string? VerificationSource { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
