namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the cleaning of a stay is recharged to the owner under the mandate (E14/F01/US02/T01, ⚖ I13, NF-114): a field of the mandate, read by the posting of the owner ledger and
/// shown by the mandate API. Persisted as int, explicit values: append-only, never reorder or reuse one. A mandate written before the field stores none, which reads as
/// <see cref="FlatRate"/> (<c>OwnerMandateRules.CleaningRechargeModeOf</c>, the one definition).
/// </summary>
public enum CleaningRechargeMode
{
    FlatRate = 1,

    ActualCost = 2,
}
