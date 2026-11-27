namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How urgent a popup alert is (ADR-025). Stored as its number in <see cref="PopupAlert.Urgency"/> and listed with the highest first
/// (<c>GET /api/me/alerts/pending</c> orders by <c>Urgency</c> descending), so the numbers are part of the contract:
/// <see cref="Low"/> must be the smallest and <see cref="High"/> the largest, whatever the order the members are declared in.
/// </summary>
public enum AlertUrgency
{
    Low = 1,
    Normal = 2,
    High = 3,
}
