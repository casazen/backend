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
/// <para><b>The invitation is the only thing here that writes to a third party</b>, and it leaves from <see cref="SendInviteAsync"/> alone:
/// when the host asks, once per booking. No job, no reminder, no event sends it. The row of <see cref="ReviewInvite"/> is written before the
/// email is queued and taken back if the email cannot be queued, so the host can ask again.</para>
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
        var booking = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Include(b => b.Property)
            .Include(b => b.Org)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
            ?? throw new NotFoundException($"Booking {bookingId} not found");

        var eligibility = ReviewRules.Evaluate(booking.Source, booking.Status, booking.CheckOutDate, _clock.TodayInRomeAsDateOnly());
        var address = booking.Guest.Email.Trim();
        if (eligibility != ReviewEligibility.Eligible || !HasAddress(address, booking.Guest.DataAnonymizedDate))
            throw new DomainRuleException("review_invite_not_allowed", "ReviewInviteNotAllowed");

        if (await db.Reviews.AnyAsync(r => r.BookingId == bookingId, cancellationToken))
            throw new DomainConflictException("review_already_submitted", "ReviewAlreadySubmitted");

        // The link and the email are made first: a missing public URL is a configuration error, and nothing was marked as sent yet.
        var expiresAt = ReviewRules.WindowEndsAtUtc(booking.CheckOutDate);
        var reviewUrl = links.GuestReview(booking.Org.Slug, booking.Id, tokens.Create(booking.Id, new DateTimeOffset(expiresAt)));
        var content = EmailTemplates.ReviewInvite(
            EmailTemplates.DefaultCulture,
            booking.Guest.FirstName,
            booking.Property.Name,
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
            // The host asked twice at the same moment: the other call marked it first, and sends the one email.
            db.ChangeTracker.Clear();
            throw new DomainConflictException("review_invite_already_sent", "ReviewInviteAlreadySent");
        }

        if (!emailQueue.Enqueue(address, content, EmailTemplates.Names.ReviewInvite))
        {
            db.ReviewInvites.Remove(invite);
            await db.SaveChangesAsync(cancellationToken);
            throw new DomainRuleException("review_invite_not_sent", "ReviewInviteNotSent");
        }

        logger.LogInformation("Review invitation of booking {BookingId} queued at the request of the host", bookingId);
    }

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
