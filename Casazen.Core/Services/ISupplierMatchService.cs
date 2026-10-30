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

/// <summary>An active supplier of a comune and category with the score <see cref="ISupplierMatchService"/> ranks it by, and the open requests that score counts.</summary>
public sealed record RankedSupplier(Casazen.Core.Entities.SupplierProfile Profile, int Score, int Load);

public interface ISupplierMatchService
{
    /// <summary>
    /// Every active supplier that covers <paramref name="target"/> for <paramref name="category"/>, best first, with the score
    /// <see cref="MatchAsync"/> gives it (SP-BKP: the backups of a request are chosen with it). No AI is called and nothing the host
    /// wrote is read. Empty when no supplier covers the comune. The caller excludes the suppliers it does not want.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Code <c>invalid_service_category</c>: <paramref name="category"/> is not a <see cref="Casazen.Core.Suppliers.ServiceCategories"/> code.
    /// </exception>
    Task<IReadOnlyList<RankedSupplier>> RankActiveAsync(
        Casazen.Core.Suppliers.ComuneTarget target,
        string category,
        ServiceRequestUrgency urgency,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ranks the active suppliers for a property of <paramref name="orgId"/>. The caller has already authorized the
    /// property (TN-3); a property of another org is rejected. The host's notes are not an input: nothing typed by
    /// the host can reach an AI prompt (A8-15).
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Code <c>invalid_service_category</c>: <paramref name="category"/> is not a
    /// <see cref="Casazen.Core.Suppliers.ServiceCategories"/> code.
    /// </exception>
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
