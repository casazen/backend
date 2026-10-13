using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// Booking rows for migration tests that seed an older schema: the columns every <c>Bookings</c> row needs since
/// <c>AddBookingCode</c> (the readable booking code, unique per org, comes from the entity: <c>BookingCodes.New()</c>);
/// every other column is nullable or has a database default (cleaning fee 0, deferred charge attempts 0). Same reasoning as
/// <see cref="LegacyPropertyRows"/>: a column that a later migration adds to <see cref="Booking"/> (CO-21 channel and review
/// columns, cancellation reason, Stripe ids…) cannot break the test; set those with an <c>UPDATE</c> after the migration that
/// adds them. The property and the guest must exist.
/// </summary>
internal static class LegacyBookingRows
{
    public static Task InsertAsync(AppDbContext db, Booking booking) =>
        db.Database.ExecuteSqlInterpolatedAsync(Statement(booking));

    internal static FormattableString Statement(Booking booking) => $"""
        INSERT INTO "Bookings" (
            "Id", "PropertyId", "OrgId", "GuestId", "CheckInDate", "CheckOutDate", "NumberOfGuests", "NumberOfAdults",
            "NumberOfChildren", "Status", "Source", "ExternalId", "BasePrice", "TouristTax", "TotalPrice", "TouristTaxAmount",
            "SpecialRequests", "PaymentOption", "FreeRefundDeadline", "BookingCode", "CreatedAt", "UpdatedAt")
        VALUES ({booking.Id}, {booking.PropertyId}, {booking.OrgId}, {booking.GuestId}, {booking.CheckInDate},
            {booking.CheckOutDate}, {booking.NumberOfGuests}, {booking.NumberOfAdults}, {booking.NumberOfChildren},
            {(int)booking.Status}, {(int)booking.Source}, {booking.ExternalId}, {booking.BasePrice}, {booking.TouristTax},
            {booking.TotalPrice}, {booking.TouristTaxAmount}, {booking.SpecialRequests}, {(int)booking.PaymentOption},
            {booking.FreeRefundDeadline}, {booking.BookingCode}, {booking.CreatedAt}, {booking.UpdatedAt});
        """;
}
