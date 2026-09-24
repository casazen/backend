using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Emails of the "pay at the property" requests (BK-06, D5, A3-06), rendered from <see cref="EmailTemplates"/> and queued
/// on Hangfire (<see cref="IEmailQueue"/>): request received with the confirmation link (guest), new request to answer
/// (host, <c>Org.ContactEmail</c>), accepted / declined / expired (guest). Called after the change is saved: a failure
/// is logged (booking id only, no address) and never undoes the change. The push to the host is MO-04's.
/// </summary>
public sealed class OnSiteRequestNotifier(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    ILogger<OnSiteRequestNotifier> logger)
{
    /// <summary>To the guest: request received, confirm the email with <paramref name="token"/> by <c>RequestExpiresAt</c>.</summary>
    public Task RequestReceivedAsync(Guid bookingId, string token, CancellationToken cancellationToken = default) =>
        SendAsync(bookingId, EmailTemplates.Names.OnSiteRequestReceived, cancellationToken, data =>
        {
            if (data.RequestExpiresAt is not { } confirmBy)
                return null;

            var content = EmailTemplates.OnSiteRequestReceived(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.PropertyName,
                data.CheckInDate,
                data.CheckOutDate,
                data.TotalPrice,
                links.OnSiteRequestConfirmation(data.OrgSlug, data.BookingId, token),
                confirmBy);
            return (data.GuestEmail, content);
        });

    /// <summary>To the host: a request whose guest confirmed the email, to answer by <c>RequestExpiresAt</c>.</summary>
    public Task HostNewRequestAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        SendAsync(bookingId, EmailTemplates.Names.OnSiteRequestToHost, cancellationToken, data =>
        {
            if (data.RequestExpiresAt is not { } answerBy)
                return null;

            var content = EmailTemplates.OnSiteRequestToHost(
                EmailTemplates.DefaultCulture,
                $"{data.GuestFirstName} {data.GuestLastName}".Trim(),
                data.PropertyName,
                data.CheckInDate,
                data.CheckOutDate,
                data.NumberOfGuests,
                data.TotalPrice,
                answerBy,
                links.HostBookingRequests());
            return (data.HostEmail, content);
        });

    /// <summary>To the guest: the host accepted, the booking is confirmed.</summary>
    public Task AcceptedAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        SendAsync(bookingId, EmailTemplates.Names.OnSiteRequestAccepted, cancellationToken, data =>
            (data.GuestEmail, EmailTemplates.OnSiteRequestAccepted(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.PropertyName,
                data.CheckInDate,
                data.CheckOutDate,
                data.TotalPrice,
                data.BookingId.ToString("D"))));

    /// <summary>To the guest: the host declined, with the host's optional message.</summary>
    public Task DeclinedAsync(Guid bookingId, string? hostMessage, CancellationToken cancellationToken = default) =>
        SendAsync(bookingId, EmailTemplates.Names.OnSiteRequestDeclined, cancellationToken, data =>
            (data.GuestEmail, EmailTemplates.OnSiteRequestDeclined(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.PropertyName,
                data.CheckInDate,
                data.CheckOutDate,
                hostMessage)));

    /// <summary>To the guest: the host did not answer in time.</summary>
    public Task ExpiredAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        SendAsync(bookingId, EmailTemplates.Names.OnSiteRequestExpired, cancellationToken, data =>
            (data.GuestEmail, EmailTemplates.OnSiteRequestExpired(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.PropertyName,
                data.CheckInDate,
                data.CheckOutDate)));

    private async Task SendAsync(
        Guid bookingId,
        string template,
        CancellationToken cancellationToken,
        Func<RequestEmailData, (string? To, EmailContent Content)?> render)
    {
        try
        {
            // The anonymous confirmation, the host's console and the jobs all reach the booking by id: the tenant filter
            // of a host request already limits it to the host's org.
            var data = await db.Bookings
                .AsNoTracking()
                .Where(b => b.Id == bookingId)
                .Select(b => new RequestEmailData(
                    b.Id,
                    b.Guest.FirstName,
                    b.Guest.LastName,
                    b.Guest.Email,
                    b.Property.Name,
                    b.Org.Slug,
                    b.Org.ContactEmail,
                    b.CheckInDate,
                    b.CheckOutDate,
                    b.NumberOfGuests,
                    b.TotalPrice,
                    b.RequestExpiresAt))
                .SingleOrDefaultAsync(cancellationToken);
            if (data is null)
            {
                logger.LogWarning("Email {Template} for booking {BookingId} skipped: booking not found", template, bookingId);
                return;
            }

            if (render(data) is not { } email)
            {
                logger.LogWarning("Email {Template} for booking {BookingId} skipped: the request has no deadline", template, bookingId);
                return;
            }

            if (!emailQueue.Enqueue(email.To, email.Content, template))
                logger.LogWarning("Email {Template} for booking {BookingId} was not queued", template, bookingId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Email {Template} for booking {BookingId} could not be prepared", template, bookingId);
        }
    }

    private sealed record RequestEmailData(
        Guid BookingId,
        string GuestFirstName,
        string GuestLastName,
        string GuestEmail,
        string PropertyName,
        string OrgSlug,
        string HostEmail,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        int NumberOfGuests,
        decimal TotalPrice,
        DateTime? RequestExpiresAt);
}
