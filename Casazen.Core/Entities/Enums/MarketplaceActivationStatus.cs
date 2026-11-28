namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where an activation of the marketplace stands (E09/F01/US01, NF-54, NF-56, NF-57). Stored as its number, never reused. The card of the
/// marketplace shows <see cref="Requested"/> and <see cref="Pending"/> as Pending, <see cref="ToFix"/> as To fix, <see cref="Active"/> as
/// Active, and <see cref="Deactivated"/> (or no row at all) as Not active.
/// </summary>
public enum MarketplaceActivationStatus
{
    Requested = 1,

    Pending = 2,

    Active = 3,

    ToFix = 4,

    Deactivated = 5,
}
