namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where an activation of the marketplace stands (E09/F01/US01, NF-54, NF-56, NF-57). Stored as its number, never reused. The card of the
/// marketplace shows <see cref="Requested"/> and <see cref="Pending"/> as Pending, <see cref="ToFix"/> as To fix, <see cref="Active"/> as
/// Active, and <see cref="Deactivated"/> (or no row at all) as Not active.
/// </summary>
public enum MarketplaceActivationStatus
{
    /// <summary>The host asked for it and nothing has answered yet (a licensed function waits until it appears in the license).</summary>
    Requested = 1,

    /// <summary>The activation has started and is not finished (the connection is made, the import or the access is not).</summary>
    Pending = 2,

    /// <summary>It works, as Octorate says, or as the host declared it for a partner (see <c>MarketplaceActivation.IsDeclared</c>).</summary>
    Active = 3,

    /// <summary>It stopped working and the host has to do something: a To do item is open for it.</summary>
    ToFix = 4,

    /// <summary>The host turned it off. The row is kept, so the history of the activation is not lost.</summary>
    Deactivated = 5,
}
