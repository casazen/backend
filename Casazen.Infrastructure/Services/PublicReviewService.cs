using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPublicReviewService"/>
/// <remarks>
/// Anonymous: the tenant filter is off, so every query names the org. A link that cannot be read, that is for another booking, that is
/// expired or whose booking is gone gets the same answer (422 <c>review_link_invalid</c>): the page does not tell a stranger which one it
/// was. The review is published when it is saved; nothing else is written, and nothing is sent to the guest.
/// </remarks>
public sealed class PublicReviewService(
    AppDbContext db,
    ReviewLinkTokens tokens,
    ILogger<PublicReviewService> logger,
    TimeProvider? timeProvider = null) : IPublicReviewService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<PublicReviewPage?> ListAsync(
        Guid orgId,
        string propertySlugOrId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var propertyId = await FindPublishedPropertyIdAsync(orgId, propertySlugOrId, cancellationToken);
        if (propertyId is null)
            return null;

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, ReviewRules.MaxPageSize);

        var query = PublicReviews(orgId, propertyId.Value);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(r => r.PublishedAt)
            .ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ProjectPublic)
            .ToListAsync(cancellationToken);

        return new PublicReviewPage(rows.Select(ToPublic).ToList(), total, page, pageSize);
    }

    public async Task<PublicRatingSummary?> GetSummaryAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        var stats = await PublicReviews(orgId, propertyId)
            .GroupBy(r => r.PropertyId)
            .Select(g => new { Count = g.Count(), Sum = g.Sum(r => (long)r.Rating) })
            .FirstOrDefaultAsync(cancellationToken);

        // Nothing is shown for a property without reviews: not a zero, not an empty star.
        return stats is null || stats.Count == 0
            ? null
            : new PublicRatingSummary(stats.Count, ReviewRules.Average(stats.Sum, stats.Count));
    }

    public async Task<ReviewLinkInfo> GetLinkInfoAsync(Guid bookingId, string token, CancellationToken cancellationToken = default)
    {
        var booking = await LoadBookingOfLinkAsync(bookingId, token, cancellationToken);

        var existing = await db.Reviews
            .AsNoTracking()
            .Where(r => r.BookingId == bookingId)
            .Select(ProjectPublic)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
            return new ReviewLinkInfo(booking.PropertyName, booking.CheckInDate, booking.CheckOutDate, ToPublic(existing));

        EnsureCanBeReviewed(booking);
        return new ReviewLinkInfo(booking.PropertyName, booking.CheckInDate, booking.CheckOutDate, null);
    }

    public async Task<PublicReview> SubmitAsync(
        Guid bookingId,
        string token,
        SubmitReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var booking = await LoadBookingOfLinkAsync(bookingId, token, cancellationToken);

        if (command.Rating is < ReviewRules.MinRating or > ReviewRules.MaxRating)
            throw new DomainRuleException("review_rating_invalid", "ReviewRatingInvalid");

        var text = ReviewRules.NormalizeText(command.Text);
        if (text is not null && text.Length > ReviewRules.TextMaxLength)
            throw new DomainRuleException("review_text_too_long", "ReviewTextTooLong");

        if (await db.Reviews.AnyAsync(r => r.BookingId == bookingId, cancellationToken))
            throw AlreadySubmitted();

        EnsureCanBeReviewed(booking);

        var now = _clock.GetUtcNow().UtcDateTime;
        var review = new Review
        {
            OrgId = booking.OrgId,
            PropertyId = booking.PropertyId,
            BookingId = booking.Id,
            Rating = command.Rating,
            Text = text,
            AuthorName = ReviewRules.AuthorNameFor(booking.GuestFirstName, command.ShowName),
            StayEndedOn = RomeCalendar.DateInRome(booking.CheckOutDate).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Status = ReviewStatus.Published,
            PublishedAt = now,
        };

        db.Reviews.Add(review);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same link opened twice: the other submission committed first.
            db.Entry(review).State = EntityState.Detached;
            throw AlreadySubmitted();
        }

        logger.LogInformation("Review {ReviewId} of booking {BookingId} published, rating {Rating}", review.Id, bookingId, review.Rating);

        return new PublicReview(
            review.Id, review.Rating, review.Text, review.AuthorName, ReviewRules.StayMonthOf(review.StayEndedOn),
            review.PublishedAt, review.HostReply, review.HostRepliedAt);
    }

    // The public reviews of a property: published ones and reported ones (they stay visible until the staff decides), never a removed one.
    private IQueryable<Review> PublicReviews(Guid orgId, Guid propertyId) =>
        db.Reviews
            .AsNoTracking()
            .Where(r => r.OrgId == orgId && r.PropertyId == propertyId && r.Status != ReviewStatus.Removed);

    private async Task<Guid?> FindPublishedPropertyIdAsync(Guid orgId, string propertySlugOrId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(propertySlugOrId))
            return null;

        // The same rule as the property page: a property that is not on the site has no reviews on it either.
        var query = db.Properties
            .AsNoTracking()
            .Where(PublicListing.IsPublished)
            .Where(p => p.OrgId == orgId && p.Org.IsActive);
        query = Guid.TryParse(propertySlugOrId, out var id)
            ? query.Where(p => p.Id == id)
            : query.Where(p => p.Slug == propertySlugOrId);

        return await query.Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<LinkBooking> LoadBookingOfLinkAsync(Guid bookingId, string token, CancellationToken cancellationToken)
    {
        if (!tokens.TryRead(token, out var tokenBookingId) || tokenBookingId != bookingId)
            throw InvalidLink();

        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId && b.Org.IsActive)
            .Select(b => new LinkBooking(
                b.Id, b.OrgId, b.PropertyId, b.Property.Name, b.CheckInDate, b.CheckOutDate, b.Status, b.Source, b.Guest.FirstName))
            .FirstOrDefaultAsync(cancellationToken);

        return booking ?? throw InvalidLink();
    }

    private void EnsureCanBeReviewed(LinkBooking booking)
    {
        var eligibility = ReviewRules.Evaluate(booking.Source, booking.Status, booking.CheckOutDate, _clock.TodayInRomeAsDateOnly());
        if (eligibility != ReviewEligibility.Eligible)
            throw new DomainConflictException("review_not_available", "ReviewNotAvailable");
    }

    private static DomainRuleException InvalidLink() => new("review_link_invalid", "ReviewLinkInvalid");

    private static DomainConflictException AlreadySubmitted() => new("review_already_submitted", "ReviewAlreadySubmitted");

    private static readonly System.Linq.Expressions.Expression<Func<Review, PublicReviewRow>> ProjectPublic =
        r => new PublicReviewRow(r.Id, r.Rating, r.Text, r.AuthorName, r.StayEndedOn, r.PublishedAt, r.HostReply, r.HostRepliedAt);

    private static PublicReview ToPublic(PublicReviewRow row) => new(
        row.Id, row.Rating, row.Text, row.AuthorName, ReviewRules.StayMonthOf(row.StayEndedOn), row.PublishedAt, row.HostReply, row.HostRepliedAt);

    private sealed record PublicReviewRow(
        Guid Id,
        int Rating,
        string? Text,
        string AuthorName,
        DateTime StayEndedOn,
        DateTime PublishedAt,
        string? HostReply,
        DateTime? HostRepliedAt);

    private sealed record LinkBooking(
        Guid Id,
        Guid OrgId,
        Guid PropertyId,
        string PropertyName,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        BookingStatus Status,
        BookingSource Source,
        string GuestFirstName);
}
