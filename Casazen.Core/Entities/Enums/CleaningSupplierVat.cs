namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What the agency answered to «La ditta di pulizie ha applicato l'IVA su questa fattura?» for a cleaning request recharged to the owner (FIX/BE-CLEANING-VAT,
/// decision 4 of 20/11/2026): on the recharge to the owner the cleaning follows the VAT of the supplier. Stored by number on
/// <see cref="ServiceRequest.SupplierVat"/>; the API writes the name.
/// </summary>
public enum CleaningSupplierVat
{
    Applied = 1,

    /// <summary>«No, senza IVA»: the supplier applied no VAT (below the Swiss threshold, a flat-rate regime): the recharge carries none.</summary>
    NotApplied = 2,

    Unknown = 3,
}
