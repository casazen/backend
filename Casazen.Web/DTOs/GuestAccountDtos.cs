using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// One booking in the list of the signed-in guest of the mobile app (<c>GET api/guest/bookings</c>): see
/// <see cref="GuestAccountBookingSummary"/>. Dates are <c>yyyy-MM-dd</c> Europe/Rome days; the code is <c>XXXXX-XXXXX</c>. No personal
/// data of anyone and nothing the host wrote for itself.
/// </summary>
public sealed record GuestAccountBookingResponse(
    Guid Id,
    string OrgSlug,
    string BookingCode,
    GuestBookingStatus Status,
    Guid PropertyId,
    string? PropertySlug,
    string PropertyName,
    string City,
    string? PhotoUrl,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    int Nights,
    int Adults,
    int Children,
    string Currency,
    decimal TotalPrice,
    decimal PaidAmount,
    decimal RefundedAmount,
    PaymentOption PaymentOption,
    string? HostName)
{
    public static GuestAccountBookingResponse From(GuestAccountBookingSummary item) => new(
        item.Id,
        item.OrgSlug,
        item.BookingCode,
        item.Status,
        item.PropertyId,
        item.PropertySlug,
        item.PropertyName,
        item.City,
        item.PhotoUrl,
        item.CheckInDate,
        item.CheckOutDate,
        item.Nights,
        item.Adults,
        item.Children,
        item.Currency,
        item.TotalPrice,
        item.PaidAmount,
        item.RefundedAmount,
        item.PaymentOption,
        item.HostName);
}

/// <summary>A page of the bookings of the account; <paramref name="Total"/> counts the whole scope.</summary>
public sealed record GuestAccountBookingPageResponse(IReadOnlyList<GuestAccountBookingResponse> Items, int Total, int Page, int PageSize)
{
    public static GuestAccountBookingPageResponse From(GuestAccountBookingPage page) =>
        new(page.Items.Select(GuestAccountBookingResponse.From).ToList(), page.Total, page.Page, page.PageSize);
}

/// <summary>
/// One booking of the account (<c>GET api/guest/bookings/{id}</c>): the body of the anonymous "Le mie prenotazioni"
/// (<see cref="GuestBookingLookupResponse"/>, which already carries <c>propertyId</c> and <c>propertySlug</c>) plus the booking's <c>id</c>
/// and the <c>orgSlug</c> of its site.
/// </summary>
public sealed record GuestAccountBookingDetailResponse : GuestBookingLookupResponse
{
    public GuestAccountBookingDetailResponse(Guid id, string orgSlug, GuestBookingLookupResponse booking)
        : base(booking)
    {
        Id = id;
        OrgSlug = orgSlug;
    }

    public Guid Id { get; }

    public string OrgSlug { get; }

    public static GuestAccountBookingDetailResponse From(GuestAccountBookingDetail detail) =>
        new(detail.Id, detail.OrgSlug, GuestBookingLookupResponse.From(detail.View));
}

/// <summary>
/// The guest-area session opened for a booking of the account (<c>POST api/guest/bookings/{id}/session</c>): send <c>accessToken</c> as
/// <c>Authorization: Bearer</c> to <c>api/public/guest/*</c> and use <c>bookingCode</c> in their path.
/// </summary>
public sealed record GuestAccountSessionResponse(string AccessToken, DateTime ExpiresAt, string BookingCode, string OrgSlug)
{
    public static GuestAccountSessionResponse From(GuestAccountSessionOpened opened) =>
        new(opened.AccessToken, opened.ExpiresAt, opened.BookingCode, opened.OrgSlug);
}
