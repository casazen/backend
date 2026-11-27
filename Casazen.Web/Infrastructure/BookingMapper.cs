using Casazen.Core.Bookings;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.DTOs;

namespace Casazen.Web.Infrastructure;

public static class BookingMapper
{
    public static BookingResponseDto ToResponse(Booking booking, OtaChannelActionPolicy? channelPolicy = null) =>
        ToResponse(booking, DateTime.UtcNow, channelPolicy);

    public static BookingResponseDto ToResponse(Booking booking, DateTime nowUtc, OtaChannelActionPolicy? channelPolicy = null) =>
        Fill(new BookingResponseDto(), booking, nowUtc, channelPolicy);

    public static BookingListItemDto ToListItem(
        Booking booking, BookingRowDetails? details, DateTime nowUtc, OtaChannelActionPolicy? channelPolicy = null)
    {
        var item = Fill(new BookingListItemDto(), booking, nowUtc, channelPolicy);
        item.Channel = BookingListRules.ChannelOf(booking.Source).ToString();
        item.PaymentState = (details?.PaymentState ?? BookingPaymentState.None).ToString();
        item.PaidAmount = details?.PaidAmount;
        item.AlloggiatiStatus = details?.AlloggiatiStatus?.ToString();
        item.RespondBy = OnSiteRequests.StateOf(booking, nowUtc) == OnSiteRequestState.AwaitingHostApproval
            ? booking.RequestExpiresAt
            : null;
        return item;
    }

    /// <summary>
    /// Fills <paramref name="response"/> (a <see cref="BookingResponseDto"/> or a response that adds fields to it, such
    /// as <see cref="ArrivalRegisteredResponse"/>) with the booking.
    /// </summary>
    /// <param name="response">The answer to fill.</param>
    /// <param name="booking">The booking, with its property and guest when loaded.</param>
    /// <param name="nowUtc">Instant that decides whether a "pay at the property" request is still open.</param>
    /// <param name="channelPolicy">
    /// Which channels let the host change or cancel a booking that came from them (D8): it decides the <c>changeOptions</c> of a booking of a channel
    /// and nothing else. Without one every channel is closed, which is the answer of every booking that is not one of a channel, so only an answer that
    /// can carry a booking of a channel needs to pass it.
    /// </param>
    public static T Fill<T>(T response, Booking booking, DateTime nowUtc, OtaChannelActionPolicy? channelPolicy = null) where T : BookingResponseDto
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(booking);

        response.Id = booking.Id;
        response.BookingCode = BookingCodes.Format(booking.BookingCode);
        response.PropertyId = booking.PropertyId;
        response.PropertyName = booking.Property?.Name;
        response.CheckInDate = booking.CheckInDate;
        response.CheckOutDate = booking.CheckOutDate;
        response.NumberOfGuests = booking.NumberOfGuests;
        response.NumberOfAdults = BookingGuestCounts.Of(booking).Adults;
        response.NumberOfChildren = BookingGuestCounts.Of(booking).Children;
        response.TotalPrice = booking.TotalPrice;
        response.BasePrice = booking.BasePrice;
        response.CleaningFee = booking.CleaningFee;
        response.TouristTax = booking.TouristTax;
        response.Currency = StayCurrency.ForProperty(booking.Property);
        response.PriceOverridden = booking.PriceOverridden;
        response.PriceOverrideNote = booking.PriceOverrideNote;
        response.Status = booking.Status.ToString();
        response.CancellationNote = booking.CancellationNote;
        response.Source = booking.Source.ToString();
        response.OtaSource = OtaChannelActionPolicy.IsChannelBooking(booking) ? booking.Source.ToString() : null;
        response.Origin = BookingOrigins.Of(booking).ToString();
        response.OriginChannel = BookingOrigins.PortalOf(booking);
        response.SpecialRequests = booking.SpecialRequests;
        response.ExpectedArrivalTime = booking.ExpectedArrivalTime;
        response.PaymentOption = booking.PaymentOption.ToString();
        response.OnSiteRequestState = OnSiteRequests.StateOf(booking, nowUtc)?.ToString();
        response.RequestExpiresAt = OnSiteRequests.StateOf(booking, nowUtc) is null ? null : booking.RequestExpiresAt;
        response.IcalFeedId = booking.ICalFeedId;
        response.ChannelLabel = booking.ChannelLabel;
        var review = booking.Status == BookingStatus.Cancelled ? null : booking.OtaReviewReason;
        response.OtaReviewReason = review?.ToString();
        response.OtaReviewRaisedAt = review is null ? null : booking.OtaReviewRaisedAt;
        response.Guest = booking.Guest is null
            ? new BookingGuestDto()
            : new BookingGuestDto
            {
                FirstName = booking.Guest.FirstName,
                LastName = booking.Guest.LastName,
                Email = booking.Guest.Email,
                Phone = booking.Guest.PhoneNumber,
                Country = booking.Guest.Country,
            };
        response.CreatedAt = booking.CreatedAt;
        response.UpdatedAt = booking.UpdatedAt;
        response.ConfirmedAt = booking.ConfirmedAt;
        response.CancelledAt = booking.CancelledAt;
        response.ArrivedAt = booking.ArrivedAt;
        response.CheckedOutAt = booking.CheckedOutAt;
        response.CanEdit = BookingEditabilityDto.From(BookingEditRules.Of(booking));
        response.ChangeOptions = BookingChangeOptionsDto.From(BookingEditRules.ChangeOptionsOf(booking, channelPolicy));
        return response;
    }
}
