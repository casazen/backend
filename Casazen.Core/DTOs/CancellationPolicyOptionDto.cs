namespace Casazen.Core.DTOs;

/// <summary>
/// A cancellation policy the host can assign to a property (<c>GET /api/properties/cancellation-policies</c>, A2-04).
/// </summary>
public sealed record CancellationPolicyOptionDto(
    Guid Id,
    string Name,
    string Description,
    int FullRefundHours,
    decimal PartialRefundPercent,
    int PartialRefundHours);
