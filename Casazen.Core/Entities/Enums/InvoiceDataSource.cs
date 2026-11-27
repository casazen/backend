namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the invoice data of a booking came from (<see cref="BookingInvoiceData.Source"/>, FIS-01, E06/F02/US01/T01). The number is also the
/// precedence the service applies when two sources disagree: the host's edit outranks what the guest typed at the check-in, which outranks what
/// the channel manager had (<see cref="InvoiceDataPrecedence"/>). Stored as a number: a later source appends the next number.
/// </summary>
public enum InvoiceDataSource
{
    /// <summary>The billing data Octorate holds for the booker: the lowest source, it only fills what is empty.</summary>
    Octorate = 1,

    /// <summary>What the guest answered to «Ti serve la fattura?» in the public check-in.</summary>
    CheckIn = 2,

    /// <summary>What the host typed or corrected in CasaZen: the highest source, never overwritten by another.</summary>
    Host = 3,
}

/// <summary>The rule of precedence between the sources of the invoice data of a booking, in one place.</summary>
public static class InvoiceDataPrecedence
{
    /// <summary>
    /// True when the source a row already has outranks <paramref name="incoming"/>: the incoming data then fills only the fields that are empty
    /// and never overwrites a stored value. The same source, or a higher one, overwrites the fields it sends.
    /// </summary>
    public static bool Outranks(InvoiceDataSource stored, InvoiceDataSource incoming) => (int)stored > (int)incoming;
}
