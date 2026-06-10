namespace Casazen.Core.DTOs;

public record OwnerCinComplianceItem(
    Guid PropertyId,
    string PropertyName,
    string? CinCode,
    string CinStatus,
    string City);

public record CinComplianceSummary(
    int Valid,
    int Missing,
    int Invalid,
    int DaysUntilDeadline,
    DateOnly Deadline,
    bool HasNonCompliant);

public record OwnerCinComplianceResult(
    IReadOnlyList<OwnerCinComplianceItem> Items,
    int TotalCount,
    CinComplianceSummary Summary);
