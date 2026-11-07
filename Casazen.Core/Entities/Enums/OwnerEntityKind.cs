namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What kind of subject a property owner of the agency registry is (E10/F02/US02/T01, NF-109): a natural person or a legal entity
/// (a company, an association, a trust). It decides which fiscal identifier the owner accounting needs (a codice fiscale for a person,
/// a VAT number or a codice fiscale for an entity) and is not the tax regime (<see cref="OwnerTaxRegime"/>). Persisted as int, explicit
/// values: append-only, never reorder or reuse a value. <c>null</c> in the column means «not declared yet».
/// </summary>
public enum OwnerEntityKind
{
    /// <summary>A natural person.</summary>
    NaturalPerson = 1,

    /// <summary>A legal entity: a company, an association, any subject that is not a natural person.</summary>
    LegalEntity = 2,
}
