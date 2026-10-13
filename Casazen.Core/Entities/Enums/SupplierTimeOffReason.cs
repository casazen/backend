namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Why a supplier is off (<see cref="Casazen.Core.Entities.SupplierTimeOff.Reason"/>, SP-03). Only a label for the
/// supplier's own console: every reason closes the days in the same way. The values are stored: never reorder or renumber,
/// only append.
/// </summary>
public enum SupplierTimeOffReason
{
    /// <summary>Ferie.</summary>
    Vacation = 0,

    /// <summary>Festività (a public holiday, a patron saint's day, a bridge).</summary>
    Holiday = 1,

    /// <summary>Malattia.</summary>
    Illness = 2,

    /// <summary>Altro.</summary>
    Other = 3,
}
