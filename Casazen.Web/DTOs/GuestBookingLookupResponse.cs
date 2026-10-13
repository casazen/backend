using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// One booking as its guest sees it in "Le mie prenotazioni" (BK-11): see <see cref="GuestBookingView"/>. No personal
/// data of the guest; the host's contact is the one of the booking site.
/// </summary>
public sealed record GuestBookingLookupResponse(
    string BookingCode,
    GuestBookingStatus Status,
    PaymentOption PaymentOption,
    Guid PropertyId,
    string? PropertySlug,
    string PropertyName,
    string? PropertyCity,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    int NumberOfAdults,
    int NumberOfChildren,
    decimal Lodging,
    decimal CleaningFee,
    decimal TouristTax,
    decimal TotalPrice,
    decimal PaidAmount,
    decimal RefundedAmount,
    string Currency,
    DateTime? ExpiresAt,
    DateOnly? DeferredChargeDate,
    GuestBookingHostContactResponse Host,
    GuestCheckInAccessResponse CheckIn)
{
    public static GuestBookingLookupResponse From(GuestBookingView view) => new(
        view.BookingCode,
        view.Status,
        view.PaymentOption,
        view.PropertyId,
        view.PropertySlug,
        view.PropertyName,
        view.PropertyCity,
        view.CheckInDate,
        view.CheckOutDate,
        view.NumberOfAdults,
        view.NumberOfChildren,
        view.Lodging,
        view.CleaningFee,
        view.TouristTax,
        view.TotalPrice,
        view.PaidAmount,
        view.RefundedAmount,
        view.Currency,
        view.ExpiresAt,
        view.DeferredChargeDate,
        new GuestBookingHostContactResponse(view.Host.Name, view.Host.Email),
        new GuestCheckInAccessResponse(view.CheckIn.Status, view.CheckIn.OpensOn, view.CheckIn.LinkSentAt));
}

/// <summary>The host's contact shown on the booking site; either may be null.</summary>
public sealed record GuestBookingHostContactResponse(string? Name, string? Email);

/// <summary>The online check-in of the booking (see <see cref="GuestCheckInAccess"/>).</summary>
public sealed record GuestCheckInAccessResponse(GuestCheckInAccessStatus Status, DateOnly? OpensOn, DateTime? LinkSentAt);
