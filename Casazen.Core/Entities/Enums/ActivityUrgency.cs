namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How urgent a "To do" item is (ADR-026). Stored as its number in <see cref="InternalActivity.Urgency"/>, so the list is
/// sorted in SQL with the highest first; the nav badge counts the <see cref="High"/> ones only (ADR-026-R12).
/// </summary>
public enum ActivityUrgency
{
    Low = 1,
    Medium = 2,
    High = 3,
}
