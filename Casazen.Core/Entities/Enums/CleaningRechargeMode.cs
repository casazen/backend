namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the cleaning of a stay is recharged to the owner under the mandate (E14/F01/US02/T01, ⚖ I13, NF-114): a field of the mandate, read by the posting of the owner ledger and
/// shown by the mandate API. Persisted as int, explicit values: append-only, never reorder or reuse one. A mandate written before the field stores none, which reads as
/// <see cref="FlatRate"/> (<c>OwnerMandateRules.CleaningRechargeModeOf</c>, the one definition).
/// </summary>
public enum CleaningRechargeMode
{
    /// <summary>The flat fee of the mandate (<see cref="OwnerMandate.CleaningFlatRate"/>) for each stay, as in E13: the default (⚖ I13).</summary>
    FlatRate = 1,

    /// <summary>
    /// The cost of each cleaning request completed in the period: one charge for each, linked to the request, read through the cost source of the cleanings. The flat fee of the
    /// stay is not posted for the mandate: the cleaning is charged once, never both.
    /// </summary>
    ActualCost = 2,
}
