namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who an invoice of a booking is made out to (<see cref="BookingInvoiceData.RecipientKind"/>, FIS-01, E06/F02/US01/T01). Stored as a number:
/// a later task appends a value with the next number, an existing one never changes.
/// </summary>
public enum InvoiceRecipientKind
{
    /// <summary>A private person: the guest, or whoever the guest names.</summary>
    Guest = 1,

    /// <summary>A company or another entity with a VAT number.</summary>
    Company = 2,
}
