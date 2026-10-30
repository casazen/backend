using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.DTOs;

namespace Casazen.Web.Infrastructure;

public static class BookingMapper
{
    public static BookingResponseDto ToResponse(Booking booking) => ToResponse(booking, DateTime.UtcNow);

    /// <param name="booking">The booking, with its property and guest when loaded.</param>
    /// <param name="nowUtc">Instant that decides whether a "pay at the property" request is still open.</param>
    public static BookingResponseDto ToResponse(Booking booking, DateTime nowUtc) => Fill(new BookingResponseDto(), booking, nowUtc);

    /// <summary>
    /// A row of the booking list (SR-03b): the booking and what the list says besides it. Without <paramref name="details"/> (a row
    /// the service did not describe) nothing has come in.
    /// </summary>
    public static BookingListItemDto ToListItem(Booking booking, BookingRowDetails? details, DateTime nowUtc)
    {
        var item = Fill(new BookingListItemDto(), booking, nowUtc);
        item.Channel = BookingListRules.ChannelOf(booking.Source).ToString();
        item.PaymentState = (details?.PaymentState ?? BookingPaymentState.None).ToString();
        item.PaidAmount = details?.PaidAmount;
        item.AlloggiatiStatus = details?.AlloggiatiStatus?.ToString();
        // The answer is the host's only once the guest confirmed the email; a request that is not the host's yet has no term to show.
        item.RespondBy = OnSiteRequests.StateOf(booking, nowUtc) == OnSiteRequestState.AwaitingHostApproval
            ? booking.RequestExpiresAt
            : null;
        return item;
    }

    /// <summary>
    /// Fills <paramref name="response"/> (a <see cref="BookingResponseDto"/> or a response that adds fields to it, such
    /// as <see cref="ArrivalRegisteredResponse"/>) with the booking.
    /// </summary>
    public static T Fill<T>(T response, Booking booking, DateTime nowUtc) where T : BookingResponseDto
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
        response.PriceOverridden = booking.PriceOverridden;
        response.PriceOverrideNote = booking.PriceOverrideNote;
        response.Status = booking.Status.ToString();
        response.CancellationNote = booking.CancellationNote;
        response.Source = booking.Source.ToString();
        response.SpecialRequests = booking.SpecialRequests;
        response.ExpectedArrivalTime = booking.ExpectedArrivalTime;
        response.PaymentOption = booking.PaymentOption.ToString();
        response.OnSiteRequestState = OnSiteRequests.StateOf(booking, nowUtc)?.ToString();
        response.RequestExpiresAt = OnSiteRequests.StateOf(booking, nowUtc) is null ? null : booking.RequestExpiresAt;
        response.IcalFeedId = booking.ICalFeedId;
        response.ChannelLabel = booking.ChannelLabel;
        // A cancelled stay has nothing left to check (CO-21).
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
        // The rule of PUT /api/bookings/{id}: the booking sheet and the calendar do not guess it (SR-05a).
        response.CanEdit = BookingEditabilityDto.From(BookingEditRules.Of(booking));
        return response;
    }
}
