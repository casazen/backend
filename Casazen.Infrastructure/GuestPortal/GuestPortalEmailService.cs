using Casazen.Core.Exceptions;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.GuestPortal;

/// <inheritdoc cref="IGuestPortalEmailService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only to the guest of the booking.</b> The address is read from the booking (<see cref="FiscalDocumentDeliveryService.TryUseAddress"/>: not an erased guest, not an invalid or reserved
/// address, not a guest of the demo); the caller gives none. An OTA booking whose guest has none (the portal does not give it) answers <see cref="GuestPortalResendOutcome.NoGuestEmail"/> and the
/// host copies the link instead, with nothing made or sent.</item>
/// <item><b>The link</b> is the active one of the booking, made now if there is none, by <see cref="IGuestPortalLinkService"/> (the one place that makes and prints it).</item>
/// <item><b>No check-in link in it.</b> The host's «resend» of the check-in link (<c>POST api/bookings/{id}/checkin/resend-link</c>) issues a new check-in link, and with it the previous one stops working; this e-mail
/// must not take away the link the guest holds. It carries the stay link, which is the guest's door to the portal, and nothing else of the check-in.</item>
/// <item><b>Brand and language</b> as every e-mail to a guest: the host's brand and sender name and the guest's language while <c>Features:GuestPortal</c> is on (the endpoint is behind the flag).</item>
/// <item><b>Logs</b> carry the ids of the booking and of the org: never the address, the name of the guest or the token.</item>
/// </list>
/// </remarks>
public sealed class GuestPortalEmailService(
    AppDbContext db,
    IGuestPortalLinkService links,
    IEmailQueue emailQueue,
    TimeProvider timeProvider,
    ILogger<GuestPortalEmailService> logger,
    IGuestEmailBrandingProvider? emailBranding = null,
    IGuestEmailLanguageProvider? emailLanguage = null) : IGuestPortalEmailService
{
    public async Task<GuestPortalResendResult> ResendPrepareArrivalAsync(
        Guid orgId,
        Guid bookingId,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        var facts = await db.Bookings
                .AsNoTracking()
                .Where(b => b.Id == bookingId && b.OrgId == orgId)
                .Select(b => new
                {
                    b.Status,
                    b.NoShowAt,
                    b.CheckInDate,
                    b.CheckOutDate,
                    GuestFirstName = b.Guest.FirstName,
                    GuestEmail = b.Guest.Email,
                    GuestAnonymizedAt = b.Guest.DataAnonymizedDate,
                    PropertyName = b.Property.Name,
                })
                .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (GuestPortalRules.IneligibleReason(facts.Status, facts.NoShowAt, facts.CheckOutDate, now) is not null)
            return GuestPortalResendResult.Refused(GuestPortalResendOutcome.NotEligible);

        if (!FiscalDocumentDeliveryService.TryUseAddress(facts.GuestEmail, facts.GuestAnonymizedAt, out var address))
        {
            logger.LogInformation("The stay e-mail of booking {BookingId} of org {OrgId} was not queued: the guest has no address to write to", bookingId, orgId);
            return GuestPortalResendResult.Refused(GuestPortalResendOutcome.NoGuestEmail);
        }

        var made = await links.GetOrCreateAsync(orgId, bookingId, userId, cancellationToken);
        if (made is null)
            return GuestPortalResendResult.Refused(GuestPortalResendOutcome.NotEligible);

        var culture = await emailLanguage.CultureOrAsync(orgId, bookingId, EmailTemplates.DefaultCulture, cancellationToken: cancellationToken);
        var branding = await emailBranding.ForOrgOrNullAsync(orgId, cancellationToken);
        var content = EmailTemplates.GuestPrepareArrival(
            culture,
            facts.GuestFirstName,
            facts.PropertyName,
            facts.CheckInDate,
            links.BuildUrl(made.Link),
            branding: branding);

        if (!emailQueue.Enqueue(address, content, EmailTemplates.Names.GuestPrepareArrival))
        {
            logger.LogWarning("The stay e-mail of booking {BookingId} of org {OrgId} was not queued: the queue of the e-mails did not take it", bookingId, orgId);
            return GuestPortalResendResult.Refused(GuestPortalResendOutcome.NotQueued);
        }

        logger.LogInformation("The stay e-mail of booking {BookingId} of org {OrgId} was queued for its guest (link {LinkId})", bookingId, orgId, made.Link.Id);
        return GuestPortalResendResult.Queued(made.Link);
    }
}
