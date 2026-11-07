namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Preventivo or consuntivo (CondoLedger <c>due_kind</c>). Stored as an integer: never renumber.
/// </summary>
public enum CondominiumStatementKind
{
    /// <summary>Preventivo: the amount decided at the start of the year.</summary>
    Budget = 0,

    /// <summary>Consuntivo: what was actually spent.</summary>
    Final = 1,
}
