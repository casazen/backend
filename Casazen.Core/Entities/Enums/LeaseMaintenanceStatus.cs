namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The decision on a tenant maintenance request. <see cref="Pagata"/> is set only when a real euro debit succeeds.
/// An approval stays <see cref="Approvata"/> until then. Stored as int, append-only.
/// </summary>
public enum LeaseMaintenanceStatus
{
    InAttesa = 0,
    Approvata = 1,
    Rifiutata = 2,
    Pagata = 3,
}
