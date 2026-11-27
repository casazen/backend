using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US02/T03 proofs of who is told that an identity verification waits for a person (AC-NF50-3, AC-NF24-3): only the users who hold
/// <c>verification.decision</c> and reach the property, with the roles the model seeds; the texts name the property, the date and the reason code and
/// never the guest; the "To do" is one for the booking; a stay that is not active raises nothing.
/// </summary>
public class VerificationOperatorAlertsTests
{
    private const string Creator = "auth0|creator";

    /// <summary>
    /// Permission rule: the owner, the administrator and the property manager are told, and the creator of a property that belongs to no team (the rule
    /// of before the team); never the accountant, a collaborator (the seeded role does not hold the permission, with or without the grant of «Solo
    /// alcuni»), a deactivated member, an inactive account, someone of another org, a stranger of the org in no team.
    /// </summary>
    [Fact]
    public async Task VerificationOperatorAudience_OnlyWhoDecidesAndReachesTheProperty_Test()
    {
        using var world = World.Create();
        var owner = world.Member("auth0|owner", OrgRole.Owner);
        var admin = world.Member("auth0|admin", OrgRole.Admin);
        var manager = world.Member("auth0|manager", OrgRole.PropertyManager);
        world.Member("auth0|accountant", OrgRole.Accountant);
        var collaborator = world.Member("auth0|collaborator", OrgRole.Collaborator, PropertyScope.Selected);
        var granted = world.Member("auth0|granted", OrgRole.Collaborator, PropertyScope.Selected);
        world.Db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = world.Org.Id, UserId = granted, PropertyId = world.Property.Id });
        world.Member("auth0|deactivated", OrgRole.PropertyManager, status: OrgMemberStatus.Deactivated);
        world.Member("auth0|inactive", OrgRole.PropertyManager, active: false);
        world.Member("auth0|elsewhere", OrgRole.PropertyManager, orgId: world.OtherOrg.Id);
        OrgTeamTestData.AddUser(world.Db, "auth0|stranger", world.Org.Id);
        OrgTeamTestData.AddUser(world.Db, Creator, world.Org.Id);
        await world.Db.SaveChangesAsync();

        var told = await ToldAsync(world);
        Assert.Equal(new[] { owner, admin, manager, Creator }.Order(StringComparer.Ordinal), told);

        // The seeded role of the collaborator does not hold the permission: the grant of «Solo alcuni» changes nothing for it.
        Assert.DoesNotContain(collaborator, told);
        Assert.DoesNotContain(granted, told);

        // If a role of the matrix ever gave a collaborator the permission, the rule of reach is the grant (without the area rule of AM-02c, which
        // the collaborator never reaches here because it never holds the permission): with the grant yes, without it no.
        var staffRole = OrgTeamTestData.RoleId(world.Db, OrgRoleCatalog.ShortRent, OrgRoleCatalog.StaffRoleKey);
        world.Db.RolePermissions.Add(new RolePermission { RoleId = staffRole, PermissionKey = HostPermissions.VerificationDecision });
        await world.Db.SaveChangesAsync();
        var withPermission = await ToldAsync(world);
        Assert.Contains(granted, withPermission);
        Assert.DoesNotContain(collaborator, withPermission);
    }

    /// <summary>
    /// Privacy and trust rule: the push, the bell and the e-mails name the property, the arrival date and the reason code, never the guest; they reach
    /// the operators only; the booking has one "To do" however many times it is raised; a stay that is not active, an unknown reason and a session that
    /// does not exist raise nothing; resolving closes the "To do".
    /// </summary>
    [Fact]
    public async Task VerificationOperatorAlerts_TheTextsNameNoGuest_OneTodoForTheBooking_Test()
    {
        using var world = World.Create();
        var owner = world.Member("auth0|owner", OrgRole.Owner);
        var manager = world.Member("auth0|manager", OrgRole.PropertyManager);
        var accountant = world.Member("auth0|accountant", OrgRole.Accountant);
        var collaborator = world.Member("auth0|collaborator", OrgRole.Collaborator, PropertyScope.Selected);
        OrgTeamTestData.AddUser(world.Db, Creator, world.Org.Id);
        var (booking, session) = world.Stay("Mario", "Rossi", BookingStatus.Confirmed);
        var (cancelled, cancelledSession) = world.Stay("Paola", "Verdi", BookingStatus.Cancelled);
        await world.Db.SaveChangesAsync();

        var emails = new List<(string? To, EmailContent Content, string Template)>();
        var queue = new Mock<IEmailQueue>();
        queue.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
            .Callback<string?, EmailContent, string>((to, content, template) => emails.Add((to, content, template)))
            .Returns(true);
        var pushes = new List<(string Key, PushAudience Audience, PushNotificationPayload Payload)>();
        var push = new Mock<IPushNotificationService>();
        push.Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()))
            .Callback<string, PushAudience, PushNotificationPayload>((key, audience, payload) => pushes.Add((key, audience, payload)))
            .Returns(true);
        var alerts = new VerificationOperatorAlerts(
            world.Db,
            new InternalActivityService(world.Db, NullLogger<InternalActivityService>.Instance, TimeProvider.System),
            push.Object,
            queue.Object,
            new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://site.example.test" })),
            NullLogger<VerificationOperatorAlerts>.Instance);

        await alerts.RaiseAsync(session.Id, VerificationAlertReasons.Review, CancellationToken.None);
        await alerts.RaiseAsync(session.Id, VerificationAlertReasons.GuestRequest, CancellationToken.None);

        // One "To do" for the booking: urgent, on the property, leading to the verification.
        var todo = await world.Db.InternalActivities.SingleAsync(a => a.EntityId == booking.Id);
        Assert.Equal(nameof(ActivityType.IdentityVerification), todo.ActivityType);
        Assert.Equal(ActivityAction.VerifyIdentity, todo.Action);
        Assert.Equal(ActivityUrgency.High, todo.Urgency);
        Assert.Equal(world.Property.Id, todo.PropertyId);
        Assert.Null(todo.CompletedAt);

        // The push: the property, to the operators of the booking, and no guest.
        var first = pushes[0];
        Assert.Equal(PushAudience.VerificationOperators(booking.Id), first.Audience);
        Assert.Equal(PushTypes.VerificationRequired, first.Payload.Type);
        Assert.Contains("Casa Aurora", first.Payload.Body, StringComparison.Ordinal);
        Assert.All(pushes, p => Assert.DoesNotContain("Rossi", p.Payload.Title + p.Payload.Body, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, pushes.Select(p => p.Key).Distinct().Count());

        // The e-mails: to the operators only (the owner, the manager, the creator of the property), the property and the reason, no guest.
        var firstCall = emails.Take(emails.Count / 2).ToList();
        Assert.Equal(
            new[] { world.EmailOf(owner), world.EmailOf(manager), world.EmailOf(Creator) }.Order(StringComparer.OrdinalIgnoreCase),
            firstCall.Select(e => e.To!).Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(emails, e => e.To == world.EmailOf(accountant) || e.To == world.EmailOf(collaborator));
        Assert.All(emails, e => Assert.Equal(EmailTemplates.Names.VerificationOperatorAlert, e.Template));
        var text = emails[0].Content.Subject + emails[0].Content.HtmlBody;
        Assert.Contains("Casa Aurora", text, StringComparison.Ordinal);
        Assert.Contains(VerificationAlertReasons.Review, text, StringComparison.Ordinal);
        Assert.All(emails, e =>
        {
            var content = e.Content.Subject + e.Content.HtmlBody;
            Assert.DoesNotContain("Mario", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Rossi", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mario.rossi@example.com", content, StringComparison.OrdinalIgnoreCase);
        });

        // Nothing is raised for a stay that is not active, for an unknown reason or for a session that does not exist.
        var before = (emails.Count, pushes.Count);
        await alerts.RaiseAsync(cancelledSession.Id, VerificationAlertReasons.Review, CancellationToken.None);
        await alerts.RaiseAsync(Guid.NewGuid(), VerificationAlertReasons.Review, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => alerts.RaiseAsync(session.Id, "not-a-reason", CancellationToken.None));
        Assert.Equal(before, (emails.Count, pushes.Count));
        Assert.False(await world.Db.InternalActivities.AnyAsync(a => a.EntityId == cancelled.Id));

        // The verification is over: the "To do" is closed by the system, once.
        await alerts.ResolveAsync(world.Org.Id, booking.Id, CancellationToken.None);
        await alerts.ResolveAsync(world.Org.Id, booking.Id, CancellationToken.None);
        var closed = await world.Db.InternalActivities.SingleAsync(a => a.EntityId == booking.Id);
        Assert.NotNull(closed.CompletedAt);
        Assert.Equal(InternalActivity.ClosedBySystem, closed.ClosedBy);
    }

    private static async Task<List<string>> ToldAsync(World world) =>
        (await VerificationOperatorAudience.UsersToTell(world.Db, world.Org.Id, world.Property.Id, Creator)
            .Select(u => u.Id)
            .ToListAsync())
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>One host org on the model seeds (the roles and their permissions as the migrations leave them), one property made by <see cref="Creator"/>.</summary>
    private sealed class World : IDisposable
    {
        private World(AppDbContext db, OrgEntity org, OrgEntity otherOrg, Property property)
        {
            Db = db;
            Org = org;
            OtherOrg = otherOrg;
            Property = property;
        }

        public AppDbContext Db { get; }

        public OrgEntity Org { get; }

        public OrgEntity OtherOrg { get; }

        public Property Property { get; }

        public static World Create()
        {
            var db = OrgTeamTestData.NewDb();
            var org = OrgTeamTestData.AddOrg(db);
            var other = OrgTeamTestData.AddOrg(db);
            var property = new Property
            {
                Id = Guid.NewGuid(),
                OrgId = org.Id,
                OwnerId = Creator,
                Name = "Casa Aurora",
                Address = "Via Test 1",
                City = "Roma",
                PostalCode = "00100",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 100m,
            };
            db.Properties.Add(property);
            db.SaveChanges();
            return new World(db, org, other, property);
        }

        /// <summary>A member of the team with the short-rent role its org role implies (the account roles do not matter here).</summary>
        public string Member(
            string id,
            OrgRole role,
            PropertyScope? scope = null,
            OrgMemberStatus status = OrgMemberStatus.Active,
            bool active = true,
            Guid? orgId = null)
        {
            var org = orgId ?? Org.Id;
            var user = OrgTeamTestData.AddUser(Db, id, org);
            user.IsActive = active;
            var member = new OrgMember { UserId = id, OrgId = org, Role = role, Status = status };
            if (scope is { } propertyScope)
                member.PropertyScope = propertyScope;

            Db.OrgMembers.Add(member);
            Db.UserContextMemberships.Add(new UserContextMembership
            {
                UserId = id,
                ContextKey = OrgRoleCatalog.ShortRent,
                RoleId = OrgTeamTestData.RoleId(Db, OrgRoleCatalog.ShortRent, OrgRoleCatalog.HostRoleKey(role, OrgRoleCatalog.ShortRent)),
            });
            return id;
        }

        public string EmailOf(string userId) => Db.Users.AsNoTracking().Single(u => u.Id == userId).Email;

        public (Booking Booking, GuestCheckInSession Session) Stay(string firstName, string lastName, BookingStatus status)
        {
            var guest = new Guest
            {
                Id = Guid.NewGuid(),
                OrgId = Org.Id,
                FirstName = firstName,
                LastName = lastName,
                Email = $"{firstName}.{lastName}@example.com".ToLowerInvariant(),
            };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = Org.Id,
                PropertyId = Property.Id,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 11, 12),
                CheckOutDate = new DateTime(2026, 11, 14),
                Status = status,
                Source = BookingSource.Direct,
                NumberOfGuests = 1,
                TotalPrice = 200m,
            };
            var session = new GuestCheckInSession
            {
                BookingId = booking.Id,
                OrgId = Org.Id,
                TokenHash = Guid.NewGuid().ToString("N"),
                ExpiresAt = DateTime.UtcNow.AddDays(10),
                Status = GuestCheckInSessionStatus.InCompilazione,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.GuestCheckInSessions.Add(session);
            return (booking, session);
        }

        public void Dispose() => Db.Dispose();
    }
}
