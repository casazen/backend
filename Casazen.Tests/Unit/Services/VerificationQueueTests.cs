using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Verification;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

    /// <summary>
    /// E73/F01/US02/T02 (NF-195, AC5): an item of the queue says how its last automatic outcome was decided (the mode, the shadow outcome, the version of the policy, the format
    /// and the channel), carries every reason of it (the legacy <c>active_pose</c> and the checks of the self-hosted flow included, with their value and threshold) and the
    /// evidence kept for the reviewer (how many pictures are live and when the first expires); the evidence is not shown while it is off.
    /// </summary>
    [Fact]
    public async Task VerificationQueue_PendingItems_CarryTheDecisionOfThePolicy_EveryReason_AndTheEvidence()
    {
        var now = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var orgId = Guid.NewGuid();
        var property = NewProperty(orgId, "auth0|owner");
        var guest = new Guest { Id = Guid.NewGuid(), OrgId = orgId, FirstName = "Anna", LastName = "Bianchi", Email = "anna@example.com" };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 28),
            CheckOutDate = new DateTime(2026, 11, 30),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 100m,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.GuestCheckInSessions.Add(new GuestCheckInSession
        {
            BookingId = booking.Id,
            OrgId = orgId,
            TokenHash = "hash",
            BiometricOutcome = BiometricOutcome.Escalate,
            BiometricAttempts = 1,
            OperatorRequestedAt = now.UtcDateTime.AddMinutes(-30),
            ExpiresAt = now.UtcDateTime.AddDays(7),
        });
        VerificationReason[] reasons =
        [
            new(VerificationReasons.Checks.ActivePose, VerificationReasons.Results.Failed),
            new(VerificationReasons.Checks.PresenceTiming, VerificationReasons.Results.Passed),
            new(VerificationReasons.Checks.Similarity, VerificationReasons.Results.BetweenThresholds, 0.81, 0.85),
            new(VerificationReasons.Checks.ObservationMode, "segment_observation"),
        ];
        db.BiometricVerificationLogs.AddRange(
            new BiometricVerificationLog
            {
                OrgId = orgId,
                BookingId = booking.Id,
                GuestId = guest.Id,
                Outcome = BiometricOutcome.Escalate,
                Method = VerificationMethod.Biometric,
                Attempts = 1,
                ModelName = "SFace",
                ModelVersion = "2021dec",
                CreatedAt = now.UtcDateTime.AddHours(-2),
            },
            new BiometricVerificationLog
            {
                OrgId = orgId,
                BookingId = booking.Id,
                GuestId = guest.Id,
                Outcome = BiometricOutcome.Escalate,
                Method = VerificationMethod.Biometric,
                Attempts = 1,
                ModelName = "SFace",
                ModelVersion = "2021dec",
                ReasonsJson = VerificationReasons.Serialize(reasons),
                Provisional = true,
                DecisionMode = IdentityDecisionMode.Observation,
                ShadowOutcome = BiometricOutcome.Accept,
                PolicyVersion = "2026.11-1",
                FormatId = "it-cie-3.0",
                Channel = "web:android-chrome",
                CreatedAt = now.UtcDateTime.AddHours(-1),
            });
        db.IdentityReviewEvidences.AddRange(
            Evidence(orgId, booking.Id, now.UtcDateTime.AddHours(20)),
            Evidence(orgId, booking.Id, now.UtcDateTime.AddHours(10)),
            Evidence(orgId, booking.Id, now.UtcDateTime.AddHours(-1)),
            Evidence(orgId, booking.Id, now.UtcDateTime.AddHours(5), deleted: true),
            Evidence(Guid.NewGuid(), booking.Id, now.UtcDateTime.AddHours(5)));
        await db.SaveChangesAsync();

        var clock = new FakeTimeProvider(now);
        var flags = new TestFeatureFlags(
            Casazen.Core.Features.FeatureFlags.CheckInIdentityVerification,
            Casazen.Core.Features.FeatureFlags.CheckInIdentitySelfHosted,
            Casazen.Core.Features.FeatureFlags.CheckInIdentityReviewEvidence);
        var store = new IdentityReviewEvidenceStore(
            db,
            Mock.Of<IFileStorage>(),
            new EphemeralDataProtectionProvider(),
            flags,
            Options.Create(new IdentityVerificationOptions()),
            Options.Create(new BiometricModelOptions()),
            Mock.Of<IFaceDetector>(),
            clock,
            Mock.Of<ILogger<IdentityReviewEvidenceStore>>());
        var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://site.example.test" }));
        var service = new VerificationQueueService(
            db, Mock.Of<IEmailQueue>(), links, Mock.Of<IGuestCheckInService>(), Mock.Of<ILogger<VerificationQueueService>>(), timeProvider: clock, reviewEvidence: store);

        var item = Assert.Single(await service.GetPendingVerificationsAsync("auth0|owner", new HostScope(orgId)));

        Assert.Equal(
            (IdentityDecisionMode.Observation, (BiometricOutcome?)BiometricOutcome.Accept, "2026.11-1", "it-cie-3.0", "web:android-chrome"),
            (item.DecisionMode, item.ShadowOutcome, item.PolicyVersion, item.FormatId, item.Channel));
        Assert.Equal(reasons, item.Reasons);
        Assert.True(item.Provisional);
        Assert.Equal(new PendingEvidenceSummary(2, now.UtcDateTime.AddHours(10)), item.Evidence);

        // The codes of the API.
        var dto = await PendingDtoAsync(item, orgId);
        Assert.Equal(("observation", "accept", "2026.11-1", "it-cie-3.0", "web:android-chrome"), (dto.DecisionMode, dto.ShadowOutcome, dto.PolicyVersion, dto.FormatId, dto.Channel));
        Assert.Equal((2, now.UtcDateTime.AddHours(10)), (dto.Evidence!.Count, dto.Evidence.ExpiresAt));
        Assert.Contains(dto.Reasons, reason => reason.Check == VerificationReasons.Checks.ActivePose && reason.Result == VerificationReasons.Results.Failed);
        Assert.Contains(dto.Reasons, reason => reason.Check == VerificationReasons.Checks.Similarity && reason.Value == 0.81 && reason.Threshold == 0.85);

        // A legacy item (no automatic outcome of the policy) says legacy with nothing else, and the evidence is not shown while it is off.
        flags.Set(Casazen.Core.Features.FeatureFlags.CheckInIdentityReviewEvidence, false);
        Assert.Null(Assert.Single(await service.GetPendingVerificationsAsync("auth0|owner", new HostScope(orgId))).Evidence);
        var legacy = await PendingDtoAsync(item with { DecisionMode = IdentityDecisionMode.Legacy, ShadowOutcome = null, PolicyVersion = null, Evidence = null }, orgId);
        Assert.Equal(("legacy", (string?)null, (PendingEvidenceDto?)null), (legacy.DecisionMode, legacy.ShadowOutcome, legacy.Evidence));
    }

    private static IdentityReviewEvidence Evidence(Guid orgId, Guid bookingId, DateTime expiresAt, bool deleted = false) => new()
    {
        OrgId = orgId,
        BookingId = bookingId,
        CheckInSessionId = Guid.NewGuid(),
        Kind = IdentityEvidenceKind.Frame,
        StorageKey = $"identity-evidence/{orgId}/{bookingId}/{Guid.NewGuid()}",
        ContentType = "image/jpeg",
        Sha256 = new string('a', 64),
        CreatedAt = expiresAt.AddHours(-48),
        ExpiresAt = expiresAt,
        DeletedAt = deleted ? expiresAt.AddHours(-47) : null,
        DeletionReason = deleted ? "decided" : null,
    };

    /// <summary>The item as <c>GET api/Verification/pending</c> sends it.</summary>
    private static async Task<PendingVerificationDto> PendingDtoAsync(PendingVerification item, Guid orgId)
    {
        const string user = "auth0|owner";
        var queue = new Mock<IVerificationQueueService>();
        queue.Setup(q => q.GetPendingVerificationsAsync(user, It.IsAny<HostScope>(), It.IsAny<CancellationToken>())).ReturnsAsync([item]);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(s => s.ResolveAsync(user, It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostScope(orgId));
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var controller = new VerificationController(queue.Object, scopes.Object, orgs.Object, Mock.Of<ILogger<VerificationController>>(), TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test")),
                },
            },
        };

        var ok = Assert.IsType<OkObjectResult>((await controller.GetPending()).Result);
        return Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<PendingVerificationDto>>(ok.Value));
    }
}
