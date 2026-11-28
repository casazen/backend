using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public record SupplierMatchCandidate(
    Guid OrgId,
    string LegalName,
    string Phone,
    string Email,
    string? Bio,
    int MatchScore,
    string MatchReason,
    string Source,
    bool ReasonGeneratedByAi = false);

public record ExternalSupplierSuggestion(
    string Name,
    string Address,
    string? Phone,
    string? Email,
    double? Rating,
    int? ReviewCount,
    string? GoogleMapsUrl,
    string? WebsiteUrl,
    string Source);

public record SupplierMatchResult(
    SupplierMatchCandidate? Recommended,
    IReadOnlyList<SupplierMatchCandidate> Alternatives,
    IReadOnlyList<ExternalSupplierSuggestion> ExternalSuggestions,
    bool UsedExternalFallback);

public sealed record RankedSupplier(Casazen.Core.Entities.SupplierProfile Profile, int Score, int Load);

public interface ISupplierMatchService
{
    Task<IReadOnlyList<RankedSupplier>> RankActiveAsync(
        Casazen.Core.Suppliers.ComuneTarget target,
        string category,
        ServiceRequestUrgency urgency,
        CancellationToken cancellationToken = default);

    Task<SupplierMatchResult> MatchAsync(
        Guid orgId,
        Guid propertyId,
        string category,
        ServiceRequestUrgency urgency,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The ranking score (1 to 100, <see cref="Casazen.Core.Suppliers.SupplierRanking"/>) of each of <paramref name="suppliers"/> for a
    /// request of <paramref name="urgency"/>: the same score <see cref="MatchAsync"/> orders by, with the reliability of each supplier
    /// (SP-REL). The list a host sees when it asks for a job is ordered by it; the number itself is never shown to anybody.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> ScoresAsync(
        IReadOnlyList<Casazen.Core.Entities.SupplierProfile> suppliers,
        ServiceRequestUrgency urgency,
        CancellationToken cancellationToken = default);
}
