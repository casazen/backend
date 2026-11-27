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
/// E05/F07/US01/T03, after the review: the release of the lock code in two steps. <c>ReleaseToGuestAsync</c> is the gate and the log in one call (the screen, the e-mail, the operator's approval); a message
/// of a routine that carries the code takes it first (<c>GetReleasableCodeAsync</c>: the same gate, nothing written) and logs the release only once the message has gone
/// (<c>RecordReleaseAsync</c>), so a message that is refused or fails leaves no line that says a code reached a guest.
/// </summary>
public class AccessCodeTwoStepReleaseTests
{
    private const string LockCode = "4321#8765";

    private static (AppDbContext Db, Guid BookingId, Guid OrgId) Seed(Mock<ITouristTaxCollectionService>? touristTax = null, bool verified = true)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"access-code-two-step-{Guid.NewGuid()}").Options;
        var db = new AppDbContext(options, null, new EphemeralDataProtectionProvider());
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
        db.Set<PropertyAccessCode>().Add(new PropertyAccessCode { OrgId = orgId, PropertyId = property.Id, EncryptedCode = LockCode, UpdatedAt = DateTime.UtcNow });
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
        if (verified)
        {
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
        }

        db.SaveChanges();
        if (touristTax is not null)
            touristTax.Setup(t => t.IsAccessCodeHeldBackAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        return (db, booking.Id, orgId);
    }

    /// <summary>
    /// PROOF TEST: privacy and audit rule. Taking the code passes the gate of the release (the guest is verified) and writes nothing; the line of the release log (the channel, never the code) is written by
    /// <c>RecordReleaseAsync</c> once, when the message has gone. <c>ReleaseToGuestAsync</c> is both, as it always was.
    /// </summary>
    [Fact]
    public async Task TakingTheCodeWritesNoLog_RecordingTheReleaseWritesOne_AndTheSingleCallIsBoth()
    {
        var (db, bookingId, orgId) = Seed();
        await using var owned = db;
        var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

        Assert.Equal(LockCode, await service.GetReleasableCodeAsync(bookingId, AccessCodeReleaseChannels.GuestArea));
        Assert.Empty(await db.Set<AccessCodeReleaseLog>().AsNoTracking().ToListAsync());

        await service.RecordReleaseAsync(bookingId, AccessCodeReleaseChannels.GuestArea);
        var recorded = Assert.Single(await db.Set<AccessCodeReleaseLog>().AsNoTracking().ToListAsync());
        Assert.Equal(AccessCodeReleaseChannels.GuestArea, recorded.Channel);
        Assert.Equal(bookingId, recorded.BookingId);
        Assert.Equal(orgId, recorded.OrgId);

        Assert.Equal(LockCode, await service.ReleaseToGuestAsync(bookingId, AccessCodeReleaseChannels.Email));
        Assert.Equal(2, (await db.Set<AccessCodeReleaseLog>().AsNoTracking().ToListAsync()).Count);
    }

    /// <summary>The gate is the same one: a guest who is not verified, and a code held back for the tourist tax, are given nothing and nothing is written; an unknown channel is refused by both halves.</summary>
    [Fact]
    public async Task TakingTheCode_StopsAtTheGateOfTheRelease_AndAnUnknownChannelIsRefused()
    {
        var (notVerified, unverifiedBooking, _) = Seed(verified: false);
        await using (notVerified)
        {
            var service = new AccessCodeService(notVerified, NullLogger<AccessCodeService>.Instance);
            Assert.Null(await service.GetReleasableCodeAsync(unverifiedBooking, AccessCodeReleaseChannels.PortalChat));
            Assert.Empty(await notVerified.Set<AccessCodeReleaseLog>().AsNoTracking().ToListAsync());
        }

        var touristTax = new Mock<ITouristTaxCollectionService>();
        var (held, heldBooking, _) = Seed(touristTax);
        await using (held)
        {
            var service = new AccessCodeService(held, NullLogger<AccessCodeService>.Instance, touristTax.Object);
            Assert.Null(await service.GetReleasableCodeAsync(heldBooking, AccessCodeReleaseChannels.PortalChat));
            Assert.Equal(AccessCodeHoldReasons.TouristTaxDue, await service.GetHoldReasonAsync(heldBooking));
            Assert.Empty(await held.Set<AccessCodeReleaseLog>().AsNoTracking().ToListAsync());

            await Assert.ThrowsAsync<ArgumentException>(() => service.GetReleasableCodeAsync(heldBooking, "carrier-pigeon"));
            await Assert.ThrowsAsync<ArgumentException>(() => service.RecordReleaseAsync(heldBooking, "carrier-pigeon"));
        }
    }
}
