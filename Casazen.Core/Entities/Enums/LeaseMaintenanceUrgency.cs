namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How soon the tenant says the work is needed. The names match <see cref="ServiceRequestUrgency"/>
/// (<c>Normal</c>, <c>High</c>, <c>Emergency</c>): the API writes them with <c>JsonStringEnumConverter</c>.
/// Italian labels stay on the client. Stored as int, append-only.
/// </summary>
public enum LeaseMaintenanceUrgency
{
    Normal = 0,
    High = 1,
    Emergency = 2,
}
