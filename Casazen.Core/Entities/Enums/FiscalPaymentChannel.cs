namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who collected the money of a fiscal document (§5bis of the analysis of 2026-11-14): it decides the payment state the document prints («paid through
/// the portal», «paid», «to collect»). Filled by the documents of every booking (E12); <see cref="Unknown"/> until then. Stored as a number.
/// </summary>
public enum FiscalPaymentChannel
{
    /// <summary>Not known: the payment state of the document says what was collected.</summary>
    Unknown = 0,

    /// <summary>The guest paid in CasaZen (the booking site of the agency on CasaZen).</summary>
    CasaZen = 1,

    /// <summary>The portal collected the stay and pays it out to the account holder.</summary>
    Portal = 2,

    /// <summary>The agency collected outside CasaZen (its own site, by hand, at the property).</summary>
    Outside = 3,
}
