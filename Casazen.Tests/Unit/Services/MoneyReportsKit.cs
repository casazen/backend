using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The rows the tests of the money reports (NF-97, NF-98) are made of: stays and payments with the instants and the amounts written out, on the clock of the
/// host scope tests (2026-10-09, 10:00 UTC), so a figure of a test can be worked out by hand. Every instant is set: a default would be the real clock.
/// </summary>
internal static class MoneyReportsKit
{
    /// <summary>The instant the services run at: 2026-10-09 in Rome.</summary>
    public static readonly TimeProvider Clock = new FixedTimeProvider(HostScopeScenario.Now);

    /// <summary>A calendar date of 2026 as a stay date (midnight UTC).</summary>
    public static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>An instant of 2026 (UTC).</summary>
    public static DateTime At(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    public static Guest NewGuest(Guid orgId, string firstName = "Anna", string lastName = "Verdi") => new()
    {
        OrgId = orgId,
        FirstName = firstName,
        LastName = lastName,
        Email = $"{Guid.NewGuid():N}@example.com",
        PhoneNumber = "+39 333 1234567",
    };

    public static Booking Stay(
        Property property,
        Guest guest,
        BookingStatus status,
        BookingSource source,
        DateTime checkIn,
        DateTime checkOut,
        decimal basePrice,
        decimal? totalPrice = null)
    {
        return new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Status = status,
            Source = source,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            BasePrice = basePrice,
            TotalPrice = totalPrice ?? basePrice,
            CreatedAt = At(10, 1, 8),
            UpdatedAt = At(10, 1, 8),
        };
    }

    /// <summary>A payment of a booking; without <paramref name="processedAt"/> it came in at <paramref name="createdAt"/> (the instant of a payment recorded before it existed).</summary>
    public static Payment Pay(
        Booking booking,
        decimal amount,
        PaymentStatus status,
        PaymentMethod method,
        DateTime? processedAt,
        DateTime? createdAt = null,
        decimal refunded = 0m)
    {
        var created = createdAt ?? processedAt ?? At(10, 1, 8);
        return new Payment
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            Amount = amount,
            RefundedAmount = refunded,
            Status = status,
            Method = method,
            TransactionId = $"tx-{Guid.NewGuid():N}",
            ProcessedAt = processedAt,
            CreatedAt = created,
            UpdatedAt = created,
        };
    }

    /// <summary>A card payment the provider settled, with the figures its capture stores.</summary>
    public static Payment CardPay(
        Booking booking,
        decimal amount,
        DateTime processedAt,
        decimal? fee,
        decimal? net,
        DateTime? availableOn,
        string? currency = "EUR",
        PaymentStatus status = PaymentStatus.Completed)
    {
        var payment = Pay(booking, amount, status, PaymentMethod.CreditCard, processedAt);
        payment.StripePaymentIntentId = $"pi_{Guid.NewGuid():N}";
        payment.ProviderFeeAmount = fee;
        payment.ProviderNetAmount = net;
        payment.ProviderAvailableOn = availableOn;
        payment.ProviderBalanceCurrency = fee is null && net is null ? null : currency;
        payment.ProviderFeeFetchedAt = fee is null && net is null ? null : processedAt.AddHours(1);
        return payment;
    }
}
