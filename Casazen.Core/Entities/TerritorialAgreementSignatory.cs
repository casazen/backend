using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("TerritorialAgreementSignatories")]
public class TerritorialAgreementSignatory
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TerritorialRentAgreementId { get; set; }

    public TerritorialRentAgreement Agreement { get; set; } = null!;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public SignatoryRole Role { get; set; }

    [Required, MaxLength(200)]
    public string Contact { get; set; } = string.Empty;
}
