namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a fiscal document is for (D4 of the analysis of 2026-11-14): the same engine, numbering, PDF and XML make all of them. Stored as a number: a
/// value never changes, a new one takes the next number.
/// </summary>
public enum FiscalDocumentPurpose
{
    /// <summary>The document of a stay, made out to the guest (the default, and every document issued before the purpose existed).</summary>
    GuestStay = 1,

    /// <summary>The invoice of the property manager to an owner, made from a version of the owner statement (E14).</summary>
    OwnerInvoice = 2,

    /// <summary>A self-billed invoice (Italian TD17, E17).</summary>
    SelfBilling = 3,
}
