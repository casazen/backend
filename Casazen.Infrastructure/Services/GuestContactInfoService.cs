using System.Net.Mail;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.OrgTeam;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestContactInfoService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The rules, in this order</b>; the first that does not hold ends the call and nothing is asked: the flags <see cref="FeatureFlags.GuestContactInfo"/> and
/// <see cref="FeatureFlags.OctorateConnector"/> are on; the booking is one of the org that came from a portal through the channel manager
/// (<see cref="OtaChannelActionPolicy.IsChannelBooking(Booking)"/>) and is an active stay (<see cref="AlloggiatiStatusRules.IsDue"/>, the one definition: confirmed, checked in or checked out, not a no-show); its guest was not erased, anonymized
/// or deleted; the guest has <b>no</b> e-mail address yet; the property of the booking has a confirmed listing. Then the reservation is read, which gives flags and no
/// data (<c>emailAvailable</c>): when the channel manager says it holds no address, the contact is <b>not asked for</b> (an Airbnb guest has none, and a sensitive read of
/// nothing is still a read Octorate records).</item>
/// <item><b>One booking, one read.</b> The contact of THIS booking is read, for the check-in link: never a list, never a guest that has no booking, never into the CRM
/// (neither <c>IOctorateContactsService</c> nor the CRM store is called: the CRM path is separate and stays off). Whether the read may be repeated is the caller's rule
/// (<see cref="CheckInLinkDelivery.ContactReadAt"/>).</item>
/// <item><b>A refusal is a state, not an empty contact.</b> A 401 or a 403 (<see cref="OctorateSensitiveDataNotAllowedException"/>: the static IP addresses are not
/// registered, or the application has no permission) is <see cref="GuestContactInfoStatus.StaticIpsRequired"/>; nothing was released, so no line of the activity log is
/// written. A failure that says nothing about the permission (a limit, an outage, an unreadable answer) is thrown, never read as «no contact».</item>
/// <item><b>What is kept.</b> The address that Octorate answered, when it is one address (<c>MailAddress</c>) of at most 255 characters, is written on the guest of the
/// booking: from then on the e-mail of the link goes to it as to any booking with an address. A phone number is written on the guest too when it had none (at most 20
/// characters), for the host's view only. Nothing else. A portal usually gives a forward address of its own, which is the one the guest answers from.</item>
/// <item><b>The record.</b> Every read that Octorate answered is one line of the activity log, <see cref="OrgActivityType.ContactInfoRead"/> with the purpose
/// <see cref="ContactReadPurposes.CheckInLink"/>, the booking as the subject and nobody as the actor (the system reads it); saved in the same unit of work as the address.</item>
/// <item><b>Logs</b> carry the booking, the org, the status and a code: never an address, a number or a name.</item>
/// </list>
/// </remarks>
public sealed class GuestContactInfoService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IOctorateBookingTargets targets,
    IFeatureFlags featureFlags,
    IActivityLog activityLog,
    ILogger<GuestContactInfoService> logger,
    TimeProvider? timeProvider = null) : IGuestContactInfoService
{
    /// <summary>The longest e-mail address and phone number of a guest (the columns of <see cref="Guest"/>).</summary>
    private const int EmailMaxLength = 255;

    private const int PhoneMaxLength = 20;

    /// <summary>The code of an answer of the channel manager that CasaZen could not read (not an object, none of the members of a contact).</summary>
    public const string UnrecognizedCode = "octorate_contact_info_unrecognized";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<GuestContactInfoOutcome> ReadForCheckInLinkAsync(Guid orgId, Guid bookingId, CancellationToken ct = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector) || !featureFlags.IsEnabled(FeatureFlags.GuestContactInfo))
            return new GuestContactInfoOutcome(GuestContactInfoStatus.Disabled);

        var booking = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.OrgId == orgId, ct);
        if (booking is null
            || !OtaChannelActionPolicy.IsChannelBooking(booking)
            || !AlloggiatiStatusRules.IsDue(booking)
            || booking.Guest is not { IsDeleted: false, ErasureRequested: false, DataAnonymizedDate: null } guest)
        {
            return new GuestContactInfoOutcome(GuestContactInfoStatus.NotApplicable);
        }

        if (!string.IsNullOrWhiteSpace(guest.Email))
            return new GuestContactInfoOutcome(GuestContactInfoStatus.AlreadyHasEmail);

        if (await targets.AccommodationOfAsync(booking.PropertyId, ct) is not { } accommodation)
            return new GuestContactInfoOutcome(GuestContactInfoStatus.NotApplicable);

        // The reservation first: it says, with flags and no data, whether the channel manager holds an address at all.
        OctorateReservation reservation;
        try
        {
            using (orgScope.Use(orgId))
                reservation = await client.GetReservationAsync(accommodation, booking.OctorateReservationId!, ct);
        }
        catch (OctorateNotFoundException ex)
        {
            logger.LogWarning("The reservation of booking {BookingId} (org {OrgId}) is not known to the channel manager ({Code})", bookingId, orgId, ex.Code);
            return new GuestContactInfoOutcome(GuestContactInfoStatus.NotAvailable, ex.Code);
        }

        if (reservation.GuestEmailAvailable != true || string.IsNullOrWhiteSpace(reservation.Refer))
        {
            logger.LogInformation("The channel manager holds no e-mail address for the guest of booking {BookingId} (org {OrgId}): the contact is not asked for", bookingId, orgId);
            return new GuestContactInfoOutcome(GuestContactInfoStatus.NoEmailAtChannel);
        }

        OctorateGuestContactInfo contact;
        try
        {
            using (orgScope.Use(orgId))
                contact = await client.GetReservationContactInfoAsync(accommodation, reservation.Refer.Trim(), ct);
        }
        catch (OctorateSensitiveDataNotAllowedException)
        {
            // The static IP addresses of CasaZen are not registered with the channel manager (or the application has no permission): nothing was released.
            logger.LogWarning(
                "The contact of the guest of booking {BookingId} (org {OrgId}) was refused by the channel manager: the static IP addresses are not registered", bookingId, orgId);
            return new GuestContactInfoOutcome(GuestContactInfoStatus.StaticIpsRequired);
        }
        catch (Exception ex) when (ex is OctorateNotFoundException or OctorateRequestRejectedException)
        {
            logger.LogWarning("The contact of the guest of booking {BookingId} (org {OrgId}) was not released by the channel manager ({Code})", bookingId, orgId, ((OctorateException)ex).Code);
            return new GuestContactInfoOutcome(GuestContactInfoStatus.NotAvailable, ((OctorateException)ex).Code);
        }

        // The channel manager answered, so it recorded the read and so does CasaZen: the booking and the purpose, nobody as the actor, never the address.
        activityLog.Record(OrgActivity.Of(
            orgId,
            OrgActivityType.ContactInfoRead,
            null,
            bookingId.ToString(),
            (OrgActivityDetailKeys.Purpose, ContactReadPurposes.CheckInLink)));

        if (!contact.Recognized)
        {
            await db.SaveChangesAsync(ct);
            logger.LogWarning("The contact of the guest of booking {BookingId} (org {OrgId}) came in a shape CasaZen does not read ({Code})", bookingId, orgId, UnrecognizedCode);
            return new GuestContactInfoOutcome(GuestContactInfoStatus.NotAvailable, UnrecognizedCode);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var address = contact.Email?.Trim();
        var usable = !string.IsNullOrEmpty(address)
            && address.Length <= EmailMaxLength
            && MailAddress.TryCreate(address, out var parsed)
            && string.Equals(parsed.Address, address, StringComparison.OrdinalIgnoreCase);
        if (usable)
        {
            guest.Email = address!;
            guest.UpdatedAt = now;
        }

        // The phone number is for the host's view of the booking only, and only where the guest had none.
        var phone = contact.Phone?.Trim();
        if (string.IsNullOrWhiteSpace(guest.PhoneNumber) && !string.IsNullOrEmpty(phone) && phone.Length <= PhoneMaxLength)
        {
            guest.PhoneNumber = phone;
            guest.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "The contact of the guest of booking {BookingId} (org {OrgId}) was read for the check-in link: {Status}",
            bookingId,
            orgId,
            usable ? GuestContactInfoStatus.EmailFilled : GuestContactInfoStatus.NoEmailAtChannel);
        return new GuestContactInfoOutcome(usable ? GuestContactInfoStatus.EmailFilled : GuestContactInfoStatus.NoEmailAtChannel);
    }
}
