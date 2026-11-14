using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E25/F01/US01/T01, brief point 7 (<c>RequiredBeforeAccessCode</c>): the lock code is held back from the guest by the server, in the one gate of every release
/// (<c>ReleaseToGuestAsync</c>: the screen, the e-mail, the operator's approval), while the organization asks for the tourist tax before the code and the
/// guest owes it. The host's own read of the code is not the guest's and is not held back.
/// </summary>
public class AccessCodeTouristTaxHoldProofTests
{
    private const string LockCode = "4321#8765";

    [Fact]
    public async Task AccessCode_HeldBackFromTheGuestInEveryChannel_WhileTheTouristTaxIsDue_ThenReleased_Test()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"access-code-hold-{Guid.NewGuid()}").Options;
        await using var db = new AppDbContext(options, null, new EphemeralDataProtectionProvider());
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Org codici",
            Slug = $"codici-{orgId:N}"[..20],
            DisplayName = "Org codici",
            ContactEmail = "org@example.com",
            PlanTier = PlanTier.Pro,
        });
        var bookingId = Seed(db, orgId);
        await db.SaveChangesAsync();
        var held = true;
        var touristTax = new Mock<ITouristTaxCollectionService>();
        touristTax.Setup(t => t.IsAccessCodeHeldBackAsync(bookingId, It.IsAny<CancellationToken>())).ReturnsAsync(() => held);
        var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance, touristTax.Object);

        // Held back: no channel gets the code around the gate, the reason is the one the guest page reads, and nothing is logged as released.
        foreach (var channel in AccessCodeReleaseChannels.All)
            Assert.Null(await service.ReleaseToGuestAsync(bookingId, channel));
        Assert.Equal(AccessCodeHoldReasons.TouristTaxDue, await service.GetHoldReasonAsync(bookingId));
        Assert.Empty(await db.Set<AccessCodeReleaseLog>().AsNoTracking().ToListAsync());

        // The host's own read (with its scope and the verification gate) is the host's: it is not held back.
        Assert.Equal(LockCode, await service.GetAccessCodeAsync(bookingId, "auth0|host", new HostScope(orgId)));

        // Paid (or not asked for): the code is released at the next ask, once logged, and no reason is given any more.
        held = false;
        Assert.Null(await service.GetHoldReasonAsync(bookingId));
        Assert.Equal(LockCode, await service.ReleaseToGuestAsync(bookingId, AccessCodeReleaseChannels.CheckInScreen));
        var log = Assert.Single(await db.Set<AccessCodeReleaseLog>().AsNoTracking().ToListAsync());
        Assert.Equal(AccessCodeReleaseChannels.CheckInScreen, log.Channel);
    }

    [Fact]
    public async Task AccessCode_WithoutTheTouristTaxCollection_IsReleasedAsBefore()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"access-code-nohold-{Guid.NewGuid()}").Options;
        await using var db = new AppDbContext(options, null, new EphemeralDataProtectionProvider());
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Org codici",
            Slug = $"codici-{orgId:N}"[..20],
            DisplayName = "Org codici",
            ContactEmail = "org@example.com",
            PlanTier = PlanTier.Pro,
        });
        var bookingId = Seed(db, orgId);
        await db.SaveChangesAsync();
        var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

        Assert.Null(await service.GetHoldReasonAsync(bookingId));
        Assert.Equal(LockCode, await service.ReleaseToGuestAsync(bookingId, AccessCodeReleaseChannels.Email));
    }

    /// <summary>A confirmed stay to come on a property with a lock code, whose verification passed.</summary>
    private static Guid Seed(AppDbContext db, Guid orgId)
    {
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = "auth0|host",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };
        db.Properties.Add(property);
        db.Set<PropertyAccessCode>().Add(new PropertyAccessCode
        {
            OrgId = orgId,
            PropertyId = property.Id,
            EncryptedCode = LockCode,
            UpdatedAt = DateTime.UtcNow,
        });
        var guest = new Guest { Id = Guid.NewGuid(), OrgId = orgId, FirstName = "Ospite", LastName = "Test", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 200m,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.BiometricVerificationLogs.Add(new BiometricVerificationLog
        {
            OrgId = orgId,
            BookingId = booking.Id,
            GuestId = guest.Id,
            Outcome = BiometricOutcome.Accept,
            Method = VerificationMethod.Biometric,
            Attempts = 1,
            ModelName = "AuraFace-v1",
            ModelVersion = "1.0.0",
            ThresholdsJson = "{}",
            CreatedAt = DateTime.UtcNow,
        });
        return booking.Id;
    }
}
