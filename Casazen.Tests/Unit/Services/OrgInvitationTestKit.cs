using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.OrgTeamTestData;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// What the tests of the org invitations (AM-02) share: the real services over the EF InMemory provider (one named
/// database per kit, a fresh context per call), a clock under the test's control, the email queue and Auth0 as doubles, and
/// the data an org team needs (an owner as the onboarding leaves it, members, invitees). The concurrency, the locks and the
/// unique indexes are proved on PostgreSQL by <c>OrgInvitationsPostgresTests</c> (CI).
/// </summary>
internal sealed class OrgInvitationTestKit
{
    public const string ConsentVersion = "2026-10-v1";

    public static readonly DateTimeOffset Start = new(2026, 10, 8, 10, 30, 0, TimeSpan.Zero);

    public string Database { get; } = Guid.NewGuid().ToString();

    public FakeTimeProvider Clock { get; } = new(Start);

    public Mock<IUserAuthorizationCache> Cache { get; } = new();

    public Mock<IAuth0ManagementService> Auth0 { get; } = new();

    public RecordingEmailQueue Emails { get; } = new();

    public Dictionary<string, string?> Settings { get; } = [];

    public bool OrgTeamFlag { get; set; } = true;

    /// <summary>When false the queue refuses every email (provider not configured, queue down).</summary>
    public bool QueueAccepts { get; set; } = true;

    public string? PublicSiteBaseUrl { get; set; } = EmailTestHelpers.PublicSiteBaseUrl;

    private readonly Func<AppDbContext>? _contextFactory;

    /// <param name="contextFactory">
    /// A new context on the database the tests use; omitted, the kit works on its own EF InMemory database. The PostgreSQL
    /// tests pass a factory over a migrated database and get the same services and the same data helpers.
    /// </param>
    public OrgInvitationTestKit(Func<AppDbContext>? contextFactory = null)
    {
        _contextFactory = contextFactory;
        Auth0
            .Setup(a => a.RemoveRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Synced);
    }

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public AppDbContext NewDb() => _contextFactory?.Invoke() ?? OrgTeamTestData.NewDb(Database);

    private IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(Settings).Build();

    private IEmailQueue Queue() => QueueAccepts ? Emails : new RefusingEmailQueue();

    public ILegalDocumentService Legal()
    {
        var legal = new Mock<ILegalDocumentService>();
        legal.Setup(l => l.GetTos()).Returns(new LegalDocumentMeta(ConsentVersion, null, "Terms", "Terms", null));
        legal.Setup(l => l.GetPrivacy()).Returns(new LegalDocumentMeta(ConsentVersion, null, "Privacy", "Privacy", null));
        legal.Setup(l => l.GetDpa()).Returns(new LegalDocumentMeta(ConsentVersion, null, "DPA", "DPA", null));
        legal.Setup(l => l.GetSubprocessors()).Returns(new SubprocessorsDocument(ConsentVersion, null, []));
        return legal.Object;
    }

    public static OnboardingConsentsInput Consents(string version = ConsentVersion, bool accepted = true) =>
        new(accepted, version, accepted, version, accepted, version, accepted, version);

    public EntitlementService Entitlement(AppDbContext db) => new(db, Config());

    public OrgSeatService Seats(AppDbContext db) => new(db, Entitlement(db), Clock);

    public OrgMembershipService Membership(AppDbContext db) =>
        new(db, Cache.Object, NullLogger<OrgMembershipService>.Instance, Clock);

    public OnboardingService Onboarding(AppDbContext db) =>
        new(db, Legal(), Cache.Object, EmailTestHelpers.Links(PublicSiteBaseUrl));

    public OrgInvitationService Invitations(AppDbContext db, IOrgMembershipService? membership = null) => new(
        db,
        Seats(db),
        membership ?? Membership(db),
        new OrgEmptinessChecker(db),
        Onboarding(db),
        Auth0.Object,
        Cache.Object,
        Queue(),
        EmailTestHelpers.Links(PublicSiteBaseUrl),
        NullLogger<OrgInvitationService>.Instance,
        Clock);

    public OrgTeamService Team(AppDbContext db) => new(
        db,
        Seats(db),
        Membership(db),
        Cache.Object,
        NullLogger<OrgTeamService>.Instance);

    public OrgInvitationMaintenanceService Maintenance(AppDbContext db)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OrgTeam)).Returns(OrgTeamFlag);
        return new OrgInvitationMaintenanceService(
            db,
            flags.Object,
            Queue(),
            EmailTestHelpers.Links(PublicSiteBaseUrl),
            Config(),
            NullLogger<OrgInvitationMaintenanceService>.Instance,
            Clock);
    }

    // ─── Data ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An owner as the onboarding leaves it: its org (named "Casa Rossi"), the account with the owner role, the rental
    /// membership, the org member row and the account membership, and the consents of the org. Pro with an active
    /// subscription by default (10 seats).
    /// </summary>
    public async Task<(OrgEntity Org, User Owner)> SeedOwnerOrgAsync(
        string ownerId = "auth0|owner",
        PlanTier tier = PlanTier.Pro,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        await using var db = NewDb();
        var org = AddOrg(db);
        org.Name = org.DisplayName = "Casa Rossi";
        org.PlanTier = tier;
        org.SubscriptionStatus = status;
        org.SubscriptionId = status == SubscriptionStatus.None ? null : $"sub_{Guid.NewGuid():N}";

        var owner = AddUser(db, ownerId, org.Id, UserRole.PropertyOwner);
        owner.Email = $"{ownerId.Replace("auth0|", string.Empty, StringComparison.Ordinal)}@example.com";
        owner.FirstName = "Giulia";
        owner.LastName = "Rinaldi";
        owner.RentalType = RentalType.ShortTerm;
        owner.OnboardingCompletedAt = Now;
        AddMembership(db, ownerId, "short-rent", "property_owner");
        AddConsents(db, ownerId, org.Id);
        await db.SaveChangesAsync();

        await Membership(db).EnsureOwnerAsync(ownerId, org.Id);
        return (org, owner);
    }

    /// <summary>A person with no org yet who just signed up (no role, not onboarded), with the given email.</summary>
    public async Task<User> SeedNewUserAsync(string userId, string email)
    {
        await using var db = NewDb();
        var user = AddUser(db, userId, orgId: null);
        user.Email = email;
        user.FirstName = "Anna";
        user.LastName = "Leone";
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// A person who signed up and onboarded on its own, as the product does: an auto-created org that is still empty and
    /// unbilled (Starter, nothing in it), the owner role, the rental membership and the consents of that org.
    /// </summary>
    public async Task<(OrgEntity Org, User User)> SeedPersonWithEmptyOrgAsync(string userId, string email)
    {
        await using var db = NewDb();
        var org = AddOrg(db);
        org.Name = org.DisplayName = "Anna Leone";
        org.PlanTier = PlanTier.Starter;
        org.ContactEmail = email;

        var user = AddUser(db, userId, org.Id, UserRole.PropertyOwner);
        user.Email = email;
        user.FirstName = "Anna";
        user.LastName = "Leone";
        user.RentalType = RentalType.ShortTerm;
        user.OnboardingCompletedAt = Now;
        user.LastUsedContextKey = "short-rent";
        AddMembership(db, userId, "short-rent", "property_owner");
        AddConsents(db, userId, org.Id);
        await db.SaveChangesAsync();

        await Membership(db).EnsureOwnerAsync(userId, org.Id);
        return (org, user);
    }

    /// <summary>A member added through the real service, with its account (name and email) and consents.</summary>
    public async Task<(User User, OrgMember Member)> SeedMemberAsync(
        Guid orgId,
        string userId,
        OrgRole role,
        string[]? areas = null,
        OrgMemberStatus status = OrgMemberStatus.Active,
        string? email = null)
    {
        await using var db = NewDb();
        var user = AddUser(db, userId, orgId: null);
        user.Email = email ?? $"{userId.Replace("auth0|", string.Empty, StringComparison.Ordinal)}@example.com";
        user.FirstName = "Membro";
        user.LastName = userId.Replace("auth0|", string.Empty, StringComparison.Ordinal);
        user.OnboardingCompletedAt = Now;
        await db.SaveChangesAsync();

        var member = await Membership(db).AddMemberAsync(userId, orgId, role, areas ?? ["short-rent"], createdByUserId: null);
        if (status == OrgMemberStatus.Deactivated)
            member = await Membership(db).DeactivateAsync(userId);

        return (user, member);
    }

    /// <summary>A pending invitation written directly (the service's own rows are produced by <see cref="OrgInvitationService"/>).</summary>
    public async Task<(OrgInvitation Invitation, string Token)> SeedInvitationAsync(
        Guid orgId,
        string email,
        OrgRole role = OrgRole.Collaborator,
        string invitedBy = "auth0|owner",
        OrgInvitationStatus status = OrgInvitationStatus.Pending,
        DateTime? expiresAt = null,
        DateTime? closedAt = null,
        string[]? areas = null,
        string language = "it",
        string name = "Anna Leone")
    {
        var token = Casazen.Core.OrgTeam.OrgInvitationTokens.Generate();
        var invitation = new OrgInvitation
        {
            OrgId = orgId,
            Email = email,
            Name = name,
            Role = role,
            Areas = (areas ?? ["short-rent"]).ToList(),
            TokenHash = Casazen.Core.OrgTeam.OrgInvitationTokens.Hash(token),
            Status = status,
            ExpiresAt = expiresAt ?? Now.AddDays(7),
            InvitedByUserId = invitedBy,
            Language = language,
            ClosedAt = closedAt,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        await using var db = NewDb();
        db.OrgInvitations.Add(invitation);
        await db.SaveChangesAsync();
        return (invitation, token);
    }

    private void AddConsents(AppDbContext db, string userId, Guid orgId)
    {
        foreach (var type in new[] { ConsentType.Tos, ConsentType.Privacy, ConsentType.Dpa, ConsentType.SubprocessorsAck })
            db.ConsentRecords.Add(new ConsentRecord { UserId = userId, OrgId = orgId, Type = type, Version = ConsentVersion });
    }

    // ─── Reading back ───────────────────────────────────────────────────────────────────────────────────

    public async Task<OrgInvitation> ReadInvitationAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == id);
    }

    public async Task<User> ReadUserAsync(string userId)
    {
        await using var db = NewDb();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    public async Task<OrgMember?> ReadMemberAsync(string userId)
    {
        await using var db = NewDb();
        return await db.OrgMembers.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId);
    }

    public async Task<List<string>> MembershipsAsync(string userId)
    {
        await using var db = NewDb();
        return await MembershipsOfAsync(db, userId);
    }

    /// <summary>The secret token inside the link of a queued email: what the person opens.</summary>
    public static string TokenInEmail((string? To, EmailContent Content, string Template) email)
    {
        var match = Regex.Match(email.Content.HtmlBody, "/invite/accept\\?token=([0-9a-f]{64})");
        Assert.True(match.Success, "The email carries no invitation link.");
        return match.Groups[1].Value;
    }

    /// <summary>Everything an <see cref="IEmailQueue"/> refuses (provider not configured, queue down).</summary>
    private sealed class RefusingEmailQueue : IEmailQueue
    {
        public bool Enqueue(string? to, EmailContent content, string template) => false;
    }
}
