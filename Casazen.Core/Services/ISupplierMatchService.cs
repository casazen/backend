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
    Task<SupplierMatchResult> MatchAsync(
        Guid orgId,
        string userId,
        Guid propertyId,
        string category,
        ServiceRequestUrgency urgency,
        string? notes,
        CancellationToken cancellationToken = default);
}
