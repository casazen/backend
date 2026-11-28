namespace Casazen.Core.Services;

/// <summary>
/// Reads the contact of the guest of ONE Octorate booking and records it for the service (messages, check-in, invoice): E07/F06/US02/T02, NF-94,
/// CRM-07, ADR-022. The contacts of the guests are sensitive data that Octorate releases only to the registered static IP addresses of CasaZen
/// (S7, S18, D24) and logs on every read, so the read is made <b>per booking</b>: there is no list, no export and no read of a guest that has no
/// booking. Bookings reach CasaZen only through the import of the channel manager (<c>IBookingImportService</c>): this service imports nothing.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Off until the product owner says so.</b> Nothing is read unless <c>Crm:OctorateContactsEnabled</c> is on (and the connector is on); he
/// turns it on once the IP addresses are registered. <b>And only while the CRM module is open</b> (<see cref="CrmProfileSyncGate"/>:
/// <c>Features:CrmMarketing</c> on and <c>Crm:ContactIndexKey</c> set): what is read is stored by the CRM, and with the module closed nothing could
/// be stored, so nothing is asked of Octorate.</item>
/// <item><b>What is stored</b> goes through <see cref="ICrmContactService"/>: origin <c>OctorateContactInfo</c>, basis service only; an address of
/// an OTA booking or on a relay domain is an OTA forward and never marketing; a customer of the booking engine of the agency is soft opt-in for a
/// confirmed booking only while <c>Crm:BookingEngineSoftOptIn</c> is on.</item>
/// <item><b>Never in bulk</b>: a stay already over and a cancelled booking are not read, and an org gets at most <c>Crm:OctorateContactReadsPerHour</c> reads an hour.</item>
/// <item><b>A refusal is not an empty contact</b>: it is recorded as not available for that booking and not asked again in a loop.</item>
/// <item>Nothing the read returns, and no address, number or name, is written to a log: ids and the outcome only.</item>
/// </list>
/// </remarks>
public interface IOctorateContactsService
{
    /// <summary>
    /// Reads the contact of the guest of <paramref name="bookingId"/> of <paramref name="orgId"/> from Octorate and stores it, if the rules in the
    /// remarks of the interface allow it. Never throws for something Octorate did or did not do (a refusal, an outage, a limit): the outcome says
    /// what happened. Idempotent: a booking read in the last day is not read again.
    /// </summary>
    Task<OctorateContactReadResult> ReadForBookingAsync(Guid orgId, Guid bookingId, CancellationToken ct = default);
}

public enum OctorateContactReadOutcome
{
    Stored = 1,

    NothingToStore = 2,

    Disabled = 3,

    NotAnOctorateBooking = 4,

    StayOver = 5,

    NotMapped = 6,

    OverCap = 7,

    AlreadyRead = 8,

    /// <summary>
    /// Octorate does not give the contact of this booking (a refusal for lack of the static IP addresses or of the permission, a reservation it
    /// no longer knows, a guest it names no id for, a request it rejects with a 4xx that is not a refusal of the data, a missing reservation or a
    /// limit): recorded for the booking, and not asked again for a while.
    /// </summary>
    NotAvailable = 9,

    Deferred = 10,

    GuestErased = 11,

    /// <summary>
    /// Octorate answered, but CasaZen does not understand the answer: the contact of the guest has none of the members that hold an address or a
    /// number (another wrapper, other names), or it is empty while the reservation says Octorate holds an address or a number. It is not "the guest
    /// has no contact": recorded for the booking, logged with the code <c>octorate_contact_info_unrecognized</c> only, and not asked again for a
    /// while. The answer is to be seen once in the sandbox (runbook § 5).
    /// </summary>
    Unrecognized = 12,

    BookingCancelled = 13,
}

public sealed record OctorateContactReadResult(OctorateContactReadOutcome Outcome, int ContactsStored = 0)
{
    public bool HasContact => Outcome is OctorateContactReadOutcome.Stored or OctorateContactReadOutcome.AlreadyRead;
}
