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

    public int MinSqm { get; set; }

    public int? MaxSqm { get; set; }

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
