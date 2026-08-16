using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public static class CanoneConcordatoCopy
{
    public const string Disclaimer =
        "Informativa, non consulenza fiscale o legale. CasaZen non presenta dichiarazioni, non rilascia l'attestazione di conformità e non sostituisce un commercialista o un'associazione di categoria.";

    public const string ReasonDataUnavailable =
        "dato non disponibile per questo comune — verificare con l'associazione di categoria locale";

    public const string ReasonZoneRequired =
        "zona o foglio catastale obbligatorio";
}

public record RentBandCharacteristics(
    decimal Sqm,
    int TypeAElementCount,
    int TypeBElementCount,
    int TypeCElementCount,
    int TypeDElementCount,
    bool IsFurnished,
    int ContractYears,
    string? ZoneName,
    string? CadastralSheet);

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
    string Disclaimer);

public interface ICanoneConcordatoEligibilityService
{
    Task<CanoneConcordatoEligibilityDto?> CalculateAsync(
        Guid propertyId,
        string ownerId,
        RentBandCharacteristics characteristics,
        CancellationToken cancellationToken = default);
}
