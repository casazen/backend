using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Canone concordato data of a lease (LT-10, A7-12): the characteristics of the unit declared at creation and the rent
/// range the server computed from them, from the lease dates and from the territorial agreement in force at that moment.
/// Stored with the lease (owned, same table) so the range shown later is the one the lease was checked against.
/// </summary>
public class LeaseConcordatoAssessment
{
    /// <summary>Surface of the dwelling (cadastral, DPR 138/98), without appurtenances.</summary>
    [Precision(10, 2)]
    public decimal Sqm { get; set; }

    /// <summary>Garage or covered parking space leased with the unit, in square metres.</summary>
    [Precision(10, 2)]
    public decimal GarageSqm { get; set; }

    /// <summary>Balconies and terraces, in square metres.</summary>
    [Precision(10, 2)]
    public decimal BalconySqm { get; set; }

    /// <summary>Open parking space, cellar, attic or other appurtenances, in square metres.</summary>
    [Precision(10, 2)]
    public decimal OtherAppurtenanceSqm { get; set; }

    /// <summary>Exclusive green areas, in square metres.</summary>
    [Precision(10, 2)]
    public decimal PrivateGreenSqm { get; set; }

    public int TypeAElementCount { get; set; }

    public int TypeBElementCount { get; set; }

    public int TypeCElementCount { get; set; }

    public int TypeDElementCount { get; set; }

    /// <summary>D-elements among those the agreement lists for sub-fascia 3.</summary>
    public int QualifyingTypeDElementCount { get; set; }

    /// <summary>Heating by stoves in the single rooms.</summary>
    public bool StoveHeating { get; set; }

    /// <summary>Complete furniture (the agreement's maximum furniture uplift).</summary>
    public bool IsFurnished { get; set; }

    /// <summary>Air conditioning as the agreement defines it (MB: on at least half of the surface).</summary>
    public bool AirConditioning { get; set; }

    /// <summary>Zone declared by the landlord (comuni with more zones).</summary>
    [MaxLength(100)]
    public string? ZoneName { get; set; }

    /// <summary>Cadastral sheet (foglio) used to find the zone.</summary>
    [MaxLength(20)]
    public string? CadastralSheet { get; set; }

    /// <summary>Whole years of the lease term, from the lease dates.</summary>
    public int ContractYears { get; set; }

    /// <summary>Surface plus the appurtenances at the agreement's percentages ("mq utili").</summary>
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

    /// <summary>Completeness of the agreement data used: anything but Complete makes the range indicative (A7-23).</summary>
    public DataCompleteness DataCompleteness { get; set; }

    /// <summary>The annual rent of the lease was inside the range when it was computed.</summary>
    public bool RentWithinRange { get; set; }

    public DateTime CalculatedAt { get; set; }

    /// <summary>The range is only indicative: the agreement data are not confirmed (A7-23), it never blocked the lease.</summary>
    public bool Indicative => DataCompleteness != DataCompleteness.Complete;
}
