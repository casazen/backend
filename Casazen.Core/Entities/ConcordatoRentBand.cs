using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("ConcordatoRentBands")]
public class ConcordatoRentBand
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TerritorialRentAgreementId { get; set; }

    public TerritorialRentAgreement Agreement { get; set; } = null!;

    [Required, MaxLength(100)]
    public string ZoneName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CadastralSheets { get; set; }

    /// <summary>
    /// Exclusive lower bound of the band, in square metres: the band holds <c>MinSqm &lt; mq ≤ MaxSqm</c> (LT-10, A7-10).
    /// Contiguous bands share the bound (MB: (0,50], (50,74], (74,99], (99,∞)), so no surface falls between two bands.
    /// </summary>
    public int MinSqm { get; set; }

    /// <summary>Inclusive upper bound of the band; null for the last band ("Oltre 100 mq").</summary>
    public int? MaxSqm { get; set; }

    /// <summary>True when <paramref name="sqm"/> is in the half-open interval (<see cref="MinSqm"/>, <see cref="MaxSqm"/>].</summary>
    public bool Contains(decimal sqm) => sqm > MinSqm && (MaxSqm is null || sqm <= MaxSqm.Value);

    [Precision(18, 2)]
    public decimal SubFascia1MinEurSqmYear { get; set; }

    [Precision(18, 2)]
    public decimal SubFascia1MaxEurSqmYear { get; set; }

    [Precision(18, 2)]
    public decimal SubFascia2MinEurSqmYear { get; set; }

    [Precision(18, 2)]
    public decimal SubFascia2MaxEurSqmYear { get; set; }

    [Precision(18, 2)]
    public decimal SubFascia3MinEurSqmYear { get; set; }

    [Precision(18, 2)]
    public decimal SubFascia3MaxEurSqmYear { get; set; }
}
