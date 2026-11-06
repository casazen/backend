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
/// <item><b>Never in bulk</b>: a stay already over is not read, and an org gets at most <c>Crm:OctorateContactReadsPerHour</c> reads an hour.</item>
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

/// <summary>What <see cref="IOctorateContactsService.ReadForBookingAsync"/> did.</summary>
public enum OctorateContactReadOutcome
{
    /// <summary>The contact was read from Octorate and at least one address or number was stored.</summary>
    Stored = 1,

    /// <summary>The contact was read (or Octorate says the guest has none) and there was nothing to store, as for a guest of Airbnb.</summary>
    NothingToStore = 2,

    /// <summary>
    /// Nothing was read: <c>Crm:OctorateContactsEnabled</c>, or the connector, is off, or the CRM module is closed (<c>Features:CrmMarketing</c> off or
    /// <c>Crm:ContactIndexKey</c> not set): no call to Octorate is made.
    /// </summary>
    Disabled = 3,

    /// <summary>
    /// Nothing was read: there is no such booking in the org, or it is not a booking that came from Octorate (one made in CasaZen has none, or was
    /// sent there by CasaZen: its guest's contact is CasaZen's own).
    /// </summary>
    NotAnOctorateBooking = 4,

    /// <summary>Nothing was read: the check-out of the stay is in the past (no history is read).</summary>
    StayOver = 5,

    /// <summary>Nothing was read: the property of the booking has no confirmed listing on Octorate to read the reservation from.</summary>
    NotMapped = 6,

    /// <summary>Nothing was read: the org already had <c>Crm:OctorateContactReadsPerHour</c> reads in the last hour. Ask again later.</summary>
    OverCap = 7,

    /// <summary>Nothing was read: the booking was read in the last day (its contacts are stored).</summary>
    AlreadyRead = 8,

    /// <summary>
    /// Octorate does not give the contact of this booking (a refusal for lack of the static IP addresses or of the permission, a reservation it
    /// no longer knows, a guest it names no id for): recorded for the booking, and not asked again for a while.
    /// </summary>
    NotAvailable = 9,

    /// <summary>The read could not be made now (Octorate did not answer, or its limit was reached): nothing is recorded, a later call tries again.</summary>
    Deferred = 10,

    /// <summary>Nothing was read: the guest of the booking was erased or anonymized, and its contacts must not come back (E07/F06/US02/T01).</summary>
    GuestErased = 11,
}

/// <summary>The result of a contact read for one booking.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ContactsStored">The addresses and numbers stored (0 to 2).</param>
public sealed record OctorateContactReadResult(OctorateContactReadOutcome Outcome, int ContactsStored = 0)
{
    /// <summary>True when the contact of the booking is in CasaZen after the call (just read, or read before).</summary>
    public bool HasContact => Outcome is OctorateContactReadOutcome.Stored or OctorateContactReadOutcome.AlreadyRead;
}
