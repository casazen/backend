using Casazen.Core.Entities;
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

public static class CanoneConcordatoWarningCodes
{
    public const string PartialData = "partial_data";

    public const string SubFascia3MaxNeedsMoreTypeD = "subfascia3_max_needs_more_d";

    public const string NoDurationUpliftOverSixYears = "no_duration_uplift_over_6_years";

    /// <summary>
    /// Today is past the agreement's formal <see cref="TerritorialRentAgreement.ExpiresAt"/> (LT-13, A7-22). Never
    /// blocks: most agreements (art. 14-style clauses) remain in force until the signatories sign a new one — see
    /// <see cref="CanoneConcordatoEligibilityDto.AgreementRemainsInForceUntilReplaced"/>.
    /// </summary>
    public const string AgreementExpired = "agreement_expired";
}

public sealed record RentBandCharacteristics
{
    public decimal Sqm { get; init; }

    public decimal GarageSqm { get; init; }

    public decimal BalconySqm { get; init; }

    public decimal OtherAppurtenanceSqm { get; init; }

    public decimal PrivateGreenSqm { get; init; }

    public int TypeAElementCount { get; init; }

    public int TypeBElementCount { get; init; }

    public int TypeCElementCount { get; init; }

    public int TypeDElementCount { get; init; }

    public int QualifyingTypeDElementCount { get; init; }

    public bool StoveHeating { get; init; }

    public bool IsFurnished { get; init; }

    public bool AirConditioning { get; init; }

    public string? ZoneName { get; init; }

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
    public string? ReasonCode { get; init; }

    public int? ContractYears { get; init; }

    public decimal? UsableSqm { get; init; }

    public int? BandMinSqm { get; init; }

    public int? BandMaxSqm { get; init; }

    /// <summary>
    /// The range comes from agreement data not confirmed by a lawyer or a signatory organization (not Complete): show it as
    /// indicative, with a warning; it never blocks the lease (A7-23).
    /// </summary>
    public bool Indicative { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? SourceUrl { get; init; }

    public DateTime? LastVerifiedAt { get; init; }

    public string? SubFascia3QualifyingTypeDElements { get; init; }

    public DateTime? AgreementExpiresAt { get; init; }

    public bool AgreementRemainsInForceUntilReplaced { get; init; }
}

public sealed record ConcordatoZoneDto(string Name, IReadOnlyList<string> CadastralSheets);

public sealed record CanoneConcordatoZonesDto(
    string Comune,
    bool Available,
    DataCompleteness? DataCompleteness,
    IReadOnlyList<ConcordatoZoneDto> Zones);

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

    Task<CanoneConcordatoZonesDto?> GetZonesAsync(Guid propertyId, CancellationToken cancellationToken = default);
}
