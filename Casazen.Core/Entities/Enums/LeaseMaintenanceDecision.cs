namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The landlord or agency decision. Serialized as <c>Approved</c> or <c>Rejected</c>
/// (<c>JsonStringEnumConverter</c>). Italian labels stay on the client.
/// </summary>
public enum LeaseMaintenanceDecision
{
    Approved = 0,
    Rejected = 1,
}
