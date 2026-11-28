namespace Casazen.Core.Entities.Enums;

public enum CoOwnerTaxReporting
{
    /// <summary>
    /// Everything on the co-owner who signed the mandate (the one whose mandate is in force: <c>DocumentPlanService</c> picks it, then the largest share): the default
    /// of a mandate that does not say (⚖ I6: «altrimenti sul firmatario»), and the value every mandate written before this field has.
    /// </summary>
    Signatory = 0,

    PerShare = 1,
}
