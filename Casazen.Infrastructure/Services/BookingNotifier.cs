using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Emails of the booking status (BK-10, A3-11, #58), rendered from <see cref="EmailTemplates"/> and queued on Hangfire
/// (<see cref="IEmailQueue"/>): confirmation to the guest and new booking to the host, cancellation to the guest. Called
/// once the change is committed, by the code that made the transition (payment webhook, saved card, host acceptance,
/// host cancellation), so a duplicate webhook that changes nothing sends nothing. A failure is logged with the booking
/// id only and never undoes the change.
/// </summary>
/// <remarks>
/// Recipients have no language preference and the booking does not record the checkout language: the emails go out in
/// Italian (<see cref="EmailTemplates.DefaultCulture"/>), the English texts are ready. The emails of the "pay at the
/// property" requests are <see cref="OnSiteRequestNotifier"/>'s, the refund confirmations
/// <see cref="PaymentRefundService"/>'s.
/// </remarks>
public sealed class BookingNotifier(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    ILogger<BookingNotifier> logger)
{
    /// <summary>
    /// The booking has just become confirmed (<paramref name="kind"/>): confirmation to the guest and, unless the host
    /// confirmed it (<see cref="BookingConfirmationKind.OnSite"/>), the new booking to the host. Skipped when the booking
    /// is no longer confirmed (cancelled in the meantime).
    /// </summary>
    public async Task BookingConfirmedAsync(Guid bookingId, BookingConfirmationKind kind, CancellationToken cancellationToken = default)
    {
        BookingEmailData? data;
        try
        {
            data = await LoadAsync(bookingId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Confirmation emails of booking {BookingId} could not be prepared", bookingId);
            return;
        }

        if (data is null)
        {
            logger.LogWarning("Confirmation emails of booking {BookingId} skipped: booking not found", bookingId);
            return;
        }

        if (data.Status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut))
        {
            logger.LogWarning(
                "Confirmation emails of booking {BookingId} skipped: the booking is {Status} by now",
                bookingId,
                data.Status);
            return;
        }

        Queue(bookingId, EmailTemplates.Names.GuestBookingConfirmed, data.GuestEmail, () =>
            EmailTemplates.GuestBookingConfirmed(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.Summary,
                kind,
                data.PaidAmount,
                data.FreeRefundDeadline,
                data.HostContact,
                links.GuestBookings(data.OrgSlug, data.Summary.BookingCode)));

        if (kind != BookingConfirmationKind.OnSite)
            AlertHostOfNewBooking(data, kind);
    }

    /// <summary>
    /// The host cancelled the booking: one email to the guest with the refund Stripe already confirmed
    /// (<paramref name="refundedAmount"/>, whose own confirmation email the caller has claimed) and the refund still in
    /// progress (<paramref name="refundStartedAmount"/>, confirmed by a later email). The host's reason is never sent.
    /// </summary>
    public async Task BookingCancelledByHostAsync(
        Guid bookingId,
        decimal refundedAmount,
        decimal refundStartedAmount,
        CancellationToken cancellationToken = default)
    {
        BookingEmailData? data;
        try
        {
            data = await LoadAsync(bookingId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Cancellation email of booking {BookingId} could not be prepared", bookingId);
            return;
        }

        if (data is null)
        {
            logger.LogWarning("Cancellation email of booking {BookingId} skipped: booking not found", bookingId);
            return;
        }

        Queue(bookingId, EmailTemplates.Names.GuestBookingCancelled, data.GuestEmail, () =>
            EmailTemplates.GuestBookingCancelled(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.Summary.PropertyName,
                data.Summary.CheckInDate,
                data.Summary.CheckOutDate,
                refundedAmount,
                refundStartedAmount,
                data.HostContact));
    }

    /// <summary>
    /// The only place where the host learns of a new booking confirmed without their action: today by email to
    /// <c>Org.ContactEmail</c>. Extension point of MO-04: the push "new booking" to the host's devices goes here, next
    /// to the email, so both channels fire once per confirmation.
    /// </summary>
    private void AlertHostOfNewBooking(BookingEmailData data, BookingConfirmationKind kind) =>
        Queue(data.BookingId, EmailTemplates.Names.HostBookingConfirmed, data.HostEmail, () =>
            EmailTemplates.HostBookingConfirmed(
                EmailTemplates.DefaultCulture,
                $"{data.GuestFirstName} {data.GuestLastName}".Trim(),
                data.Summary,
                kind,
                data.PaidAmount,
                data.FreeRefundDeadline,
                links.HostBooking(data.BookingId)));

    private void Queue(Guid bookingId, string template, string? to, Func<EmailContent> render)
    {
        try
        {
            if (!emailQueue.Enqueue(to, render(), template))
                logger.LogWarning("Email {Template} for booking {BookingId} was not queued", template, bookingId);
        }
        catch (Exception ex)
        {
            // E.g. App:PublicSiteBaseUrl missing (Development/Testing only): the booking change stays.
            logger.LogError(ex, "Email {Template} for booking {BookingId} could not be prepared", template, bookingId);
        }
    }

    /// <summary>
    /// The booking with what its emails show. Read by id: the payment webhook and the jobs have no tenant, and a host
    /// request is already limited to the host's org by the tenant filter.
    /// </summary>
    private async Task<BookingEmailData?> LoadAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var row = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new
            {
                b.Id,
                b.BookingCode,
                b.Status,
                GuestFirstName = b.Guest.FirstName,
                GuestLastName = b.Guest.LastName,
                GuestEmail = b.Guest.Email,
                PropertyName = b.Property.Name,
                OrgSlug = b.Org.Slug,
                OrgDisplayName = b.Org.DisplayName,
                OrgContactEmail = b.Org.ContactEmail,
                b.CheckInDate,
                b.CheckOutDate,
                b.NumberOfGuests,
                b.BasePrice,
                b.CleaningFee,
                b.TouristTax,
                b.TotalPrice,
                b.FreeRefundDeadline,
                PaidAmount = b.Payments.Where(p => p.Status == PaymentStatus.Completed).Sum(p => p.Amount),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        var summary = new BookingEmailSummary(
            BookingCodes.Format(row.BookingCode),
            row.PropertyName,
            row.CheckInDate,
            row.CheckOutDate,
            row.NumberOfGuests,
            row.BasePrice - row.CleaningFee,
            row.CleaningFee,
            row.TouristTax,
            row.TotalPrice);
        return new BookingEmailData(
            row.Id,
            row.Status,
            row.GuestFirstName,
            row.GuestLastName,
            row.GuestEmail,
            row.OrgSlug,
            row.OrgContactEmail,
            new BookingHostContact(row.OrgDisplayName, row.OrgContactEmail),
            summary,
            row.PaidAmount,
            row.FreeRefundDeadline);
    }

    private sealed record BookingEmailData(
        Guid BookingId,
        BookingStatus Status,
        string GuestFirstName,
        string GuestLastName,
        string GuestEmail,
        string OrgSlug,
        string HostEmail,
        BookingHostContact HostContact,
        BookingEmailSummary Summary,
        decimal PaidAmount,
        DateTime? FreeRefundDeadline);
}
