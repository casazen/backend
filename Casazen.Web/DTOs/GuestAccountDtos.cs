using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

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

public sealed record GuestAccountBookingPageResponse(IReadOnlyList<GuestAccountBookingResponse> Items, int Total, int Page, int PageSize)
{
    public static GuestAccountBookingPageResponse From(GuestAccountBookingPage page) =>
        new(page.Items.Select(GuestAccountBookingResponse.From).ToList(), page.Total, page.Page, page.PageSize);
}

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

public sealed record GuestAccountSessionResponse(string AccessToken, DateTime ExpiresAt, string BookingCode, string OrgSlug)
{
    public static GuestAccountSessionResponse From(GuestAccountSessionOpened opened) =>
        new(opened.AccessToken, opened.ExpiresAt, opened.BookingCode, opened.OrgSlug);
}
