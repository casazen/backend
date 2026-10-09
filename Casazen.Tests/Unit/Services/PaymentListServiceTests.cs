using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-03: the payments of the host as list rows, with the guest and the property of their booking, of one booking, of one property
/// and/or of a period (Europe/Rome days, the day of settlement), and always inside the caller's scope: a collaborator limited to
/// some properties never gets the payments of the others, whatever the criteria say.
/// </summary>
public class PaymentListServiceTests
{
    private const string Collaborator = "auth0|collaboratore-sr03";

    private static DateTime At(int month, int day, int hour = 10, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        AppDbContext Db,
        Guid OrgId,
        Property Trullo,
        Property CasaBianca,
        Booking OnTrullo,
        Booking OnCasaBianca,
        Payment P1,
        Payment P2,
        Payment P3,
        Payment P4,
        Payment OfDeleted,
        HostScope OrgWide,
        HostScope Restricted);

    /// <summary>
    ///   p1  Trullo       Completed          300  settled 12 Oct 10:00Z
    ///   p2  Casa Bianca  PartiallyRefunded  200  settled 20 Oct, 50 refunded
    ///   p3  Trullo       Pending            100  not settled, created 14 Oct
    ///   p4  Casa Bianca  Completed           10  settled 8 Oct 22:30Z (00:30 of the 9th in Rome), created 1 Oct
    ///   and the payment of a property deleted since, and one of another org.
    /// </summary>
    private static async Task<Fixture> SeedAsync()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var trullo = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Trullo");
        var casaBianca = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Casa Bianca");
        var archived = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Casa Archiviata");
        archived.IsDeleted = true;
        var foreign = HostScopeScenario.NewProperty(otherOrgId, "auth0|altro-sr03", "Villa Altrove");
        db.Properties.AddRange(trullo, casaBianca, archived, foreign);
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = orgId, UserId = Collaborator, PropertyId = trullo.Id });

        var anna = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        var mario = new Guest { OrgId = orgId, FirstName = "Mario", LastName = "Rossi", Email = $"{Guid.NewGuid():N}@example.com" };
        var luca = new Guest { OrgId = otherOrgId, FirstName = "Luca", LastName = "Neri", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Guests.AddRange(anna, mario, luca);

        var onTrullo = Stay(trullo, anna, "ABCDEFGHJK");
        var onCasaBianca = Stay(casaBianca, mario, "MNPQRSTVWX");
        var onArchived = Stay(archived, mario, "0123456789");
        var elsewhere = Stay(foreign, luca, "ZZZZZ00000");
        db.Bookings.AddRange(onTrullo, onCasaBianca, onArchived, elsewhere);

        var p1 = Pay(onTrullo, PaymentStatus.Completed, 300m, processed: At(10, 12), created: At(10, 11));
        var p2 = Pay(onCasaBianca, PaymentStatus.PartiallyRefunded, 200m, refunded: 50m, processed: At(10, 20), created: At(10, 19));
        var p3 = Pay(onTrullo, PaymentStatus.Pending, 100m, processed: null, created: At(10, 14));
        var p4 = Pay(onCasaBianca, PaymentStatus.Completed, 10m, processed: At(10, 8, 22, 30), created: At(10, 1));
        var ofDeleted = Pay(onArchived, PaymentStatus.Completed, 77m, processed: At(10, 13), created: At(10, 13));
        db.Payments.AddRange(p1, p2, p3, p4, ofDeleted, Pay(elsewhere, PaymentStatus.Completed, 999m, processed: At(10, 12), created: At(10, 12)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new Fixture(
            db, orgId, trullo, casaBianca, onTrullo, onCasaBianca, p1, p2, p3, p4, ofDeleted, new HostScope(orgId),
            new HostScope(orgId, GrantedToUserId: Collaborator));
    }

    private static Booking Stay(Property property, Guest guest, string code)
    {
        return new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            CheckInDate = At(10, 10, 0),
            CheckOutDate = At(10, 13, 0),
            NumberOfGuests = 2,
            BookingCode = code,
        };
    }

    private static Payment Pay(Booking booking, PaymentStatus status, decimal amount, DateTime? processed, DateTime created, decimal refunded = 0m)
    {
        return new Payment
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            Status = status,
            Method = PaymentMethod.CreditCard,
            Amount = amount,
            RefundedAmount = refunded,
            ProcessedAt = processed,
            CreatedAt = created,
            UpdatedAt = created,
            TransactionId = $"tx-{Guid.NewGuid():N}",
            Description = "Soggiorno",
            StripePaymentIntentId = "pi_test",
        };
    }

    private static async Task<List<Guid>> IdsAsync(Fixture world, PaymentListCriteria criteria, HostScope? scope = null) =>
        (await new PaymentListService(world.Db).ListAsync(scope ?? world.OrgWide, criteria)).Select(p => p.Id).ToList();

    [Fact]
    public async Task NoCriteria_EveryPaymentOfTheScope_NewestFirst_WithTheGuestAndThePropertyNamed()
    {
        var world = await SeedAsync();

        var rows = await new PaymentListService(world.Db).ListAsync(world.OrgWide, new PaymentListCriteria());

        // By creation, newest first: the 20th, the 19th... the payment of the archived property too, never another org's.
        Assert.Equal([world.P2.Id, world.P3.Id, world.OfDeleted.Id, world.P1.Id, world.P4.Id], rows.Select(r => r.Id));
        var first = rows[0];
        Assert.Equal("Casa Bianca", first.PropertyName);
        Assert.Equal(world.CasaBianca.Id, first.PropertyId);
        Assert.Equal("Mario Rossi", first.GuestName);
        Assert.Equal("MNPQRSTVWX", first.BookingCode);
        Assert.Equal(world.OnCasaBianca.Id, first.BookingId);
        Assert.Equal((200m, 50m), (first.Amount, first.RefundedAmount));
        Assert.Equal(PaymentStatus.PartiallyRefunded, first.Status);
        Assert.Equal(PaymentMethod.CreditCard, first.Method);
        Assert.Equal(At(10, 20), first.ProcessedAt);
        Assert.Null(rows.Single(r => r.Id == world.P3.Id).ProcessedAt);
    }

    [Fact]
    public async Task Booking_OnlyThePaymentsOfThatBooking()
    {
        var world = await SeedAsync();

        Assert.Equal(
            new[] { world.P3.Id, world.P1.Id },
            await IdsAsync(world, new PaymentListCriteria(BookingId: world.OnTrullo.Id)));
    }

    [Fact]
    public async Task Property_OnlyThePaymentsOfTheStaysOfThatProperty()
    {
        var world = await SeedAsync();

        Assert.Equal(
            new[] { world.P2.Id, world.P4.Id },
            await IdsAsync(world, new PaymentListCriteria(PropertyId: world.CasaBianca.Id)));
    }

    [Theory]
    // (from, to) as (month, day); 0 for none. The payment date is the day of settlement in Rome, else the day of creation.
    [InlineData(10, 12, 10, 12, "p1")]
    [InlineData(10, 13, 10, 14, "p3,ofDeleted")]
    [InlineData(10, 9, 10, 9, "p4")]
    [InlineData(10, 8, 10, 8, "")]
    [InlineData(10, 15, 0, 0, "p2")]
    [InlineData(0, 0, 10, 12, "p1,p4")]
    [InlineData(10, 1, 10, 31, "p2,p3,ofDeleted,p1,p4")]
    public async Task Period_RomeDays_BothIncluded_TheDayOfSettlementElseOfCreation(
        int fromMonth, int fromDay, int toMonth, int toDay, string expected)
    {
        var world = await SeedAsync();
        var names = new Dictionary<string, Guid>
        {
            ["p1"] = world.P1.Id,
            ["p2"] = world.P2.Id,
            ["p3"] = world.P3.Id,
            ["p4"] = world.P4.Id,
            ["ofDeleted"] = world.OfDeleted.Id,
        };
        var criteria = new PaymentListCriteria(
            From: fromMonth == 0 ? null : new DateOnly(2026, fromMonth, fromDay),
            To: toMonth == 0 ? null : new DateOnly(2026, toMonth, toDay));

        var ids = await IdsAsync(world, criteria);

        Assert.Equal(expected.Length == 0 ? [] : expected.Split(',').Select(name => names[name]).ToList(), ids);
    }

    [Fact]
    public async Task Scope_ACollaboratorLimitedToSomePropertiesGetsTheirPaymentsOnly_WhateverTheCriteria()
    {
        var world = await SeedAsync();

        Assert.Equal(new[] { world.P3.Id, world.P1.Id }, await IdsAsync(world, new PaymentListCriteria(), world.Restricted));
        // The other property, and a booking of the other property, asked for by id: nothing.
        Assert.Empty(await IdsAsync(world, new PaymentListCriteria(PropertyId: world.CasaBianca.Id), world.Restricted));
        Assert.Empty(await IdsAsync(world, new PaymentListCriteria(BookingId: world.OnCasaBianca.Id), world.Restricted));
        Assert.Equal(
            new[] { world.P1.Id },
            await IdsAsync(
                world,
                new PaymentListCriteria(From: new DateOnly(2026, 10, 1), To: new DateOnly(2026, 10, 12)),
                world.Restricted));
    }

    [Fact]
    public async Task Scope_NeverAnotherOrg()
    {
        var world = await SeedAsync();

        var ids = await IdsAsync(world, new PaymentListCriteria(From: new DateOnly(2026, 10, 12), To: new DateOnly(2026, 10, 12)));
        var allOfTheOrg = await IdsAsync(world, new PaymentListCriteria());

        // The 999 of the other org was settled the same day as p1 and is not here.
        Assert.Equal(new[] { world.P1.Id }, ids);
        Assert.Equal(5, allOfTheOrg.Count);
    }
}
