using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public record GuestBookingLookupResponse(
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

public sealed record GuestBookingHostContactResponse(string? Name, string? Email);

public sealed record GuestCheckInAccessResponse(GuestCheckInAccessStatus Status, DateOnly? OpensOn, DateTime? LinkSentAt);
