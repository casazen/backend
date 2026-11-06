using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US02/T03 proofs of the operator queue: the lock code leaves only after an operator's approval (AC-NF50-4), and only a user who holds
/// <c>verification.decision</c> and reaches the property lists, decides or counts a verification (AC-NF24-3).
/// </summary>
public class VerificationOperatorDecisionProofTests
{
    private const string LockCode = "4242";
    private const string Operator = "auth0|operator";
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;
        public bool FilterEnabled => true;
    }

    /// <summary>Trust and data rule (AC-NF50-4): a pending or rejected verification never releases the code; an approval releases and e-mails it once.</summary>
    [Fact]
    public async Task VerificationDecision_CodeReleasedOnlyAfterPositiveOperatorDecision_Test()
    {
        var world = new World();
        var property = world.AddProperty("auth0|owner");
        world.AddCode(property);
        var approved = world.AddPendingStay(property, "guest.approved@example.com", language: "en");
        var rejected = world.AddPendingStay(property, "guest.rejected@example.com");
        await world.SaveAsync();

        var emails = new List<(string? To, EmailContent Content, string Template)>();
        var queue = new Mock<IEmailQueue>();
        queue.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
            .Callback<string?, EmailContent, string>((to, content, template) => emails.Add((to, content, template)))
            .Returns(true);

        await using var db = world.NewDb();
        var accessCodes = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);
        var service = NewService(db, queue.Object, accessCodes);
        var scope = new HostScope(world.OrgId);

        // Pending: nobody decided, the gate keeps the code.
        Assert.Null(await accessCodes.ReleaseToGuestAsync(approved, AccessCodeReleaseChannels.CheckInScreen));

        // A rejection is logged as the operator's decision and releases nothing, sends nothing.
        Assert.True(await service.RecordManualVerificationAsync(rejected, scope, false, "Il volto non corrisponde", Operator));
        var rejection = await db.BiometricVerificationLogs.SingleAsync(l => l.BookingId == rejected);
        Assert.Equal(VerificationMethod.Operator, rejection.Method);
        Assert.Equal(BiometricOutcome.Reject, rejection.Outcome);
        Assert.Equal(Operator, rejection.OperatorUserId);
        Assert.Null(await accessCodes.ReleaseToGuestAsync(rejected, AccessCodeReleaseChannels.Email));
        Assert.Null(await accessCodes.GetAccessCodeAsync(rejected, Operator));
        Assert.Empty(emails);
        Assert.False(await db.AccessCodeReleaseLogs.AnyAsync());

        // The approval is logged, releases the code once through the operator channel and e-mails it to the guest.
        Assert.True(await service.RecordManualVerificationAsync(approved, scope, true, null, Operator));
        var approval = await db.BiometricVerificationLogs.SingleAsync(l => l.BookingId == approved);
        Assert.Equal(VerificationMethod.Operator, approval.Method);
        Assert.Equal(BiometricOutcome.Accept, approval.Outcome);
        var release = await db.AccessCodeReleaseLogs.SingleAsync();
        Assert.Equal(approved, release.BookingId);
        Assert.Equal(AccessCodeReleaseChannels.Operator, release.Channel);
        var sent = Assert.Single(emails);
        Assert.Equal("guest.approved@example.com", sent.To);
        Assert.Equal(EmailTemplates.Names.GuestAccessCode, sent.Template);
        Assert.Contains(LockCode, sent.Content.HtmlBody, StringComparison.Ordinal);

        // A decided verification leaves the queue: it cannot be decided twice.
        Assert.Empty(await service.GetPendingVerificationsAsync(Operator, scope));
        Assert.False(await service.RecordManualVerificationAsync(approved, scope, true, null, Operator));
    }

    /// <summary>
    /// Permission rule (AC-NF24-3): only <c>verification.decision</c> reaches the queue, and within it only the properties of the caller's scope: a
    /// member «Solo alcuni» given one property neither lists, nor decides, nor counts the verification of another one.
    /// </summary>
    [Fact]
    public async Task VerificationQueue_OnlyScopeAndPermission_Test()
    {
        // Every action of the queue carries the policy of verification.decision; the collaborator and the accountant do not hold it.
        Assert.Contains(
            CasazenPolicies.VerificationDecision,
            typeof(VerificationController).GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy));
        Assert.DoesNotContain(
            typeof(VerificationController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            m => m.GetCustomAttributes<AllowAnonymousAttribute>().Any());
        foreach (var roleKey in new[] { OrgRoleCatalog.StaffRoleKey, OrgRoleCatalog.AccountantRoleKey })
        {
            var role = OrgRoleCatalog.SeededRoles.Single(r => r.ContextKey == OrgRoleCatalog.ShortRent && r.RoleKey == roleKey);
            Assert.DoesNotContain(HostPermissions.VerificationDecision, role.Permissions);
        }

        const string member = "auth0|collaborator";
        var world = new World();
        var given = world.AddProperty("auth0|owner");
        var other = world.AddProperty("auth0|owner");
        world.Grant(member, given);
        var reachable = world.AddPendingStay(given, "guest.given@example.com");
        var unreachable = world.AddPendingStay(other, "guest.other@example.com");
        world.AddAutomaticReview(reachable);
        world.AddAutomaticReview(unreachable);
        await world.SaveAsync();

        await using var db = world.NewDb();
        var service = NewService(db, Mock.Of<IEmailQueue>(), new AccessCodeService(db, NullLogger<AccessCodeService>.Instance));
        var scope = new HostScope(world.OrgId, GrantedToUserId: member);

        var pending = Assert.Single(await service.GetPendingVerificationsAsync(member, scope));
        Assert.Equal(reachable, pending.BookingId);
        // The technical reasons and the provisional flag of the last automatic outcome come with it (AC-NF51-5).
        Assert.Contains(pending.Reasons, r => r is { Check: VerificationReasons.Checks.Similarity, Result: VerificationReasons.Results.BetweenThresholds });
        Assert.True(pending.Provisional);

        Assert.False(await service.RecordManualVerificationAsync(unreachable, scope, true, null, member));
        Assert.False(await service.RejectVerificationAsync(unreachable, scope, "documento non valido", member));
        Assert.False(await db.BiometricVerificationLogs.AnyAsync(l => l.BookingId == unreachable && l.Method == VerificationMethod.Operator));

        var metrics = await service.GetMetricsAsync(scope, Now.UtcDateTime.AddDays(-1), Now.UtcDateTime.AddDays(1));
        Assert.Equal(1, metrics.Review);
    }

    private static VerificationQueueService NewService(AppDbContext db, IEmailQueue queue, IAccessCodeService accessCodes) => new(
        db,
        queue,
        new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://site.example.test" })),
        Mock.Of<IGuestCheckInService>(),
        NullLogger<VerificationQueueService>.Instance,
        new FixedTimeProvider(Now),
        accessCodes);

    /// <summary>One org on an in-memory database with Data Protection (the lock code is an encrypted column).</summary>
    private sealed class World
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"verification-decision-{Guid.NewGuid()}").Options;

        private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
        private readonly List<object> _rows = [];

        public World()
        {
            _rows.Add(new OrgEntity
            {
                Id = OrgId,
                Name = "Org verifiche",
                Slug = $"verifiche-{OrgId:N}"[..20],
                DisplayName = "Org verifiche",
                ContactEmail = "org@example.com",
                PlanTier = PlanTier.Pro,
            });
        }

        public Guid OrgId { get; } = Guid.NewGuid();

        public AppDbContext NewDb() => new(_options, new FixedTenantContext(OrgId), _dataProtection);

        public Guid AddProperty(string ownerId)
        {
            var property = new Property
            {
                Id = Guid.NewGuid(),
                OrgId = OrgId,
                OwnerId = ownerId,
                Name = "Casa Test",
                Address = "Via Test 1",
                City = "Roma",
                PostalCode = "00100",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 100m,
            };
            _rows.Add(property);
            return property.Id;
        }

        public void AddCode(Guid propertyId) => _rows.Add(new PropertyAccessCode
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            PropertyId = propertyId,
            EncryptedCode = LockCode,
            UpdatedAt = Now.UtcDateTime,
        });

        public void Grant(string userId, Guid propertyId) =>
            _rows.Add(new PropertyMemberAccess { OrgId = OrgId, UserId = userId, PropertyId = propertyId });

        /// <summary>A confirmed stay whose guest went to the operator (an operator request on its session).</summary>
        public Guid AddPendingStay(Guid propertyId, string email, string? language = null)
        {
            var guest = new Guest { Id = Guid.NewGuid(), OrgId = OrgId, FirstName = "Ospite", LastName = "Test", Email = email };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = OrgId,
                PropertyId = propertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 11, 10),
                CheckOutDate = new DateTime(2026, 11, 12),
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                NumberOfGuests = 1,
                TotalPrice = 200m,
            };
            _rows.Add(guest);
            _rows.Add(booking);
            _rows.Add(new GuestCheckInSession
            {
                BookingId = booking.Id,
                OrgId = OrgId,
                TokenHash = Guid.NewGuid().ToString("N"),
                ExpiresAt = Now.UtcDateTime.AddDays(10),
                Status = GuestCheckInSessionStatus.InCompilazione,
                Language = language,
                BiometricConsentAt = Now.UtcDateTime.AddMinutes(-30),
                BiometricConsentVersion = "biometric-v1",
                OperatorRequestedAt = Now.UtcDateTime.AddMinutes(-5),
                CreatedAt = Now.UtcDateTime.AddHours(-1),
            });
            return booking.Id;
        }

        /// <summary>The agent's review of the stay: similarity between the thresholds, provisional thresholds.</summary>
        public void AddAutomaticReview(Guid bookingId)
        {
            var booking = _rows.OfType<Booking>().Single(b => b.Id == bookingId);
            _rows.Add(new BiometricVerificationLog
            {
                OrgId = OrgId,
                BookingId = bookingId,
                GuestId = booking.GuestId,
                Outcome = BiometricOutcome.Escalate,
                Method = VerificationMethod.Biometric,
                Attempts = 1,
                ModelName = "AuraFace-v1",
                ModelVersion = "1.0.0",
                ThresholdsJson = "{\"accept\":0.85,\"escalate\":0.7}",
                ReasonsJson = VerificationReasons.Serialize(
                [
                    new VerificationReason(VerificationReasons.Checks.Liveness, VerificationReasons.Results.Passed, 0.93, 0.7),
                    new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.BetweenThresholds, 0.78, 0.85),
                ]),
                Provisional = true,
                DurationMs = 820,
                CreatedAt = Now.UtcDateTime.AddMinutes(-10),
            });
        }

        public async Task SaveAsync()
        {
            await using var db = NewDb();
            foreach (var row in _rows)
                db.Add(row);
            await db.SaveChangesAsync();
        }
    }
}
