namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who cleans a property at the check-out of a stay (E24/F01/US01, NF-120): the default cleaner the host chose for it
/// (<see cref="PropertyCleaningSetup"/>). Stored as the number, so every value has an explicit one and a later task appends the next.
/// </summary>
public enum PropertyCleanerKind
{
    /// <summary>
    /// A supplier of the marketplace (preferably one of the org's trusted suppliers): the automation sends it a service request for the cleaning, at
    /// the agreed price, and the supplier takes it with the existing flow.
    /// </summary>
    Supplier = 1,

    /// <summary>A member of the org with access to the property: the automation assigns it a staff activity of type cleaning, with its cost.</summary>
    Staff = 2,
}
