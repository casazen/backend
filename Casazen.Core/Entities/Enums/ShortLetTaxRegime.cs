namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The tax regime a person decided to apply to one stay (E18/F01/US03/T01, ⚖ I7, NF-117): whether the stay of a company tenant in the property of a private owner
/// is still a short let (the intermediary withholds 21%) or not. Persisted as int, explicit values: append-only, never reorder or reuse one.
/// </summary>
public enum ShortLetTaxRegime
{
    /// <summary>The stay stays a short let: the withholding of the intermediary applies (the reading of the Court of Cassation: only the landlord counts).</summary>
    ShortLet = 1,

    /// <summary>The stay is not a short let: no withholding of the intermediary on it (the reading of the tax agency circular: a company tenant excludes the regime).</summary>
    NotShortLet = 2,
}
