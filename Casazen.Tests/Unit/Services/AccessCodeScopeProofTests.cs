using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US02/T04 permission proof (AM-03, NF-50): the host reads the lock code of a booking only when the booking is one the caller reaches, as the
/// status of the codes does. A collaborator «Solo alcuni» reads the codes of the properties it was given, not those of the other properties of the
/// org; another org's caller reads nothing; and the verification gate still applies inside the scope.
/// </summary>
public class AccessCodeScopeProofTests
{
    private const string LockCodeA = "1111";
    private const string LockCodeB = "2222";
    private const string Member = "auth0|collaborator";

    [Fact]
    public async Task AccessCode_BookingOfAnotherProperty_IsNotReadOutsideTheScope_Test()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"access-code-scope-{Guid.NewGuid()}").Options;
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
        var propertyA = AddProperty(db, orgId, "auth0|owner-a", LockCodeA);
        var propertyB = AddProperty(db, orgId, "auth0|owner-b", LockCodeB);
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = orgId, UserId = Member, PropertyId = propertyA });
        var verifiedA = AddBooking(db, orgId, propertyA, verified: true);
        var verifiedB = AddBooking(db, orgId, propertyB, verified: true);
        var unverifiedA = AddBooking(db, orgId, propertyA, verified: false);
        await db.SaveChangesAsync();
        var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

        // An org-wide caller (the owner, the administrators, the property managers) reads both, once verified.
        var orgWide = new HostScope(orgId);
        Assert.Equal(LockCodeA, await service.GetAccessCodeAsync(verifiedA, "auth0|admin", orgWide));
        Assert.Equal(LockCodeB, await service.GetAccessCodeAsync(verifiedB, "auth0|admin", orgWide));

        // A collaborator «Solo alcuni» given the property A: its code, not the code of B (it is as absent as a booking that does not exist).
        var someProperties = new HostScope(orgId, GrantedToUserId: Member);
        Assert.Equal(LockCodeA, await service.GetAccessCodeAsync(verifiedA, Member, someProperties));
        Assert.Null(await service.GetAccessCodeAsync(verifiedB, Member, someProperties));
        Assert.Null(await service.GetAccessCodeAsync(Guid.NewGuid(), Member, someProperties));

        // The account that owns A alone (the rule of before the team).
        var legacyOwner = new HostScope(orgId, OwnerId: "auth0|owner-a");
        Assert.Equal(LockCodeA, await service.GetAccessCodeAsync(verifiedA, "auth0|owner-a", legacyOwner));
        Assert.Null(await service.GetAccessCodeAsync(verifiedB, "auth0|owner-a", legacyOwner));

        // Inside the scope the verification gate still decides; another org's caller reaches nothing.
        Assert.Null(await service.GetAccessCodeAsync(unverifiedA, Member, someProperties));
        var anotherOrg = new HostScope(Guid.NewGuid());
        Assert.Null(await service.GetAccessCodeAsync(verifiedA, "auth0|stranger", anotherOrg));
        Assert.Null(await service.GetAccessCodeAsync(verifiedB, "auth0|stranger", anotherOrg));
    }

    private static Guid AddProperty(AppDbContext db, Guid orgId, string ownerId, string code)
    {
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = ownerId,
            Name = $"Casa {ownerId}",
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
            EncryptedCode = code,
            UpdatedAt = DateTime.UtcNow,
        });
        return property.Id;
    }

    private static Guid AddBooking(AppDbContext db, Guid orgId, Guid propertyId, bool verified)
    {
        var guest = new Guest { Id = Guid.NewGuid(), OrgId = orgId, FirstName = "Ospite", LastName = "Test", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 12),
            CheckOutDate = new DateTime(2026, 11, 14),
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

        return booking.Id;
    }
}
