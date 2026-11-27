namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Ordinary quota, extraordinary works, or not yet known (CondoLedger <c>voice</c>). Stored as an integer: never renumber.
/// </summary>
public enum CondominiumExpenseNature
{
    Ordinary = 0,
    Extraordinary = 1,
    Unknown = 2,
}
