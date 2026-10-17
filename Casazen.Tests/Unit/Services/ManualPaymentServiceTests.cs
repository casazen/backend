using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Money recorded by the host (SR-06b): the receipt is born Completed, never goes past what the booking still has to collect,
/// and the same key sent twice writes one payment.
/// </summary>
public class ManualPaymentServiceTests
{
    [Fact]
    public async Task RecordAsync_WritesACompletedReceiptOnce_AndNeverPastTheBalance()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options, NullTenantContext.Instance);
        var booking = new Booking
        {
            OrgId = Guid.NewGuid(),
            PropertyId = Guid.NewGuid(),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Manual,
            TotalPrice = 100m,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var sut = new ManualPaymentService(db, Mock.Of<IFiscalRegimeService>(), NullLogger<ManualPaymentService>.Instance);

        var first = await sut.RecordAsync(Command(booking.Id, 60m, "key-a"));
        var again = await sut.RecordAsync(Command(booking.Id, 60m, "key-a"));

        Assert.True(first.Created);
        Assert.Equal(PaymentStatus.Completed, first.Payment.Status);
        Assert.NotNull(first.Payment.ProcessedAt);
        Assert.False(again.Created);
        Assert.Equal(first.Payment.Id, again.Payment.Id);
        Assert.Equal(1, await db.Payments.CountAsync());

        var tooMuch = await Assert.ThrowsAsync<DomainRuleException>(() => sut.RecordAsync(Command(booking.Id, 50m, "key-b")));
        Assert.Equal("payment_exceeds_balance", tooMuch.Code);

        var rest = await sut.RecordAsync(Command(booking.Id, 40m, "key-c"));
        Assert.True(rest.Created);
        Assert.Equal(2, await db.Payments.CountAsync());
        var exhausted = await Assert.ThrowsAsync<DomainRuleException>(() => sut.RecordAsync(Command(booking.Id, 0.01m, "key-d")));
        Assert.Equal("payment_exceeds_balance", exhausted.Code);
    }

    private static ManualPaymentCommand Command(Guid bookingId, decimal amount, string key) =>
        new(bookingId, amount, PaymentMethod.CashOnArrival, null, null, key);
}
