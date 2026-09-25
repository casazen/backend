using Casazen.Core.Regulatory;

namespace Casazen.Core.DTOs;

public record OwnerCinComplianceItem(
    Guid PropertyId,
    string PropertyName,
    string? CinCode,
    string CinStatus,
    string City);

/// <param name="Deadline">The configured CIN deadline on the Europe/Rome day of the request (CO-20).</param>
public record CinComplianceSummary(
    int Valid,
    int Missing,
    int Invalid,
    CinDeadlineStatus Deadline,
    bool HasNonCompliant);

public record OwnerCinComplianceResult(
    IReadOnlyList<OwnerCinComplianceItem> Items,
    int TotalCount,
    CinComplianceSummary Summary);
