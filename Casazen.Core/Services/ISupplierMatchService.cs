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
    string Source);

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

public interface ISupplierMatchService
{
    /// <summary>
    /// Ranks the active suppliers for a property of <paramref name="orgId"/>. The caller has already authorized the
    /// property (TN-3); a property of another org is rejected. The host's notes are not an input: nothing typed by
    /// the host can reach an AI prompt (A8-15).
    /// </summary>
    Task<SupplierMatchResult> MatchAsync(
        Guid orgId,
        Guid propertyId,
        string category,
        ServiceRequestUrgency urgency,
        CancellationToken cancellationToken = default);
}
