using Casazen.Core.Entities;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;
using Xunit;

namespace Casazen.Tests.Unit.Validation;

public class BookingValidatorTests
{
    // 00:30 on 01/10/2026 in Rome (CEST), still 30/09 in UTC.
    private static readonly FixedTimeProvider AfterMidnightInRome =
        new(new DateTimeOffset(2026, 9, 30, 22, 30, 0, TimeSpan.Zero));

    private static Booking MakeBooking(DateTime checkIn) => new()
    {
        PropertyId = Guid.NewGuid(),
        GuestId = Guid.NewGuid(),
        CheckInDate = checkIn,
        CheckOutDate = checkIn.AddDays(2),
        NumberOfGuests = 2,
        TotalPrice = 100m,
    };

    [Fact]
    public void ValidateBooking_CheckInYesterdayInRome_ReturnsPastCheckInError()
    {
        var booking = MakeBooking(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc));

        var result = BookingValidator.ValidateBooking(booking, today: AfterMidnightInRome.TodayInRome());

        Assert.False(result.IsValid);
        Assert.Contains("Check-in date cannot be in the past", result.Errors);
    }

    [Fact]
    public void ValidateBooking_CheckInTodayInRome_IsValid()
    {
        var booking = MakeBooking(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = BookingValidator.ValidateBooking(booking, today: AfterMidnightInRome.TodayInRome());

        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void ValidateBooking_PastCheckInAllowed_IsValid()
    {
        var booking = MakeBooking(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc));

        var result = BookingValidator.ValidateBooking(
            booking, allowPastCheckIn: true, today: AfterMidnightInRome.TodayInRome());

        Assert.True(result.IsValid, result.ErrorMessage);
    }
}
