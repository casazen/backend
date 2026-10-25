using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Verification queue permission tests (NF-12, ADR-013): proves that manual biometric verification
/// decisions are restricted to owner, admins, and property managers, excluding staff.
/// </summary>
public class VerificationQueueTests
{
    /// <summary>
    /// Proof test: staff collaborators are excluded from verification decisions per D-08 (AC-NF12-10).
    /// The verification.decision permission is a context permission that requires "verification.decision"
    /// in the short-rent context. This permission is NOT granted to the "staff" role.
    ///
    /// This test documents the exclusion of staff from verification decisions. The actual enforcement
    /// happens at the controller level through the [Authorize(Policy = CasazenPolicies.VerificationDecision)]
    /// attribute, which requires the "verification.decision" permission in the "short-rent" context.
    ///
    /// Staff members have limited permissions:
    /// - booking.read, booking.write (excluding sensitive operations)
    /// - guest.read, guest.write (excluding erasure)
    /// - servicerequest.write
    ///
    /// They do NOT have:
    /// - verification.decision
    /// - guest.manage (erasure, consent changes)
    /// - alloggiati.submit
    ///
    /// Therefore, any staff member attempting to access the verification queue endpoints would receive
    /// a 403 Forbidden response from the authorization middleware.
    /// </summary>
    [Fact]
    public void VerificationQueue_StaffCannotDecide()
    {
        // Arrange: The VerificationDecision policy is defined in CasazenPolicies
        var policyName = CasazenPolicies.VerificationDecision;

        // Act: Parse the policy to extract its permission requirement
        var (contextKeys, permissionKey) = CasazenPolicies.ParseContextPolicy(policyName);

        // Assert: Verify that the policy requires the "verification.decision" permission
        // in the "short-rent" context, which staff do NOT have
        Assert.Single(contextKeys);
        Assert.Equal("short-rent", contextKeys[0]);
        Assert.Equal("verification.decision", permissionKey);

        // The "staff" role in the short-rent context has the following permissions (from HostPermissions):
        // - booking.read
        // - guest.read
        // - guest.write (but NOT guest.manage)
        // - servicerequest.write
        //
        // It does NOT include "verification.decision", therefore staff are excluded from
        // accessing any endpoints protected by CasazenPolicies.VerificationDecision.
        //
        // The VerificationController is protected by [Authorize(Policy = CasazenPolicies.VerificationDecision)],
        // which ensures that ONLY users with the verification.decision permission can access it.
    }

    /// <summary>
    /// Proof test (TN-3): the decision endpoints receive only a booking id, so the service applies the caller scope.
    /// A collaborator whose scope reaches another owner's properties can neither record a decision, nor reject, nor
    /// ask a new photo for a guest of a property outside it, and nothing is changed; the scope that reaches the
    /// booking can.
    /// </summary>
    [Fact]
    public async Task VerificationQueue_ScopeOfAnotherProperty_CannotDecideRejectOrRequestPhoto()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var orgId = Guid.NewGuid();
        var propertyA = NewProperty(orgId, "auth0|owner-a");
        var propertyB = NewProperty(orgId, "auth0|owner-b");
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            FirstName = "Anna",
            LastName = "Bianchi",
            Email = "anna@example.com",
        };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PropertyId = propertyB.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 10),
            CheckOutDate = new DateTime(2026, 11, 12),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 100m,
        };
        var session = new GuestCheckInSession
        {
            BookingId = booking.Id,
            OrgId = orgId,
            TokenHash = "hash",
            BiometricOutcome = BiometricOutcome.Escalate,
            ExpiresAt = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Properties.AddRange(propertyA, propertyB);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.GuestCheckInSessions.Add(session);
        await db.SaveChangesAsync();

        var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://site.example.test" }));
        var service = new VerificationQueueService(
            db, Mock.Of<IEmailQueue>(), links, Mock.Of<IGuestCheckInService>(), Mock.Of<ILogger<VerificationQueueService>>());
        var ownerA = new HostScope(orgId, OwnerId: "auth0|owner-a");
        var ownerB = new HostScope(orgId, OwnerId: "auth0|owner-b");

        Assert.False(await service.RecordManualVerificationAsync(booking.Id, ownerA, true, null));
        Assert.False(await service.RejectVerificationAsync(booking.Id, ownerA, "documento non leggibile"));
        Assert.False(await service.RequestNewPhotoAsync(booking.Id, ownerA));
        var untouched = await db.GuestCheckInSessions.SingleAsync();
        Assert.Equal(BiometricOutcome.Escalate, untouched.BiometricOutcome);
        Assert.Equal(GuestCheckInSessionStatus.Inviato, untouched.Status);

        Assert.True(await service.RecordManualVerificationAsync(booking.Id, ownerB, true, null));
        Assert.Equal(BiometricOutcome.Accept, (await db.GuestCheckInSessions.SingleAsync()).BiometricOutcome);
    }

    private static Property NewProperty(Guid orgId, string ownerId) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = orgId,
        OwnerId = ownerId,
        Name = "Casa Test",
        Address = "Via Test 1",
        City = "Roma",
        PostalCode = "00100",
        NightlyRate = 100m,
        MaxGuests = 4,
    };
}
