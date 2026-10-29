namespace Casazen.Core.DTOs;

/// <summary>
/// A cancellation policy the host can assign to a property (<c>GET /api/properties/cancellation-policies</c>, A2-04). The
/// numbers are the summary of the window (full refund until <see cref="FullRefundHours"/> before check-in, then
/// <see cref="PartialRefundPercent"/> until <see cref="PartialRefundHours"/>), so a client can state it without copying the
/// description; <see cref="Slug"/> is the stable identifier of the catalog (SR-07a): the name and the description may be
/// reworded, the slug never.
/// </summary>
public sealed record CancellationPolicyOptionDto(
    Guid Id,
    string Name,
    string Description,
    int FullRefundHours,
    decimal PartialRefundPercent,
    int PartialRefundHours,
    string Slug = "");
