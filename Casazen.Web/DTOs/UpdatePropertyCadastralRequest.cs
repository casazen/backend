using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs;

public sealed class UpdatePropertyCadastralRequest
{
    [MaxLength(PropertyCadastralLimits.SheetMaxLength)]
    public string? Sheet { get; set; }

    [MaxLength(PropertyCadastralLimits.ParcelMaxLength)]
    public string? Parcel { get; set; }

    [MaxLength(PropertyCadastralLimits.SubalternMaxLength)]
    public string? Subaltern { get; set; }

    [MaxLength(PropertyCadastralLimits.CategoryMaxLength)]
    public string? Category { get; set; }

    [Range(0, PropertyCadastralLimits.IncomeMax)]
    public decimal? Income { get; set; }
}

public sealed class UpdateApeIdentificationRequest
{
    [Required]
    [MaxLength(ApeDocumentLimits.CodeMaxLength)]
    public string? Code { get; set; }

    [Required]
    [MaxLength(ApeDocumentLimits.EnergyClassMaxLength)]
    public string? EnergyClass { get; set; }
}
