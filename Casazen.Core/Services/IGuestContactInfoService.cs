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
    Task<GuestContactInfoOutcome> ReadForCheckInLinkAsync(Guid orgId, Guid bookingId, CancellationToken ct = default);
}

public enum GuestContactInfoStatus
{
    Disabled = 0,

    NotApplicable = 1,

    AlreadyHasEmail = 2,

    NoEmailAtChannel = 3,

    EmailFilled = 4,

    StaticIpsRequired = 5,

    /// <summary>The channel manager does not know the reservation, or rejected the request: nothing was released, and a retry does not help.</summary>
    NotAvailable = 6,
}

public sealed record GuestContactInfoOutcome(GuestContactInfoStatus Status, string? ErrorCode = null);
