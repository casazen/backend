namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the preventivo is first split (CondoLedger cadences). Stored as an integer: never renumber.
/// After the rows exist they stay as issued.
/// </summary>
public enum CondominiumInstallmentCadence
{
    Monthly = 0,
    Bimonthly = 1,
    Semiannual = 2,
    Single = 3,
}
