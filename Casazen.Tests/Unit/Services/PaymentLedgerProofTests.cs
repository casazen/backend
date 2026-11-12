using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F04/US01/T03 (NF-85): the ledger shows the fee, the net and the gross of a card payment exactly as stored, never a guess for what is not stored, never
/// the lines of a property a partial scope does not reach, and degrades to the stored OTA amounts when Octorate does not answer.
/// </summary>
public class PaymentLedgerProofTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private const string Anna = "auth0|anna-collaborator";

    [Fact]
    public async Task PaymentLedger_StripeFeeAndNet_MatchStoredPayment_Test()
    {
        await using var db = NewDb();
        var (booking, _) = await SeedBookingAsync(db, "CHF");
        var card = Payment(booking, 450.00m, PaymentMethod.CreditCard, "pi_TEST_card");
        card.ProviderFeeAmount = 6.55m;
        card.ProviderNetAmount = 443.45m;
        card.ProviderBalanceCurrency = "CHF";
        card.ProviderAvailableOn = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
        var uncaptured = Payment(booking, 50.00m, PaymentMethod.CreditCard, "pi_TEST_pending_fee");
        var cash = Payment(booking, 120.00m, PaymentMethod.CashOnArrival, null);
        db.Payments.AddRange(card, uncaptured, cash);
        await db.SaveChangesAsync();

        var result = await Service(db).GetForBookingAsync(new HostScope(OrgId), booking);

        var cardLine = Assert.Single(result!.Lines, l => l.PaymentId == card.Id);
        Assert.Equal(PaymentLedgerKind.Card, cardLine.Kind);
        Assert.Equal(450.00m, cardLine.Gross);
        Assert.Equal(6.55m, cardLine.ProviderFee);
        Assert.Equal(443.45m, cardLine.Net);
        Assert.Equal(new DateOnly(2026, 11, 19), cardLine.CreditDate);
        Assert.Equal("CHF", cardLine.Currency);
        Assert.Equal(PaymentLedgerState.Paid, cardLine.State);
        var pending = Assert.Single(result.Lines, l => l.PaymentId == uncaptured.Id);
        Assert.Null(pending.ProviderFee);
        Assert.Null(pending.Net);
        Assert.Null(pending.CreditDate);
        Assert.Equal(PaymentLedgerKind.Cash, Assert.Single(result.Lines, l => l.PaymentId == cash.Id).Kind);
        Assert.DoesNotContain(result.Lines, l => l.Kind == PaymentLedgerKind.Ota);
    }

    [Fact]
    public async Task PaymentLedger_HostScope_OtherPropertiesNeverReturned_Test()
    {
        await using var db = NewDb();
        var (granted, grantedProperty) = await SeedBookingAsync(db, "EUR");
        var (hidden, _) = await SeedBookingAsync(db, "EUR", otaPaid: 380m);
        db.Payments.AddRange(Payment(granted, 100m, PaymentMethod.CreditCard, "pi_TEST_granted"), Payment(hidden, 200m, PaymentMethod.CreditCard, "pi_TEST_hidden"));
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = OrgId, UserId = Anna, PropertyId = grantedProperty });
        await db.SaveChangesAsync();
        var collaborator = new HostScope(OrgId, GrantedToUserId: Anna);
        var service = Service(db);

        var page = await service.ListAsync(collaborator, new PaymentListCriteria(), 1, 100);

        Assert.All(page.Items, l => Assert.Equal(granted, l.BookingId));
        Assert.Single(page.Items);
        Assert.Null(await service.GetForBookingAsync(collaborator, hidden));
        var whole = await service.ListAsync(new HostScope(OrgId), new PaymentListCriteria(), 1, 100);
        Assert.Equal(3, whole.TotalCount);
        Assert.Contains(whole.Items, l => l.BookingId == hidden && l.Kind == PaymentLedgerKind.Ota && l.Gross == 380m && l.ProviderFee is null);
    }

    [Fact]
    public async Task PaymentLedger_OctorateFailure_DegradesToTheStoredAmounts_AndAMissingAmountIsNoLine()
    {
        await using var db = NewDb();
        var (ota, property) = await SeedBookingAsync(db, "EUR", otaPaid: 380m, reservation: "R-9");
        var (empty, _) = await SeedBookingAsync(db, "EUR", reservation: "R-10");
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = OrgId,
            ConnectionId = Guid.NewGuid(),
            OctorateAccommodationId = "ACC-1",
            OctorateProductId = "P-1",
            PropertyId = property,
            Status = OctorateMappingStatus.Confirmed,
        });
        await db.SaveChangesAsync();
        var client = new Mock<IOctorateApiClient>();
        client.Setup(c => c.GetReservationPaymentsAsync("ACC-1", "R-9", It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateServerException(500));

        var degraded = await Service(db, client.Object).GetForBookingAsync(new HostScope(OrgId), ota);

        Assert.True(degraded!.Degraded);
        var line = Assert.Single(degraded.Lines);
        Assert.Equal(380m, line.Gross);
        Assert.Equal(PaymentLedgerCollector.Channel, line.CollectedBy);
        Assert.Null(line.ProviderFee);

        var none = await Service(db, client.Object).GetForBookingAsync(new HostScope(OrgId), empty);
        Assert.Empty(none!.Lines);
        Assert.False(none.Degraded);
    }

    /// <summary>
    /// A payment CasaZen recorded on the Octorate reservation comes back in the reservation's payments with its transaction: it is the line of the payment, not a
    /// second one (the amount on Octorate is net of the tourist tax, so it is matched by transaction, never by amount). Only what the channel collected is added.
    /// </summary>
    [Fact]
    public async Task PaymentLedger_PaymentMirroredOnOctorate_IsNotCountedTwice_Test()
    {
        await using var db = NewDb();
        var (booking, property) = await SeedBookingAsync(db, "EUR", reservation: "R-11");
        var card = Payment(booking, 450m, PaymentMethod.CreditCard, "pi_TEST_mirrored");
        card.OctorateTransactionId = "pi_TEST_mirrored";
        var cash = Payment(booking, 100m, PaymentMethod.CashOnArrival, null);
        cash.OctorateTransactionId = "casazen-" + cash.Id.ToString("N");
        db.Payments.AddRange(card, cash);
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = OrgId,
            ConnectionId = Guid.NewGuid(),
            OctorateAccommodationId = "ACC-1",
            OctorateProductId = "P-1",
            PropertyId = property,
            Status = OctorateMappingStatus.Confirmed,
        });
        await db.SaveChangesAsync();
        var day = new DateOnly(2026, 11, 3);
        var client = new Mock<IOctorateApiClient>();
        client.Setup(c => c.GetReservationPaymentsAsync("ACC-1", "R-11", It.IsAny<CancellationToken>())).ReturnsAsync(new List<OctoratePayment>
        {
            new(420m, day, "CARD", "HOTEL", "pi_TEST_mirrored"),
            new(100m, day, "CASH", "HOTEL", cash.OctorateTransactionId),
            new(60m, day, "BANK", "COMPANY", "ota-9"),
        });

        var result = await Service(db, client.Object).GetForBookingAsync(new HostScope(OrgId), booking);

        Assert.Equal(3, result!.Lines.Count);
        Assert.Equal(610m, result.Lines.Sum(l => l.Gross));
        var channel = Assert.Single(result.Lines, l => l.Kind == PaymentLedgerKind.Ota);
        Assert.Equal(60m, channel.Gross);
        Assert.Equal(PaymentLedgerKind.Card, Assert.Single(result.Lines, l => l.PaymentId == card.Id).Kind);
    }

    private static BookingPaymentLedgerService Service(AppDbContext db, IOctorateApiClient? client = null)
    {
        var scope = new Mock<IOctorateOrgScope>();
        scope.Setup(s => s.Use(It.IsAny<Guid>())).Returns(Mock.Of<IDisposable>());
        return new BookingPaymentLedgerService(
            db, client ?? Mock.Of<IOctorateApiClient>(), scope.Object, new TestFeatureFlags(FeatureFlags.OctorateConnector), NullLogger<BookingPaymentLedgerService>.Instance);
    }

    private static Payment Payment(Guid booking, decimal amount, PaymentMethod method, string? intent) => new()
    {
        OrgId = OrgId,
        BookingId = booking,
        Amount = amount,
        Status = PaymentStatus.Completed,
        Method = method,
        StripePaymentIntentId = intent,
        ProcessedAt = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc),
    };

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"PaymentLedger_{Guid.NewGuid()}").Options, new LedgerTenant(), new EphemeralDataProtectionProvider());

    private static async Task<(Guid Booking, Guid Property)> SeedBookingAsync(
        AppDbContext db,
        string currency,
        decimal? otaPaid = null,
        string? reservation = null)
    {
        var property = new Property { OrgId = OrgId, OwnerId = "auth0|owner", Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = currency };
        var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 2),
            CheckOutDate = new DateTime(2026, 11, 5),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = otaPaid is null && reservation is null ? BookingSource.Direct : BookingSource.BookingCom,
            OtaPaidAmount = otaPaid,
            OctorateReservationId = reservation,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return (booking.Id, property.Id);
    }
}

file sealed class LedgerTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
