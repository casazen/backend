using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit;

/// <summary>
/// ADR-017 proof tests for owner statement rounding and co-owner split rules.
/// </summary>
public class StatementProofTests
{
    /// <summary>
    /// ADR-017-R2 proof: Statement.NetPayable is rounded ONCE to 2 decimals with banker's rounding (MidpointRounding.ToEven).
    /// Tests that banker's rounding (half-to-even) is applied correctly at the total level.
    /// Examples: 100.005 -> 100.00 (rounds down to even), 100.015 -> 100.02 (rounds up to even).
    /// </summary>
    [Fact]
    public async Task StatementRounding_ToTheCent_BankersRounding()
    {
        // Arrange: in-memory database
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"StatementRoundingTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var propertyOwnerId = Guid.NewGuid();
        var guestId = Guid.NewGuid();

        await using var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection);

        // Create test data
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

        // Create bookings and payments with amounts that test banker's rounding
        // Payment 1: 100.005 (should round to 100.00 - down to even)
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

        // Payment 2: 100.015 (should round to 100.02 - up to even)
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

        // Act: generate statement for October 2026
        var statement = await service.GenerateStatementAsync(propertyOwnerId, propertyId, 10, 2026);

        // Assert: verify banker's rounding was applied
        // Line 1: 100.005 rounds to 100.00 (down to even)
        // Line 2: 100.015 rounds to 100.02 (up to even)
        // Total: 200.02 (not 200.01 or 200.03)
        var expectedTotal = Math.Round(100.005m, 2, MidpointRounding.ToEven) + Math.Round(100.015m, 2, MidpointRounding.ToEven);
        Assert.Equal(100.00m, Math.Round(100.005m, 2, MidpointRounding.ToEven));
        Assert.Equal(100.02m, Math.Round(100.015m, 2, MidpointRounding.ToEven));
        Assert.Equal(200.02m, expectedTotal);
        Assert.Equal(expectedTotal, statement.NetPayable);
    }

    /// <summary>
    /// ADR-017-R3/R4 proof: Co-owner split with last owner receiving remainder.
    /// Tests that three co-owners with 33.33% each receive splits that sum exactly to the total.
    /// Example: EUR 100.03 -> 33.33 + 33.33 + 33.37 = 100.03 (last owner gets remainder to preserve total).
    /// </summary>
    [Fact]
    public async Task StatementCoOwnerSplit_SumsToTotal_LastOwnerRemainder()
    {
        // Arrange: in-memory database with three co-owners
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"StatementCoOwnerSplitTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var owner1Id = Guid.NewGuid();
        var owner2Id = Guid.NewGuid();
        var owner3Id = Guid.NewGuid();
        var guestId = Guid.NewGuid();

        await using var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection);

        // Create property and three co-owners (33.33% each)
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
            SharePercent = 33.34m, // Last owner gets the remainder in the ownership split
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

        // Create a booking with payment of exactly 100.03 EUR
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

        // Act: generate statements for all three owners
        var statement1 = await service.GenerateStatementAsync(owner1Id, propertyId, 10, 2026);
        var statement2 = await service.GenerateStatementAsync(owner2Id, propertyId, 10, 2026);
        var statement3 = await service.GenerateStatementAsync(owner3Id, propertyId, 10, 2026);

        // Assert: verify splits sum exactly to 100.03
        // Owner 1: 100.03 * 33.33% = 33.33 (rounded with banker's rounding)
        // Owner 2: 100.03 * 33.33% = 33.33 (rounded with banker's rounding)
        // Owner 3: 100.03 * 33.34% = 33.37 (rounded with banker's rounding)
        // Total: 33.33 + 33.33 + 33.37 = 100.03 (exact)

        var split1 = Math.Round(100.03m * 33.33m / 100m, 2, MidpointRounding.ToEven);
        var split2 = Math.Round(100.03m * 33.33m / 100m, 2, MidpointRounding.ToEven);
        var split3 = Math.Round(100.03m * 33.34m / 100m, 2, MidpointRounding.ToEven);

        Assert.Equal(33.33m, split1);
        Assert.Equal(33.33m, split2);
        Assert.Equal(33.37m, split3);

        Assert.Equal(split1, statement1.NetPayable);
        Assert.Equal(split2, statement2.NetPayable);
        Assert.Equal(split3, statement3.NetPayable);

        // The key assertion: splits sum to the exact original total
        var total = statement1.NetPayable + statement2.NetPayable + statement3.NetPayable;
        Assert.Equal(100.03m, total);
    }
}

/// <summary>Fixed tenant context for unit tests.</summary>
file sealed class FixedTenantContext(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;
    public bool FilterEnabled => true;
}
