using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs;

/// <summary>
/// Cadastral identification of a property (LT-10): all optional, empty clears the value. Lengths only; the formats are
/// not validated (no invented patterns).
/// </summary>
public sealed class UpdatePropertyCadastralRequest
{
    /// <summary>Foglio.</summary>
    [MaxLength(PropertyCadastralLimits.SheetMaxLength)]
    public string? Sheet { get; set; }

    /// <summary>Particella (mappale).</summary>
    [MaxLength(PropertyCadastralLimits.ParcelMaxLength)]
    public string? Parcel { get; set; }

    /// <summary>Subalterno.</summary>
    [MaxLength(PropertyCadastralLimits.SubalternMaxLength)]
    public string? Subaltern { get; set; }

    /// <summary>Categoria catastale (e.g. "A/2").</summary>
    [MaxLength(PropertyCadastralLimits.CategoryMaxLength)]
    public string? Category { get; set; }

    /// <summary>Rendita catastale in euros.</summary>
    [Range(0, PropertyCadastralLimits.IncomeMax)]
    public decimal? Income { get; set; }
}

/// <summary>Code and energy class printed on an APE document (LT-10).</summary>
public sealed class UpdateApeIdentificationRequest
{
    [Required]
    [MaxLength(ApeDocumentLimits.CodeMaxLength)]
    public string? Code { get; set; }

    [Required]
    [MaxLength(ApeDocumentLimits.EnergyClassMaxLength)]
    public string? EnergyClass { get; set; }
}
