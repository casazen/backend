using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

public class LeaseConcordatoAssessment
{
    [Precision(10, 2)]
    public decimal Sqm { get; set; }

    [Precision(10, 2)]
    public decimal GarageSqm { get; set; }

    [Precision(10, 2)]
    public decimal BalconySqm { get; set; }

    [Precision(10, 2)]
    public decimal OtherAppurtenanceSqm { get; set; }

    [Precision(10, 2)]
    public decimal PrivateGreenSqm { get; set; }

    public int TypeAElementCount { get; set; }

    public int TypeBElementCount { get; set; }

    public int TypeCElementCount { get; set; }

    public int TypeDElementCount { get; set; }

    public int QualifyingTypeDElementCount { get; set; }

    public bool StoveHeating { get; set; }

    public bool IsFurnished { get; set; }

    public bool AirConditioning { get; set; }

    [MaxLength(100)]
    public string? ZoneName { get; set; }

    [MaxLength(20)]
    public string? CadastralSheet { get; set; }

    public int ContractYears { get; set; }

    [Precision(10, 2)]
    public decimal UsableSqm { get; set; }

    [MaxLength(100)]
    public string Zone { get; set; } = string.Empty;

    public int SubFascia { get; set; }

    [Precision(18, 2)]
    public decimal CanoneMinAnnuo { get; set; }

    [Precision(18, 2)]
    public decimal CanoneMaxAnnuo { get; set; }

    [Precision(18, 2)]
    public decimal CanoneMinMensile { get; set; }

    [Precision(18, 2)]
    public decimal CanoneMaxMensile { get; set; }

    public DataCompleteness DataCompleteness { get; set; }

    public bool RentWithinRange { get; set; }

    public DateTime CalculatedAt { get; set; }

    public bool Indicative => DataCompleteness != DataCompleteness.Complete;
}
