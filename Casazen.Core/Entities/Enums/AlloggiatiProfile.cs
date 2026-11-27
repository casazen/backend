namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kind of Questura account the Alloggiati Web credentials of a property belong to (AW-R02, Q1-14). Stored as its number: the
/// values are explicit and never renumbered.
/// </summary>
public enum AlloggiatiProfile
{
    /// <summary>One account for one structure: the account of the property alone.</summary>
    SingleStructure = 1,

    /// <summary>
    /// Profile «Gestione Appartamenti»: one account of the agency shared by several apartments, each known to the Questura by its
    /// <c>IdAppartamento</c>.
    /// </summary>
    ApartmentsManagement = 2,
}
