using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit;

public class StatementProofTests
{
    [Fact]
    public async Task StatementRounding_ToTheCent_BankersRounding()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"StatementRoundingTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = TestDataProtection.Shared;
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var propertyOwnerId = Guid.NewGuid();
        var guestId = Guid.NewGuid();

        await using var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection);

        var property = new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "test-owner",
            Name = "Test Property",
            Address = "Test Address",
            City = "Rome",
            CreatedAt = DateTime.UtcNow
        };

        var owner = new PropertyOwner
        {
            Id = propertyOwnerId,
            OrgId = orgId,
            FirstName = "Test",
            LastName = "Owner",
            Email = "test@example.com",
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow
        };

        var ownership = new PropertyOwnership
        {
            OrgId = orgId,
            PropertyId = propertyId,
            PropertyOwnerId = propertyOwnerId,
            SharePercent = 100.00m,
            OwnershipStart = DateTime.UtcNow.AddMonths(-1)
        };

        var guest = new Guest
        {
            Id = guestId,
            OrgId = orgId,
            FirstName = "Test",
            LastName = "Guest",
            Email = "guest@example.com",
            PhoneNumber = "+39123456789"
        };

        var booking1 = new Booking
        {
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = new DateTime(2026, 10, 1),
            CheckOutDate = new DateTime(2026, 10, 5),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 100.005m
        };

        var payment1 = new Payment
        {
            OrgId = orgId,
            BookingId = booking1.Id,
            Amount = 100.005m,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            ProcessedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
        };

        var booking2 = new Booking
        {
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = new DateTime(2026, 10, 6),
            CheckOutDate = new DateTime(2026, 10, 10),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 100.015m
        };

        var payment2 = new Payment
        {
            OrgId = orgId,
            BookingId = booking2.Id,
            Amount = 100.015m,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            ProcessedAt = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc)
        };

        db.Properties.Add(property);
        db.PropertyOwners.Add(owner);
        db.PropertyOwnerships.Add(ownership);
        db.Guests.Add(guest);
        db.Bookings.Add(booking1);
        db.Bookings.Add(booking2);
        db.Payments.Add(payment1);
        db.Payments.Add(payment2);
        await db.SaveChangesAsync();

        var service = new StatementService(db);

        var statement = await service.GenerateStatementAsync(propertyOwnerId, propertyId, 10, 2026);

        var expectedTotal = Math.Round(100.005m, 2, MidpointRounding.ToEven) + Math.Round(100.015m, 2, MidpointRounding.ToEven);
        Assert.Equal(100.00m, Math.Round(100.005m, 2, MidpointRounding.ToEven));
        Assert.Equal(100.02m, Math.Round(100.015m, 2, MidpointRounding.ToEven));
        Assert.Equal(200.02m, expectedTotal);
        Assert.Equal(expectedTotal, statement.NetPayable);
    }

    [Fact]
    public async Task StatementCoOwnerSplit_SumsToTotal_LastOwnerRemainder()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"StatementCoOwnerSplitTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = TestDataProtection.Shared;
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var owner1Id = Guid.NewGuid();
        var owner2Id = Guid.NewGuid();
        var owner3Id = Guid.NewGuid();
        var guestId = Guid.NewGuid();

        await using var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection);

        var property = new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "test-owner",
            Name = "Test Property",
            Address = "Test Address",
            City = "Rome",
            CreatedAt = DateTime.UtcNow
        };

        var owner1 = new PropertyOwner
        {
            Id = owner1Id,
            OrgId = orgId,
            FirstName = "Owner",
            LastName = "One",
            Email = "owner1@example.com",
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow
        };

        var owner2 = new PropertyOwner
        {
            Id = owner2Id,
            OrgId = orgId,
            FirstName = "Owner",
            LastName = "Two",
            Email = "owner2@example.com",
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow
        };

        var owner3 = new PropertyOwner
        {
            Id = owner3Id,
            OrgId = orgId,
            FirstName = "Owner",
            LastName = "Three",
            Email = "owner3@example.com",
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow
        };

        var ownership1 = new PropertyOwnership
        {
            OrgId = orgId,
            PropertyId = propertyId,
            PropertyOwnerId = owner1Id,
            SharePercent = 33.33m,
            OwnershipStart = DateTime.UtcNow.AddMonths(-1)
        };

        var ownership2 = new PropertyOwnership
        {
            OrgId = orgId,
            PropertyId = propertyId,
            PropertyOwnerId = owner2Id,
            SharePercent = 33.33m,
            OwnershipStart = DateTime.UtcNow.AddMonths(-1)
        };

        var ownership3 = new PropertyOwnership
        {
            OrgId = orgId,
            PropertyId = propertyId,
            PropertyOwnerId = owner3Id,
            SharePercent = 33.34m,
            OwnershipStart = DateTime.UtcNow.AddMonths(-1)
        };

        var guest = new Guest
        {
            Id = guestId,
            OrgId = orgId,
            FirstName = "Test",
            LastName = "Guest",
            Email = "guest@example.com",
            PhoneNumber = "+39123456789"
        };

        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = new DateTime(2026, 10, 1),
            CheckOutDate = new DateTime(2026, 10, 5),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 100.03m
        };

        var payment = new Payment
        {
            OrgId = orgId,
            BookingId = booking.Id,
            Amount = 100.03m,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            ProcessedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
        };

        db.Properties.Add(property);
        db.PropertyOwners.AddRange(owner1, owner2, owner3);
        db.PropertyOwnerships.AddRange(ownership1, ownership2, ownership3);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var service = new StatementService(db);

        var statement1 = await service.GenerateStatementAsync(owner1Id, propertyId, 10, 2026);
        var statement2 = await service.GenerateStatementAsync(owner2Id, propertyId, 10, 2026);

        var statement3 = await service.GenerateStatementAsync(owner3Id, propertyId, 10, 2026);

        var propertyTotal = 100.03m;
        var owner1Expected = Math.Round(propertyTotal * 33.33m / 100m, 2, MidpointRounding.ToEven);
        var owner2Expected = Math.Round(propertyTotal * 33.33m / 100m, 2, MidpointRounding.ToEven);

        var owner3Expected = Math.Round(propertyTotal, 2, MidpointRounding.ToEven) - owner1Expected - owner2Expected;

        Assert.Equal(owner1Expected, statement1.NetPayable);
        Assert.Equal(owner2Expected, statement2.NetPayable);
        Assert.Equal(owner3Expected, statement3.NetPayable);

        var total = statement1.NetPayable + statement2.NetPayable + statement3.NetPayable;
        Assert.Equal(propertyTotal, total);

        Assert.Equal(33.35m, statement3.NetPayable);
    }
}

file sealed class FixedTenantContext(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;
    public bool FilterEnabled => true;
}
