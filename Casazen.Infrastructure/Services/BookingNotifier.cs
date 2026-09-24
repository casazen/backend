using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Emails of the booking status (BK-10, A3-11, #58), rendered from <see cref="EmailTemplates"/> and queued on Hangfire
/// (<see cref="IEmailQueue"/>): confirmation to the guest and new booking to the host, cancellation to the guest, and the
/// deferred charge of "Paga più tardi" (BK-08: failed charge with the link to pay, automatic cancellation). Called
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
                links.GuestBookings(data.OrgSlug)));

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
    /// The deferred charge failed and needs the guest (BK-08, A3-14): the guest gets the link to pay on the checkout outcome
    /// page (<paramref name="checkoutToken"/>, the raw token whose hash was just stored), the host is told. The last day to
    /// pay is the day before <paramref name="cancellationDay"/> when an automatic cancellation applies.
    /// </summary>
    public async Task DeferredChargeFailedAsync(
        Guid bookingId,
        string checkoutToken,
        DateOnly? cancellationDay,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutToken);
        var data = await TryLoadAsync(bookingId, "Deferred charge failure", cancellationToken);
        if (data is null)
            return;

        var cancelOn = cancellationDay?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        Queue(bookingId, EmailTemplates.Names.GuestDeferredChargeFailed, data.GuestEmail, () =>
            EmailTemplates.GuestDeferredChargeFailed(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.Summary,
                links.CheckoutOutcome(data.OrgSlug, data.BookingId, checkoutToken),
                cancelOn?.AddDays(-1),
                data.HostContact));
        AlertHostOfFailedDeferredCharge(data, guestAsked: true, cancelOn);
    }

    /// <summary>
    /// Every attempt of the deferred charge ended without a payment the guest could complete (Stripe unreachable, no saved
    /// payment method): the host only, who handles the payment with the guest (BK-08).
    /// </summary>
    public async Task DeferredChargeNotAttemptedAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var data = await TryLoadAsync(bookingId, "Deferred charge failure", cancellationToken);
        if (data is not null)
            AlertHostOfFailedDeferredCharge(data, guestAsked: false, cancelOnDay: null);
    }

    /// <summary>
    /// "Paga più tardi" not paid in time: the system cancelled the booking and released its dates (BK-08). The guest gets
    /// the standard cancellation email with its own cause (nothing charged), the host a notice.
    /// </summary>
    public async Task DeferredChargeCancelledAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var data = await TryLoadAsync(bookingId, "Cancellation", cancellationToken);
        if (data is null)
            return;

        Queue(bookingId, EmailTemplates.Names.GuestBookingCancelled, data.GuestEmail, () =>
            EmailTemplates.GuestBookingCancelled(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.Summary.PropertyName,
                data.Summary.CheckInDate,
                data.Summary.CheckOutDate,
                refundedEur: 0m,
                refundStartedEur: 0m,
                data.HostContact,
                BookingCancellationEmailCause.DeferredPaymentNotCompleted));
        Queue(bookingId, EmailTemplates.Names.HostDeferredChargeCancelled, data.HostEmail, () =>
            EmailTemplates.HostDeferredChargeCancelled(
                EmailTemplates.DefaultCulture,
                $"{data.GuestFirstName} {data.GuestLastName}".Trim(),
                data.Summary,
                links.HostBooking(data.BookingId)));
    }

    private void AlertHostOfFailedDeferredCharge(BookingEmailData data, bool guestAsked, DateTime? cancelOnDay) =>
        Queue(data.BookingId, EmailTemplates.Names.HostDeferredChargeFailed, data.HostEmail, () =>
            EmailTemplates.HostDeferredChargeFailed(
                EmailTemplates.DefaultCulture,
                $"{data.GuestFirstName} {data.GuestLastName}".Trim(),
                data.Summary,
                guestAsked,
                cancelOnDay,
                links.HostBooking(data.BookingId)));

    private async Task<BookingEmailData?> TryLoadAsync(Guid bookingId, string what, CancellationToken cancellationToken)
    {
        try
        {
            var data = await LoadAsync(bookingId, cancellationToken);
            if (data is null)
                logger.LogWarning("{What} emails of booking {BookingId} skipped: booking not found", what, bookingId);
            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{What} emails of booking {BookingId} could not be prepared", what, bookingId);
            return null;
        }
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
            row.Id.ToString("D"),
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
