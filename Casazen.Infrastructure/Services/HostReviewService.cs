using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IHostReviewService"/>
/// <remarks>
/// <para>Reads and writes through the caller's tenant (the filter is on). The controller has already authorized the booking or the
/// property of the review; the list applies the caller's <see cref="HostScope"/> in SQL.</para>
/// <para><b>The invitation is the only thing here that writes to a third party</b>, and it leaves through one core, <see cref="InviteAsync"/>, from two
/// doors: the host's explicit request (<see cref="SendInviteAsync"/>) and the action <c>RequestReview</c> of a routine the host switched on
/// (<see cref="RequestInviteAsync"/>, E26/F01/US01/T01). Once per booking whoever asks: the row of <see cref="ReviewInvite"/> is unique, so the button, a
/// routine, two routines and an event delivered twice send one email. No job of its own and no reminder sends it. The row is written before the email
/// is queued and taken back if the email cannot be queued, so the host can ask again.</para>
/// <para>The routine's door names the org in every read (a routine has no caller and its job has no tenant) and is stricter than the button where nobody
/// decides: the stay of a no-show, and a guest who was erased, asked for the erasure or has no address, are skipped. A booking it must not invite is an answer
/// (<see cref="ReviewInviteRequestResult"/>), never an exception that makes the engine try again. Logs carry ids, never the address or the link.</para>
/// </remarks>
public sealed class HostReviewService(
    AppDbContext db,
    ReviewLinkTokens tokens,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    ILogger<HostReviewService> logger,
    TimeProvider? timeProvider = null) : IHostReviewService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<HostReviewPage> ListAsync(
        HostScope scope,
        Guid? propertyId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, ReviewRules.MaxPageSize);

        var query = db.Reviews.AsNoTracking().Where(r => r.OrgId == scope.OrgId).InScope(scope);
        if (propertyId is { } id)
            query = query.Where(r => r.PropertyId == id);

        var total = await query.CountAsync(cancellationToken);
        var rows = await Rows(query
                .OrderByDescending(r => r.PublishedAt)
                .ThenBy(r => r.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize))
            .ToListAsync(cancellationToken);

        return new HostReviewPage(rows.Select(ToHostReview).ToList(), total, page, pageSize);
    }

    public Task<Guid?> FindPropertyIdAsync(Guid reviewId, CancellationToken cancellationToken = default) =>
        db.Reviews
            .AsNoTracking()
            .Where(r => r.Id == reviewId)
            .Select(r => (Guid?)r.PropertyId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<HostReview> ReplyAsync(Guid reviewId, string text, CancellationToken cancellationToken = default)
    {
        var reply = ReviewRules.NormalizeText(text);
        if (reply is null || reply.Length > ReviewRules.ReplyMaxLength)
            throw new DomainRuleException("review_reply_invalid", "ReviewReplyInvalid");

        var review = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reviewId, cancellationToken);
        if (review is null || review.Status == ReviewStatus.Removed)
            throw new NotFoundException($"Review {reviewId} not found");
        if (review.HostReply is not null)
            throw new DomainConflictException("review_reply_already_sent", "ReviewReplyAlreadySent");

        // One conditional write: two answers sent at the same moment cannot both win, the second one finds the first.
        var now = _clock.GetUtcNow().UtcDateTime;
        var updated = await db.Reviews
            .Where(r => r.Id == reviewId && r.HostReply == null && r.Status != ReviewStatus.Removed)
            .ExecuteUpdateAsync(
                set => set.SetProperty(r => r.HostReply, reply).SetProperty(r => r.HostRepliedAt, now),
                cancellationToken);
        if (updated == 0)
            throw new DomainConflictException("review_reply_already_sent", "ReviewReplyAlreadySent");

        logger.LogInformation("Review {ReviewId} answered by the host", reviewId);
        return await ReadAsync(reviewId, cancellationToken);
    }

    public async Task<HostReview> ReportAsync(
        Guid reviewId,
        ReviewReportReason reason,
        string? note,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        var text = ReviewRules.NormalizeText(note);
        if (text is not null && text.Length > ReviewRules.ReportNoteMaxLength)
            throw new DomainRuleException("review_text_too_long", "ReviewTextTooLong");

        var review = await db.Reviews.FirstOrDefaultAsync(r => r.Id == reviewId, cancellationToken);
        if (review is null || review.Status == ReviewStatus.Removed)
            throw new NotFoundException($"Review {reviewId} not found");
        if (await db.ReviewReports.AnyAsync(x => x.ReviewId == reviewId, cancellationToken))
            throw new DomainConflictException("review_already_reported", "ReviewAlreadyReported");

        // The review stays public: reported is a mark for the staff, not a way to hide it.
        db.ReviewReports.Add(new ReviewReport
        {
            OrgId = review.OrgId,
            ReviewId = review.Id,
            Reason = reason,
            Note = text,
            ReportedByUserId = userId,
            ReportedAt = _clock.GetUtcNow().UtcDateTime,
            Status = ReviewReportStatus.Open,
        });
        review.Status = ReviewStatus.Reported;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Reported twice at the same moment: the other report committed first.
            db.ChangeTracker.Clear();
            throw new DomainConflictException("review_already_reported", "ReviewAlreadyReported");
        }

        logger.LogInformation("Review {ReviewId} reported by its host, reason {Reason}", reviewId, reason);
        return await ReadAsync(reviewId, cancellationToken);
    }

    public async Task<ReviewInviteStatus> GetInviteStatusAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.Source, b.Status, b.CheckOutDate, b.Guest.Email, b.Guest.DataAnonymizedDate })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"Booking {bookingId} not found");

        var sentAt = await db.ReviewInvites
            .AsNoTracking()
            .Where(i => i.BookingId == bookingId)
            .Select(i => (DateTime?)i.SentAt)
            .FirstOrDefaultAsync(cancellationToken);
        var reviewed = await db.Reviews.AnyAsync(r => r.BookingId == bookingId, cancellationToken);

        return new ReviewInviteStatus(
            ReviewRules.Evaluate(booking.Source, booking.Status, booking.CheckOutDate, _clock.TodayInRomeAsDateOnly()),
            HasAddress(booking.Email, booking.DataAnonymizedDate),
            sentAt,
            reviewed);
    }

    public async Task SendInviteAsync(Guid bookingId, string? userId, CancellationToken cancellationToken = default)
    {
        // The host's button: the reads go through the tenant of the caller, and the controller has authorized the booking.
        var result = await InviteAsync(bookingId, orgId: null, userId, cancellationToken);
        switch (result.Status)
        {
            case ReviewInviteRequestStatus.Sent:
                logger.LogInformation("Review invitation of booking {BookingId} queued at the request of the host", bookingId);
                return;
            case ReviewInviteRequestStatus.BookingNotFound:
                throw new NotFoundException($"Booking {bookingId} not found");
            case ReviewInviteRequestStatus.NotQueued:
                throw new DomainRuleException("review_invite_not_sent", "ReviewInviteNotSent");
            default:
                throw RefusalOf(result.Reason);
        }
    }

    public async Task<ReviewInviteRequestResult> RequestInviteAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (orgId == Guid.Empty)
            throw new ArgumentException("A request for a review is made for an org.", nameof(orgId));

        var result = await InviteAsync(bookingId, orgId, userId: null, cancellationToken);
        switch (result.Status)
        {
            case ReviewInviteRequestStatus.Sent:
                logger.LogInformation("Review invitation of booking {BookingId} of org {OrgId} queued at the request of a routine", bookingId, orgId);
                break;
            case ReviewInviteRequestStatus.Skipped:
                logger.LogInformation(
                    "Review invitation of booking {BookingId} of org {OrgId} skipped for a routine: {Reason}",
                    bookingId, orgId, result.Reason);
                break;
            case ReviewInviteRequestStatus.NotQueued:
                logger.LogWarning("Review invitation of booking {BookingId} of org {OrgId} was not queued for a routine", bookingId, orgId);
                break;
        }

        return result;
    }

    /// <summary>
    /// The one core of the invitation. <paramref name="orgId"/> is the org of a routine, named in every read and in the rules of the automatic request; it is
    /// <c>null</c> for the host's button, whose reads go through the tenant of the caller.
    /// </summary>
    private async Task<ReviewInviteRequestResult> InviteAsync(Guid bookingId, Guid? orgId, string? userId, CancellationToken cancellationToken)
    {
        var automatic = orgId is not null;
        var bookings = db.Bookings.AsNoTracking().Where(b => b.Id == bookingId);
        if (orgId is { } org)
            bookings = bookings.Where(b => b.OrgId == org);

        // What the email needs and what the rules read, nothing else of the guest or of the booking.
        var booking = await bookings
            .Select(b => new InviteBooking(
                b.Id,
                b.OrgId,
                b.Org.Slug,
                b.Source,
                b.Status,
                b.CheckInDate,
                b.CheckOutDate,
                b.NoShowAt,
                b.Property.Name,
                b.Guest.FirstName,
                b.Guest.Email,
                b.Guest.DataAnonymizedDate,
                b.Guest.ErasureRequested,
                b.Guest.IsDeleted))
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
            return ReviewInviteRequestResult.BookingNotFound;

        var eligibility = ReviewRules.Evaluate(booking.Source, booking.Status, booking.CheckOutDate, _clock.TodayInRomeAsDateOnly());
        if (eligibility != ReviewEligibility.Eligible)
            return ReviewInviteRequestResult.Skipped(SkipReasonOf(eligibility));

        // Where nobody decides (a routine), a stay that did not happen gets no thanks for it, and nobody who asked to be erased gets an email.
        if (automatic && booking.NoShowAt is not null)
            return ReviewInviteRequestResult.Skipped(ReviewInviteSkipReasons.NoShow);

        var address = booking.GuestEmail.Trim();
        var reachable = HasAddress(address, booking.GuestAnonymizedAt) && !(automatic && (booking.GuestErasureRequested || booking.GuestDeleted));
        if (!reachable)
            return ReviewInviteRequestResult.Skipped(ReviewInviteSkipReasons.NoEmail);

        if (await db.Reviews.AnyAsync(r => r.OrgId == booking.OrgId && r.BookingId == booking.Id, cancellationToken))
            return ReviewInviteRequestResult.Skipped(ReviewInviteSkipReasons.AlreadyReviewed);

        if (await db.ReviewInvites.AnyAsync(i => i.OrgId == booking.OrgId && i.BookingId == booking.Id, cancellationToken))
            return ReviewInviteRequestResult.Skipped(ReviewInviteSkipReasons.AlreadyInvited);

        // The link and the email are made first: a missing public URL is a configuration error, and nothing was marked as sent yet.
        var expiresAt = ReviewRules.WindowEndsAtUtc(booking.CheckOutDate);
        var reviewUrl = links.GuestReview(booking.OrgSlug, booking.Id, tokens.Create(booking.Id, new DateTimeOffset(expiresAt)));
        var content = EmailTemplates.ReviewInvite(
            EmailTemplates.DefaultCulture,
            booking.GuestFirstName,
            booking.PropertyName,
            booking.CheckInDate,
            booking.CheckOutDate,
            reviewUrl,
            expiresAt);

        var invite = new ReviewInvite
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            SentAt = _clock.GetUtcNow().UtcDateTime,
            SentByUserId = userId,
        };
        db.ReviewInvites.Add(invite);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Asked twice at the same moment: the other call marked it first, and sends the one email. Only this row is dropped from the unit of work: a
            // routine shares it with its own execution, which the engine still has to save.
            db.Entry(invite).State = EntityState.Detached;
            return ReviewInviteRequestResult.Skipped(ReviewInviteSkipReasons.AlreadyInvited);
        }

        if (!emailQueue.Enqueue(address, content, EmailTemplates.Names.ReviewInvite))
        {
            db.ReviewInvites.Remove(invite);
            await db.SaveChangesAsync(cancellationToken);
            return ReviewInviteRequestResult.NotQueued;
        }

        return ReviewInviteRequestResult.Sent;
    }

    /// <summary>The stable code a booking that is not to be invited is skipped with: the reason the rules of <see cref="ReviewRules"/> give.</summary>
    private static string SkipReasonOf(ReviewEligibility eligibility) => eligibility switch
    {
        ReviewEligibility.NotDirectBooking => ReviewInviteSkipReasons.NotDirect,
        ReviewEligibility.BookingNotConfirmed => ReviewInviteSkipReasons.NotConfirmed,
        ReviewEligibility.StayNotEnded => ReviewInviteSkipReasons.TooEarly,
        ReviewEligibility.WindowClosed => ReviewInviteSkipReasons.TooLate,
        _ => throw new ArgumentOutOfRangeException(nameof(eligibility), eligibility, "A booking that can be reviewed has no reason to be skipped."),
    };

    /// <summary>What the host's button answers when the booking is skipped: the errors of the button, unchanged (the API of <c>review-invite</c>).</summary>
    private static DomainException RefusalOf(string? reason) => reason switch
    {
        ReviewInviteSkipReasons.AlreadyReviewed => new DomainConflictException("review_already_submitted", "ReviewAlreadySubmitted"),
        ReviewInviteSkipReasons.AlreadyInvited => new DomainConflictException("review_invite_already_sent", "ReviewInviteAlreadySent"),
        _ => new DomainRuleException("review_invite_not_allowed", "ReviewInviteNotAllowed"),
    };

    private async Task<HostReview> ReadAsync(Guid reviewId, CancellationToken cancellationToken)
    {
        var row = await Rows(db.Reviews.AsNoTracking().Where(r => r.Id == reviewId)).FirstOrDefaultAsync(cancellationToken);
        return row is null ? throw new NotFoundException($"Review {reviewId} not found") : ToHostReview(row);
    }

    private IQueryable<HostReviewRow> Rows(IQueryable<Review> reviews) =>
        reviews.Select(r => new HostReviewRow(
            r.Id,
            r.PropertyId,
            r.Property.Name,
            r.BookingId,
            r.Rating,
            r.Text,
            r.AuthorName,
            r.StayEndedOn,
            r.PublishedAt,
            r.Status,
            r.HostReply,
            r.HostRepliedAt,
            db.ReviewReports.Where(x => x.ReviewId == r.Id).Select(x => (DateTime?)x.ReportedAt).FirstOrDefault()));

    private static HostReview ToHostReview(HostReviewRow r) => new(
        r.Id, r.PropertyId, r.PropertyName, r.BookingId, r.Rating, r.Text, r.AuthorName,
        ReviewRules.StayMonthOf(r.StayEndedOn), r.PublishedAt, r.Status, r.HostReply, r.HostRepliedAt, r.ReportedAt);

    // An erased guest keeps a placeholder address that is never deliverable (GuestDataEraser).
    private static bool HasAddress(string? email, DateTime? anonymizedAt) =>
        anonymizedAt is null && !string.IsNullOrWhiteSpace(email);

    /// <summary>The columns of a booking that the invitation reads (<see cref="InviteAsync"/>): the stay, the first name and the address of the guest, never more.</summary>
    private sealed record InviteBooking(
        Guid Id,
        Guid OrgId,
        string OrgSlug,
        BookingSource Source,
        BookingStatus Status,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        DateTime? NoShowAt,
        string PropertyName,
        string GuestFirstName,
        string GuestEmail,
        DateTime? GuestAnonymizedAt,
        bool GuestErasureRequested,
        bool GuestDeleted);

    private sealed record HostReviewRow(
        Guid Id,
        Guid PropertyId,
        string PropertyName,
        Guid BookingId,
        int Rating,
        string? Text,
        string AuthorName,
        DateTime StayEndedOn,
        DateTime PublishedAt,
        ReviewStatus Status,
        string? HostReply,
        DateTime? HostRepliedAt,
        DateTime? ReportedAt);
}
