namespace Casazen.Infrastructure.ICalSpike;

/// <summary>
/// In-memory calendar block for F0 iCal spike (#289). Promoted to entity in Fase 1 (US-018).
/// </summary>
public sealed record CalendarBlockSlice(
    string? ExternalUid,
    DateTime StartUtc,
    DateTime EndUtc,
    string? Summary);
