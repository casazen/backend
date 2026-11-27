namespace Casazen.Core.Services;

/// <summary>
/// The contact of the guest of a portal booking, read from the channel manager <b>for the check-in link and for nothing else</b> (E23/F01/US01/T02, NF-122, NF-63, S18,
/// D24, decision O10). Octorate releases the e-mail address and the phone number of a guest only to an application with the dedicated permission, from the static IP
/// addresses registered with it, and records every read, so CasaZen asks for the contact of ONE booking, once, when its link has no other way to the guest: never in
/// bulk, never for a guest that has no booking, and <b>never into the CRM</b> (the CRM path, <c>IOctorateContactsService</c>, stays separate and off: this service
/// calls neither it nor the CRM store).
/// </summary>
/// <remarks>
/// <para><b>What it keeps.</b> A usable e-mail address is written on the guest of the booking (so the e-mail of the link goes to it, as to any booking with an address);
/// a phone number that comes with it is written on the guest too, for the host's view only, and only when the guest had none. Nothing else is stored, and nothing of the
/// answer is logged: the log carries the booking, the org, the outcome and a code.</para>
/// <para><b>The record.</b> Every read Octorate answered is one line of the activity log of the org, <c>ContactInfoRead</c> with the purpose
/// <see cref="OrgTeam.ContactReadPurposes.CheckInLink"/>: the booking, nobody as the actor (it is the system), never the address. A read that was refused writes
/// nothing: nothing was released.</para>
/// <para>Behind <see cref="Features.FeatureFlags.GuestContactInfo"/> (and <see cref="Features.FeatureFlags.OctorateConnector"/>): off, no call is made.</para>
/// </remarks>
public interface IGuestContactInfoService
{
    /// <summary>
    /// Reads the contact of the guest of <paramref name="bookingId"/> (a booking of <paramref name="orgId"/>) for the check-in link. The caller decides whether it may be
    /// asked again (<see cref="Entities.CheckInLinkDelivery.ContactReadAt"/>): this method asks whenever it is called and the booking qualifies. A transient failure of
    /// the channel manager (a limit, an outage) is thrown, not turned into «no contact».
    /// </summary>
    Task<GuestContactInfoOutcome> ReadForCheckInLinkAsync(Guid orgId, Guid bookingId, CancellationToken ct = default);
}

/// <summary>What <see cref="IGuestContactInfoService.ReadForCheckInLinkAsync"/> did.</summary>
public enum GuestContactInfoStatus
{
    /// <summary>The flag, or the connector, is off: nothing was asked.</summary>
    Disabled = 0,

    /// <summary>The booking does not qualify (not a stay from a portal on Octorate, not an active stay, an erased guest, no confirmed listing): nothing was asked.</summary>
    NotApplicable = 1,

    /// <summary>The guest already has an e-mail address: nothing was asked.</summary>
    AlreadyHasEmail = 2,

    /// <summary>The channel manager holds no e-mail address for the guest (an Airbnb guest has none), or gave none that can be used: the read was made (or not needed).</summary>
    NoEmailAtChannel = 3,

    /// <summary>The address was read and written on the guest of the booking.</summary>
    EmailFilled = 4,

    /// <summary>The channel manager refused (401 or 403): the static IP addresses are not registered, or the application has no permission. Nothing was released.</summary>
    StaticIpsRequired = 5,

    /// <summary>The channel manager does not know the reservation, or rejected the request: nothing was released, and a retry does not help.</summary>
    NotAvailable = 6,
}

/// <summary>The outcome of a read of the contact: the <paramref name="Status"/>, and for <see cref="GuestContactInfoStatus.NotAvailable"/> the stable code of the error.</summary>
public sealed record GuestContactInfoOutcome(GuestContactInfoStatus Status, string? ErrorCode = null);
