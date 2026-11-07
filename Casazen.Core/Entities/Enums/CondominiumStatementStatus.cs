namespace Casazen.Core.Entities.Enums;

/// <summary>A statement revision. Stored as an integer: never renumber. A correction supersedes; it does not overwrite.</summary>
public enum CondominiumStatementStatus
{
    Draft = 0,
    Confirmed = 1,
    Superseded = 2,
}
