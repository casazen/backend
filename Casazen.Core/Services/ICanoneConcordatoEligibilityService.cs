using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Core.Services;

public static class CanoneConcordatoCopy
{
    public const string Disclaimer =
        "Informativa, non consulenza fiscale o legale. CasaZen non presenta dichiarazioni, non rilascia l'attestazione di conformità e non sostituisce un commercialista o un'associazione di categoria.";

    public const string ReasonDataUnavailable =
        "dato non disponibile per questo comune — verificare con l'associazione di categoria locale";

    public const string ReasonZoneRequired =
        "zona o foglio catastale obbligatorio";

    public const string ReasonZoneNotFound =
        "zona o foglio catastale non previsti dall'accordo per questo comune";

    public const string ReasonInvalidSqm =
        "superficie catastale non valida";

    public const string ReasonSurfaceOutOfBands =
        "superficie fuori dalle fasce dell'accordo";

    public const string ReasonInvalidElementCounts =
        "gli elementi D qualificanti non possono superare gli elementi D";

    public const string ReasonTermTooShort =
        "durata inferiore ai 3 anni del contratto 3+2";
}

/// <summary>Why no range is available (<see cref="CanoneConcordatoEligibilityDto.ReasonCode"/>), stable for the client.</summary>
public static class CanoneConcordatoReasonCodes
{
    public const string DataUnavailable = "data_unavailable";
    public const string ZoneRequired = "zone_required";
    public const string ZoneNotFound = "zone_not_found";
    public const string InvalidSurface = "invalid_surface";
    public const string SurfaceOutOfBands = "surface_out_of_bands";
    public const string InvalidElementCounts = "invalid_element_counts";
    public const string TermTooShort = "term_too_short";
}

/// <summary>Warnings on an available range (<see cref="CanoneConcordatoEligibilityDto.Warnings"/>).</summary>
public static class CanoneConcordatoWarningCodes
{
    /// <summary>The agreement data are not confirmed (Partial): the range is indicative and never blocks (A7-23).</summary>
    public const string PartialData = "partial_data";

    /// <summary>
    /// Sub-fascia 3 with fewer D-elements than the agreement requires for the maximum of sub-fascia 3: the agreement does
    /// not say which ceiling applies (class D), the maximum shown may be too high.
    /// </summary>
    public const string SubFascia3MaxNeedsMoreTypeD = "subfascia3_max_needs_more_d";

    /// <summary>Term over 6 years: the agreement has no uplift for it, none is applied (prudent on the maximum).</summary>
    public const string NoDurationUpliftOverSixYears = "no_duration_uplift_over_6_years";
}

/// <summary>
/// Characteristics of the unit for the canone concordato range (LT-10). The term is not here: it always comes from the
/// lease dates (<see cref="LeaseTerm"/>), never from the client (A7-12).
/// </summary>
public sealed record RentBandCharacteristics
{
    /// <summary>Surface of the dwelling (cadastral), without appurtenances.</summary>
    public decimal Sqm { get; init; }

    /// <summary>Garage or covered parking space leased with the unit.</summary>
    public decimal GarageSqm { get; init; }

    /// <summary>Balconies and terraces.</summary>
    public decimal BalconySqm { get; init; }

    /// <summary>Open parking space, cellar, attic or other appurtenances.</summary>
    public decimal OtherAppurtenanceSqm { get; init; }

    /// <summary>Exclusive green areas.</summary>
    public decimal PrivateGreenSqm { get; init; }

    public int TypeAElementCount { get; init; }

    public int TypeBElementCount { get; init; }

    public int TypeCElementCount { get; init; }

    public int TypeDElementCount { get; init; }

    /// <summary>D-elements among those the agreement lists for sub-fascia 3 (MB: D1, D2, D4, D6, D7, D9).</summary>
    public int QualifyingTypeDElementCount { get; init; }

    /// <summary>Heating by stoves in the single rooms.</summary>
    public bool StoveHeating { get; init; }

    /// <summary>Complete furniture.</summary>
    public bool IsFurnished { get; init; }

    /// <summary>Air conditioning as the agreement defines it.</summary>
    public bool AirConditioning { get; init; }

    public string? ZoneName { get; init; }

    /// <summary>Cadastral sheet; when empty the property's own sheet is used.</summary>
    public string? CadastralSheet { get; init; }
}

public record CanoneConcordatoEligibilityDto(
    bool Available,
    string? Reason,
    string Comune,
    string? Zone,
    int? SubFascia,
    decimal? CanoneMinAnnuo,
    decimal? CanoneMaxAnnuo,
    decimal? CanoneMinMensile,
    decimal? CanoneMaxMensile,
    DataCompleteness? DataCompleteness,
    bool ImuAppliesTheoretical,
    bool AtaApplies,
    bool AttestationRequired,
    string Disclaimer)
{
    /// <summary>Stable code of <see cref="Reason"/> (<see cref="CanoneConcordatoReasonCodes"/>); null when available.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Whole years of the term, computed from the dates.</summary>
    public int? ContractYears { get; init; }

    /// <summary>Surface plus appurtenances at the agreement's percentages ("mq utili").</summary>
    public decimal? UsableSqm { get; init; }

    /// <summary>Exclusive lower bound of the surface band used (half-open band, A7-10).</summary>
    public int? BandMinSqm { get; init; }

    /// <summary>Inclusive upper bound of the surface band used; null for the last band.</summary>
    public int? BandMaxSqm { get; init; }

    /// <summary>
    /// The range comes from agreement data not confirmed by a lawyer or a signatory organization (not Complete): show it as
    /// indicative, with a warning; it never blocks the lease (A7-23).
    /// </summary>
    public bool Indicative { get; init; }

    /// <summary>Warning codes (<see cref="CanoneConcordatoWarningCodes"/>).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Official text of the agreement.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Date the agreement data were last checked against the official text.</summary>
    public DateTime? LastVerifiedAt { get; init; }

    /// <summary>D-elements that count for sub-fascia 3, as listed by the agreement.</summary>
    public string? SubFascia3QualifyingTypeDElements { get; init; }
}

public interface ICanoneConcordatoEligibilityService
{
    /// <summary>
    /// Rent band of the property for a lease of <paramref name="term"/>, or <c>null</c> when the property is not visible
    /// (tenant filter). No ownership check: the caller authorizes the property first (TN-3), so an org member with the
    /// lease permission gets it too.
    /// </summary>
    Task<CanoneConcordatoEligibilityDto?> CalculateAsync(
        Guid propertyId,
        RentBandCharacteristics characteristics,
        LeaseTerm term,
        CancellationToken cancellationToken = default);
}
