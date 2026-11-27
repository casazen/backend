namespace Casazen.Core.Entities.Enums;

/// <summary>Whether a CondoLedger snapshot row is still staged or has been confirmed into the CasaZen book. Stored as an integer: never renumber.</summary>
public enum CondominiumImportStatus
{
    Staged = 0,
    Confirmed = 1,
}
