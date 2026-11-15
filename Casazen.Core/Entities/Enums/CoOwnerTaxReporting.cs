namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the withholding evidence and the Certificazione Unica of a property held by several co-owners are written (E18/F01/US03/T01, ⚖ I6, NF-117): a field of the
/// mandate, read by the withholding facts and by the annual tax data of the owners. Persisted as int, explicit values: append-only, never reorder or reuse one.
/// </summary>
public enum CoOwnerTaxReporting
{
    /// <summary>
    /// Everything on the co-owner who signed the mandate (the one whose mandate is in force: <c>DocumentPlanService</c> picks it, then the largest share): the default
    /// of a mandate that does not say (⚖ I6: «altrimenti sul firmatario»), and the value every mandate written before this field has.
    /// </summary>
    Signatory = 0,

    /// <summary>The withholding evidence and the CU split among the co-owners by their share of the property (<c>PropertyOwnership.SharePercent</c>).</summary>
    PerShare = 1,
}
