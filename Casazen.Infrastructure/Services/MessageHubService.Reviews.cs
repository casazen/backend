using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The reviews in the hub (E05/F06/US01/T03, NF-69): the list under the filter <c>kind=review</c> and the detail of one review. A review is not a conversation: it is never in the default view
/// of the hub, and it is opened with its own detail, never with the endpoints of a conversation (a key of the one kind is a 404 for the other). The same scope rules as the conversations: the
/// org of the scope is named in every query and <c>InScope</c> narrows both sources to the properties the caller reaches, so a collaborator «Solo alcuni» lists and opens the reviews of its
/// properties only, and a review of another org is a 404, never a 403. The words of a review are personal data: for the host only, never logged.
/// </summary>
public sealed partial class MessageHubService
{
    public const string ReviewNotFoundCode = "review_not_found";

    private const int OtaReviewSource = (int)MessageThreadSource.OtaReview;
    private const int ReviewSource = (int)MessageThreadSource.Review;

    private static NotFoundException ReviewNotFound() =>
        new("The review was not found or is not within reach") { Code = ReviewNotFoundCode, MessageKey = "OtaReviewNotFound" };

    private IQueryable<HubRow> ReviewRows(HostScope scope)
    {
        var orgId = scope.OrgId;

        var site = db.Reviews
            .AsNoTracking()
            .Where(r => r.OrgId == orgId && r.Status != ReviewStatus.Removed)
            .InScope(scope)
            .Select(r => new HubRow
            {
                Source = ReviewSource,
                KeyId = r.Id,
                Channel = (int)MessageHubChannel.Direct,
                Kind = (int)MessageHubThreadKind.Review,
                PropertyId = r.PropertyId,
                PropertyName = r.Property.Name,
                BookingId = r.BookingId,
                BookingCode = r.Booking.BookingCode,
                GuestName = r.Booking.Guest.FirstName + " " + r.Booking.Guest.LastName,
                CheckInDate = r.Booking.CheckInDate,
                CheckOutDate = r.Booking.CheckOutDate,
                BookingStatus = (int)r.Booking.Status,
                IsArchived = false,
                LastMessageAt = r.PublishedAt,
                UnreadCount = 0,
                NeedsReply = r.HostReply == null,
            });

        var channel = db.OtaReviews
            .AsNoTracking()
            .Where(r => r.OrgId == orgId && r.GuestReviewPublished)
            .InScope(scope)
            .Select(r => new HubRow
            {
                Source = OtaReviewSource,
                KeyId = r.Id,
                Channel = (int)r.Channel,
                Kind = (int)MessageHubThreadKind.Review,
                PropertyId = r.PropertyId,
                PropertyName = r.Property.Name,
                BookingId = r.BookingId,
                BookingCode = r.Booking.BookingCode,
                GuestName = r.Booking.Guest.FirstName + " " + r.Booking.Guest.LastName,
                CheckInDate = r.Booking.CheckInDate,
                CheckOutDate = r.Booking.CheckOutDate,
                BookingStatus = (int)r.Booking.Status,
                IsArchived = false,
                LastMessageAt = r.GuestReviewPublishedAt ?? r.CreatedAt,
                UnreadCount = 0,
                NeedsReply = r.HostReplyState == OtaReviewActionState.None || r.HostReplyState == OtaReviewActionState.Error,
            });

        return site.Concat(channel);
    }

    /// <summary>
    /// The first words of the reviews of the page (<see cref="ReviewRules.Preview"/>, for the host only) and their stars, read after the statement of the page: the text of a review of a channel
    /// is encrypted at rest and is decrypted by the entity, never searched, and the two sources keep their stars in columns of different kinds, which a union would not join. The author of a
    /// review is the guest.
    /// </summary>
    private async Task AddReviewPreviewsAsync(
        Guid orgId,
        IReadOnlyList<HubRow> pageRows,
        Dictionary<MessageThreadKey, LastMessage> previews,
        Dictionary<MessageThreadKey, decimal?> ratings,
        CancellationToken cancellationToken)
    {
        var siteIds = pageRows.Where(r => r.Source == ReviewSource).Select(r => r.KeyId).ToList();
        if (siteIds.Count > 0)
        {
            var texts = await db.Reviews
                .AsNoTracking()
                .Where(r => r.OrgId == orgId && siteIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Text, r.Rating })
                .ToListAsync(cancellationToken);
            foreach (var review in texts)
            {
                var key = MessageThreadKey.ForReview(review.Id);
                previews[key] = new LastMessage(MessageOrigin.Guest, ReviewRules.Preview(review.Text));
                ratings[key] = review.Rating;
            }
        }

        var channelIds = pageRows.Where(r => r.Source == OtaReviewSource).Select(r => r.KeyId).ToList();
        if (channelIds.Count > 0)
        {
            var texts = await db.OtaReviews
                .AsNoTracking()
                .Where(r => r.OrgId == orgId && channelIds.Contains(r.Id))
                .Select(r => new { r.Id, r.GuestReviewText, r.GuestRating })
                .ToListAsync(cancellationToken);
            foreach (var review in texts)
            {
                var key = MessageThreadKey.ForOtaReview(review.Id);
                previews[key] = new LastMessage(MessageOrigin.Guest, ReviewRules.Preview(review.GuestReviewText));
                ratings[key] = review.GuestRating;
            }
        }
    }

    public async Task<ReviewThreadView> GetReviewThreadAsync(
        HostScope scope,
        MessageThreadKey key,
        bool includeAmounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return key.Source switch
        {
            MessageThreadSource.OtaReview => await GetChannelReviewAsync(scope, key, includeAmounts, cancellationToken),
            MessageThreadSource.Review => await GetSiteReviewAsync(scope, key, includeAmounts, cancellationToken),
            _ => throw ReviewNotFound(),
        };
    }

    private async Task<ReviewThreadView> GetChannelReviewAsync(HostScope scope, MessageThreadKey key, bool includeAmounts, CancellationToken cancellationToken)
    {
        var service = otaReviews ?? throw new InvalidOperationException("The reviews of the channels are not registered (AddOtaReviews).");
        OtaReviewView view;
        try
        {
            view = await service.GetAsync(scope, key.Id, cancellationToken);
        }
        catch (NotFoundException)
        {
            throw ReviewNotFound();
        }

        if (!view.GuestReviewPublished)
            throw ReviewNotFound();

        var card = await BookingCardAsync(scope, view.BookingId, view.PropertyId, null, includeAmounts, cancellationToken);
        return new ReviewThreadView(
            key,
            MessageThreadSource.OtaReview,
            (MessageHubChannel)(int)view.Channel,
            view.PropertyId,
            view.PropertyName,
            view.BookingId,
            card?.GuestName,
            view.GuestReviewPublishedAt,
            view.GuestRating,
            view.GuestCategoryRatings,
            view.GuestText,
            view.Reply,
            view.HostReview,
            card?.Card,
            card?.GuestId,
            view.Capabilities,
            view.LastSyncedAt,
            view.LastSyncError);
    }

    private async Task<ReviewThreadView> GetSiteReviewAsync(HostScope scope, MessageThreadKey key, bool includeAmounts, CancellationToken cancellationToken)
    {
        var review = await db.Reviews
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId && r.Id == key.Id && r.Status != ReviewStatus.Removed)
            .InScope(scope)
            .Select(r => new
            {
                r.PropertyId,
                PropertyName = r.Property.Name,
                r.BookingId,
                r.Rating,
                r.Text,
                r.PublishedAt,
                r.HostReply,
                r.HostRepliedAt,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ReviewNotFound();

        var card = await BookingCardAsync(scope, review.BookingId, review.PropertyId, null, includeAmounts, cancellationToken);
        var answered = review.HostReply is not null;
        return new ReviewThreadView(
            key,
            MessageThreadSource.Review,
            MessageHubChannel.Direct,
            review.PropertyId,
            review.PropertyName,
            review.BookingId,
            card?.GuestName,
            review.PublishedAt,
            review.Rating,
            [],
            review.Text,
            new ReviewReplyView(answered ? OtaReviewActionState.Sent : OtaReviewActionState.None, review.HostReply, review.HostRepliedAt, null),
            null,
            card?.Card,
            card?.GuestId,
            OtaReviewService.DirectCapabilities(answered),
            null,
            null);
    }
}
