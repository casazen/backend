using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Comune office that receives the canone concordato IMU-reduction communication, with its rate (LT-13, A7-22). One
/// row per comune with known data; a comune with no row falls back to "verify with the comune's ufficio tributi" in
/// the notification draft. Every value is data an admin can change without a deploy (<c>AdminCanoneConcordatoController</c>);
/// <see cref="DataCompleteness"/> other than <c>Complete</c> keeps the uncertainty visible to the landlord.
/// </summary>
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

    /// <summary>Free text: portal name, uncertainty between channels, anything the landlord needs to know before sending.</summary>
    [MaxLength(2000)]
    public string? Instructions { get; set; }

    /// <summary>Nominal IMU rate published by the comune, per mille or percent as the comune states it.</summary>
    [Precision(7, 3)]
    public decimal? RatePercent { get; set; }

    /// <summary>Rate after the national 25% canone concordato reduction (L. 160/2019 art. 1 c. 760), when computed.</summary>
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

    /// <summary>What the last verification was checked against (an admin's free text, never inferred).</summary>
    [MaxLength(500)]
    public string? VerificationSource { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
