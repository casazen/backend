using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02: creating, listing, sending again, revoking and copying the link of an invitation, and what the anonymous lookup
/// tells. The real services over EF InMemory; the last seat given to one request only, and the unique indexes, are proved
/// on PostgreSQL by <c>OrgInvitationsPostgresTests</c> (CI).
/// </summary>
public class OrgInvitationServiceTests
{
    private readonly OrgInvitationTestKit _kit = new();

    private static CreateOrgInvitation Request(
        Guid orgId,
        string actor = "auth0|owner",
        string email = "anna.leone@example.com",
        OrgRole role = OrgRole.Collaborator,
        string[]? areas = null,
        PropertyScope scope = PropertyScope.All,
        string? language = null,
        string name = "Anna Leone") =>
        new(orgId, actor, email, name, role, areas ?? ["short-rent"], scope, language);

    private async Task<OrgInvitationSent> CreateAsync(CreateOrgInvitation request)
    {
        await using var db = _kit.NewDb();
        return await _kit.Invitations(db).CreateAsync(request);
    }

    // ─── Create ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_Collaborator_StoresOnlyTheHashOfTheTokenAndQueuesTheEmailWithTheLink()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var sent = await CreateAsync(Request(org.Id, scope: PropertyScope.Selected));

        var stored = await _kit.ReadInvitationAsync(sent.Invitation.Id);
        var email = Assert.Single(_kit.Emails.Snapshot());
        var token = OrgInvitationTestKit.TokenInEmail(email);
        Assert.Equal(OrgInvitationTokens.Hash(token), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal(64, token.Length);
        Assert.Equal("anna.leone@example.com", email.To);
        Assert.Equal(EmailTemplates.Names.OrgInvitation, email.Template);
        Assert.True(sent.EmailQueued);

        Assert.Equal((org.Id, "anna.leone@example.com", "Anna Leone"), (stored.OrgId, stored.Email, stored.Name));
        Assert.Equal((OrgRole.Collaborator, PropertyScope.Selected, OrgInvitationStatus.Pending), (stored.Role, stored.PropertyScope, stored.Status));
        Assert.Equal(["short-rent"], stored.Areas);
        Assert.Equal("auth0|owner", stored.InvitedByUserId);
        Assert.Equal(_kit.Now, stored.CreatedAt);
        Assert.Equal(_kit.Now.AddDays(7), stored.ExpiresAt);
        Assert.Null(stored.ReminderSentAt);
        Assert.Null(stored.AcceptedAt);
        Assert.Null(stored.ClosedAt);
    }

    [Fact]
    public async Task CreateAsync_TheTokenIsNowhereInTheDatabase()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var sent = await CreateAsync(Request(org.Id));

        var token = OrgInvitationTestKit.TokenInEmail(Assert.Single(_kit.Emails.Snapshot()));
        await using var db = _kit.NewDb();
        var rows = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.All(rows, row => Assert.DoesNotContain(token, string.Join('|', row.Email, row.Name, row.TokenHash, row.InvitedByUserId, row.Language)));
        Assert.Equal(sent.Invitation.Id, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task CreateAsync_EmailWithSpacesAndCapitals_IsStoredNormalized()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var sent = await CreateAsync(Request(org.Id, email: "  Anna.LEONE@Example.IT "));

        Assert.Equal("anna.leone@example.it", sent.Invitation.Email);
        Assert.Equal("anna.leone@example.it", Assert.Single(_kit.Emails.Snapshot()).To);
    }

    [Fact]
    public async Task CreateAsync_AreasAreCleanedUp_KeepingTheValidOnesOnce()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var sent = await CreateAsync(Request(org.Id, areas: [" Short-Rent ", "long-rent", "SHORT-RENT", "account", "admin", "x"]));

        Assert.Equal(["short-rent", "long-rent"], sent.Invitation.Areas);
    }

    [Theory]
    [InlineData("en", "invites you to work with", "en")]
    [InlineData("EN", "invites you to work with", "en")]
    [InlineData("it", "ti invita a lavorare con", "it")]
    [InlineData("fr", "ti invita a lavorare con", "it")]
    [InlineData(null, "ti invita a lavorare con", "it")]
    public async Task CreateAsync_Language_DecidesTheLanguageOfTheEmailAndIsRemembered(
        string? language,
        string subjectPart,
        string expectedLanguage)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var sent = await CreateAsync(Request(org.Id, language: language));

        var email = Assert.Single(_kit.Emails.Snapshot());
        Assert.Contains(subjectPart, email.Content.Subject);
        Assert.Contains($"<html lang=\"{expectedLanguage}\">", email.Content.HtmlBody);
        Assert.Equal(expectedLanguage, (await _kit.ReadInvitationAsync(sent.Invitation.Id)).Language);
    }

    [Fact]
    public async Task CreateAsync_OwnerInvitesAnAdmin_Works()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var sent = await CreateAsync(Request(org.Id, role: OrgRole.Admin));

        Assert.Equal(OrgRole.Admin, sent.Invitation.Role);
    }

    [Fact]
    public async Task CreateAsync_AdminInvitesAnAdmin_IsForbiddenBecauseOnlyTheOwnerCreatesAdministrators()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin", OrgRole.Admin);

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => CreateAsync(Request(org.Id, actor: "auth0|admin", role: OrgRole.Admin)));

        Assert.Equal(OrgInvitationErrors.OwnerRequired, error.Code);
        Assert.Empty(await ListIdsAsync(org.Id));
        Assert.Empty(_kit.Emails.Snapshot());
    }

    [Theory]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Collaborator)]
    [InlineData(OrgRole.Accountant)]
    public async Task CreateAsync_AdminInvitesTheOtherRoles_Works(OrgRole role)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin", OrgRole.Admin);

        var sent = await CreateAsync(Request(org.Id, actor: "auth0|admin", role: role));

        Assert.Equal(role, sent.Invitation.Role);
        Assert.Equal("auth0|admin", sent.Invitation.InvitedByUserId);
    }

    [Fact]
    public async Task CreateAsync_Owner_IsNotAssignableToAnybody()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => CreateAsync(Request(org.Id, role: OrgRole.Owner)));

        Assert.Equal(OrgMembershipErrors.OwnerNotAssignable, error.Code);
    }

    public static TheoryData<string[]> NoValidAreas => new()
    {
        Array.Empty<string>(),
        new[] { "account" },
        new[] { "admin", "supplier", " " },
    };

    [Theory]
    [MemberData(nameof(NoValidAreas))]
    public async Task CreateAsync_NoValidArea_Is422(string[] areas)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => CreateAsync(Request(org.Id, areas: areas)));

        Assert.Equal(OrgMembershipErrors.AreaRequired, error.Code);
    }

    [Theory]
    [InlineData(OrgRole.Collaborator)]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Accountant)]
    public async Task CreateAsync_ByAPersonWhoDoesNotManageThePeople_IsRefused(OrgRole actorRole)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|member", actorRole);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateAsync(Request(org.Id, actor: "auth0|member")));

        Assert.Empty(await ListIdsAsync(org.Id));
    }

    [Fact]
    public async Task CreateAsync_ByADeactivatedAdmin_IsRefused()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin", OrgRole.Admin, status: OrgMemberStatus.Deactivated);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateAsync(Request(org.Id, actor: "auth0|admin")));
    }

    [Fact]
    public async Task CreateAsync_ByTheOwnerOfAnotherOrg_IsRefused()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedOwnerOrgAsync("auth0|other-owner");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateAsync(Request(org.Id, actor: "auth0|other-owner")));
    }

    [Fact]
    public async Task CreateAsync_PendingInvitationOfTheSameEmail_IsAConflict()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await CreateAsync(Request(org.Id));

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => CreateAsync(Request(org.Id, email: "ANNA.LEONE@example.com")));

        Assert.Equal(OrgInvitationErrors.AlreadyPending, error.Code);
        Assert.Single(await ListIdsAsync(org.Id));
    }

    [Fact]
    public async Task CreateAsync_SameEmailPendingInAnotherOrg_IsFine()
    {
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await CreateAsync(Request(orgA.Id, actor: "auth0|owner-a"));

        var sent = await CreateAsync(Request(orgB.Id, actor: "auth0|owner-b"));

        Assert.Equal(OrgInvitationStatus.Pending, sent.Invitation.Status);
    }

    [Fact]
    public async Task CreateAsync_PendingInvitationPastItsExpiry_IsClosedAndReplacedByTheNewOne()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (old, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", expiresAt: _kit.Now.AddHours(-1));

        var sent = await CreateAsync(Request(org.Id));

        var closed = await _kit.ReadInvitationAsync(old.Id);
        Assert.Equal(OrgInvitationStatus.Expired, closed.Status);
        Assert.Equal(old.ExpiresAt, closed.ClosedAt);
        Assert.NotEqual(old.Id, sent.Invitation.Id);
        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(sent.Invitation.Id)).Status);
    }

    [Theory]
    [InlineData(OrgMemberStatus.Active)]
    [InlineData(OrgMemberStatus.Deactivated)]
    public async Task CreateAsync_EmailOfAMemberOfTheOrg_IsAConflict(OrgMemberStatus status)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|anna", OrgRole.Collaborator, status: status, email: "Anna.Leone@Example.com");

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => CreateAsync(Request(org.Id)));

        Assert.Equal(OrgMembershipErrors.AlreadyMember, error.Code);
        Assert.Empty(await ListIdsAsync(org.Id));
    }

    [Fact]
    public async Task CreateAsync_TheOwnersOwnEmail_IsAConflict()
    {
        var (org, owner) = await _kit.SeedOwnerOrgAsync();

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => CreateAsync(Request(org.Id, email: owner.Email)));

        Assert.Equal(OrgMembershipErrors.AlreadyMember, error.Code);
    }

    [Fact]
    public async Task CreateAsync_EmailOfAPersonOfAnotherOrg_IsAllowed_TheAcceptanceDecides()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedOwnerOrgAsync("auth0|elsewhere");
        var email = (await _kit.ReadUserAsync("auth0|elsewhere")).Email;

        var sent = await CreateAsync(Request(org.Id, email: email));

        Assert.Equal(email, sent.Invitation.Email);
    }

    [Fact]
    public async Task CreateAsync_PublicSiteNotConfigured_FailsBeforeSavingAnything()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        _kit.PublicSiteBaseUrl = null;

        await Assert.ThrowsAsync<EmailConfigurationException>(() => CreateAsync(Request(org.Id)));

        Assert.Empty(await ListIdsAsync(org.Id));
        Assert.Empty(_kit.Emails.Snapshot());
    }

    [Fact]
    public async Task CreateAsync_EmailCannotBeQueued_TheInvitationExistsAndTheResultSaysSo()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        _kit.QueueAccepts = false;

        var sent = await CreateAsync(Request(org.Id));

        Assert.False(sent.EmailQueued);
        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(sent.Invitation.Id)).Status);
    }

    [Fact]
    public async Task CreateAsync_TheEmailNamesTheInviterTheOrgTheRoleAndTheAreas()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        await CreateAsync(Request(org.Id, role: OrgRole.Accountant, areas: ["short-rent", "long-rent"]));

        var body = Assert.Single(_kit.Emails.Snapshot()).Content.HtmlBody;
        Assert.Contains("Giulia Rinaldi", body);
        Assert.Contains("Casa Rossi", body);
        Assert.Contains("Contabile", body);
        Assert.Contains("Affitti brevi, Affitti lunghi", body);
        Assert.Contains("anna.leone@example.com", body);
    }

    // ─── Seats ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_StarterHasTwoSeats_TheOwnerAndOneInvitation()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);

        await CreateAsync(Request(org.Id, email: "uno@example.com"));
        var error = await Assert.ThrowsAsync<DomainConflictException>(() => CreateAsync(Request(org.Id, email: "due@example.com")));

        Assert.Equal(OrgSeatErrors.LimitReached, error.Code);
        Assert.Single(await ListIdsAsync(org.Id));
    }

    [Fact]
    public async Task CreateAsync_PendingInvitationsAndActiveMembersTogetherFillTheSeats()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro);
        for (var i = 0; i < 6; i++)
            await _kit.SeedMemberAsync(org.Id, $"auth0|m{i}", OrgRole.Collaborator);
        for (var i = 0; i < 3; i++)
            await CreateAsync(Request(org.Id, email: $"inv{i}@example.com"));

        // 1 owner + 6 members + 3 pending = 10 of 10.
        var error = await Assert.ThrowsAsync<DomainConflictException>(() => CreateAsync(Request(org.Id, email: "last@example.com")));

        Assert.Equal(OrgSeatErrors.LimitReached, error.Code);
    }

    [Fact]
    public async Task CreateAsync_TheLastSeat_IsGiven()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro);
        for (var i = 0; i < 8; i++)
            await _kit.SeedMemberAsync(org.Id, $"auth0|m{i}", OrgRole.Collaborator);

        var sent = await CreateAsync(Request(org.Id));

        Assert.Equal(OrgInvitationStatus.Pending, sent.Invitation.Status);
        await using var db = _kit.NewDb();
        Assert.Equal(10, (await _kit.Seats(db).GetUsageAsync(org.Id)).Used);
    }

    [Fact]
    public async Task CreateAsync_ExpiredInvitationsHoldNoSeat()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        await _kit.SeedInvitationAsync(org.Id, "old@example.com", expiresAt: _kit.Now.AddMinutes(-1));
        await _kit.SeedInvitationAsync(org.Id, "marked@example.com", status: OrgInvitationStatus.Expired, closedAt: _kit.Now.AddDays(-1));

        var sent = await CreateAsync(Request(org.Id));

        Assert.Equal(OrgInvitationStatus.Pending, sent.Invitation.Status);
    }

    [Fact]
    public async Task CreateAsync_RevokedAndAcceptedInvitationsHoldNoSeat_ButTheAcceptedMemberDoes()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        await _kit.SeedInvitationAsync(org.Id, "revoked@example.com", status: OrgInvitationStatus.Revoked, closedAt: _kit.Now);

        await CreateAsync(Request(org.Id, email: "uno@example.com"));
        var error = await Assert.ThrowsAsync<DomainConflictException>(() => CreateAsync(Request(org.Id, email: "due@example.com")));

        Assert.Equal(OrgSeatErrors.LimitReached, error.Code);
    }

    [Fact]
    public async Task CreateAsync_ADeactivatedMemberHoldsNoSeat()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        await _kit.SeedMemberAsync(org.Id, "auth0|gone", OrgRole.Collaborator, status: OrgMemberStatus.Deactivated);

        var sent = await CreateAsync(Request(org.Id));

        Assert.Equal(OrgInvitationStatus.Pending, sent.Invitation.Status);
    }

    [Fact]
    public async Task CreateAsync_TheAccountantCountsLikeEveryoneElse()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        await _kit.SeedMemberAsync(org.Id, "auth0|accountant", OrgRole.Accountant);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => CreateAsync(Request(org.Id)));

        Assert.Equal(OrgSeatErrors.LimitReached, error.Code);
    }

    [Fact]
    public async Task CreateAsync_ProWithASubscriptionNotInGoodStanding_FallsBackToTheStarterSeats_MembersStay()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro, status: SubscriptionStatus.Unpaid);
        await _kit.SeedMemberAsync(org.Id, "auth0|m1", OrgRole.Collaborator);
        await _kit.SeedMemberAsync(org.Id, "auth0|m2", OrgRole.Collaborator);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => CreateAsync(Request(org.Id)));

        // 3 members above the 2 seats of Starter: they stay, nothing new comes in.
        Assert.Equal(OrgSeatErrors.LimitReached, error.Code);
        await using var db = _kit.NewDb();
        var usage = await _kit.Seats(db).GetUsageAsync(org.Id);
        Assert.Equal((2, 3), (usage.Max, usage.Used));
        Assert.Equal(0, usage.Available);
    }

    [Fact]
    public async Task CreateAsync_Scale_HasNoLimit()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Scale);
        for (var i = 0; i < 12; i++)
            await CreateAsync(Request(org.Id, email: $"p{i}@example.com"));

        await using var db = _kit.NewDb();
        var usage = await _kit.Seats(db).GetUsageAsync(org.Id);
        Assert.True(usage.IsUnlimited);
        Assert.Equal(12, usage.PendingInvitations);
    }

    [Fact]
    public async Task CreateAsync_ConfiguredSeatsOfTheTier_ReplaceTheDefault()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro);
        _kit.Settings["Entitlement:Tiers:Pro:MaxSeats"] = "2";
        await CreateAsync(Request(org.Id, email: "uno@example.com"));

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => CreateAsync(Request(org.Id, email: "due@example.com")));

        // The owner and one invitation fill the two seats the configuration allows a Pro org.
        Assert.Equal(OrgSeatErrors.LimitReached, error.Code);
    }

    // ─── List ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ReturnsTheOpenAndTheExpiredOnesNewestFirst_NotTheAcceptedNorTheRevoked()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (older, _) = await _kit.SeedInvitationAsync(org.Id, "older@example.com");
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        var (newer, _) = await _kit.SeedInvitationAsync(org.Id, "newer@example.com");
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        var (overdue, _) = await _kit.SeedInvitationAsync(org.Id, "overdue@example.com", expiresAt: _kit.Now.AddHours(-2));
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        var (marked, _) = await _kit.SeedInvitationAsync(org.Id, "marked@example.com", status: OrgInvitationStatus.Expired, closedAt: _kit.Now);
        await _kit.SeedInvitationAsync(org.Id, "accepted@example.com", status: OrgInvitationStatus.Accepted, closedAt: _kit.Now);
        await _kit.SeedInvitationAsync(org.Id, "revoked@example.com", status: OrgInvitationStatus.Revoked, closedAt: _kit.Now);

        await using var db = _kit.NewDb();
        var items = await _kit.Invitations(db).ListAsync(org.Id);

        // The accepted and the revoked ones are history (and deleted after the retention); the others, newest first.
        Assert.Equal([marked.Id, overdue.Id, newer.Id, older.Id], items.Select(i => i.Id));
        Assert.Equal(OrgInvitationStatus.Pending, items.Single(i => i.Id == newer.Id).Status);
        Assert.Equal(OrgInvitationStatus.Pending, items.Single(i => i.Id == older.Id).Status);
        // Past its expiry it is expired for everyone, before the job marks it.
        Assert.Equal(OrgInvitationStatus.Expired, items.Single(i => i.Id == overdue.Id).Status);
        Assert.Equal(OrgInvitationStatus.Expired, items.Single(i => i.Id == marked.Id).Status);
    }

    [Fact]
    public async Task ListAsync_NeverShowsTheInvitationsOfAnotherOrg()
    {
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await _kit.SeedInvitationAsync(orgA.Id, "a@example.com");
        await _kit.SeedInvitationAsync(orgB.Id, "b@example.com");

        await using var db = _kit.NewDb();
        var items = await _kit.Invitations(db).ListAsync(orgA.Id);

        Assert.Equal("a@example.com", Assert.Single(items).Email);
    }

    // ─── Resend ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResendAsync_RotatesTheTokenResetsTheExpiryAndTheReminderAndQueuesTheEmail()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var created = await CreateAsync(Request(org.Id));
        var firstToken = OrgInvitationTestKit.TokenInEmail(Assert.Single(_kit.Emails.Snapshot()));
        _kit.Clock.Advance(TimeSpan.FromDays(4));
        await using (var db = _kit.NewDb())
        {
            var row = await db.OrgInvitations.IgnoreQueryFilters().SingleAsync();
            row.ReminderSentAt = _kit.Now;
            await db.SaveChangesAsync();
        }

        OrgInvitationSent sent;
        await using (var db = _kit.NewDb())
            sent = await _kit.Invitations(db).ResendAsync(org.Id, created.Invitation.Id, "auth0|owner");

        var emails = _kit.Emails.Snapshot();
        Assert.Equal(2, emails.Count);
        var secondToken = OrgInvitationTestKit.TokenInEmail(emails[1]);
        Assert.NotEqual(firstToken, secondToken);
        var stored = await _kit.ReadInvitationAsync(created.Invitation.Id);
        Assert.Equal(OrgInvitationTokens.Hash(secondToken), stored.TokenHash);
        Assert.NotEqual(OrgInvitationTokens.Hash(firstToken), stored.TokenHash);
        Assert.Equal(_kit.Now.AddDays(7), stored.ExpiresAt);
        Assert.Null(stored.ReminderSentAt);
        Assert.Equal(OrgInvitationStatus.Pending, stored.Status);
        Assert.True(sent.EmailQueued);

        // The link of the first email no longer opens anything.
        await using var lookupDb = _kit.NewDb();
        Assert.Null(await _kit.Invitations(lookupDb).LookupAsync(firstToken));
        Assert.NotNull(await _kit.Invitations(lookupDb).LookupAsync(secondToken));
    }

    [Fact]
    public async Task ResendAsync_AnExpiredInvitation_ComesBackAndTakesASeatAgain()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        var (expired, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", expiresAt: _kit.Now.AddDays(-1));

        await using var db = _kit.NewDb();
        var sent = await _kit.Invitations(db).ResendAsync(org.Id, expired.Id, "auth0|owner");

        Assert.Equal(OrgInvitationStatus.Pending, sent.Invitation.Status);
        var stored = await _kit.ReadInvitationAsync(expired.Id);
        Assert.Equal((OrgInvitationStatus.Pending, _kit.Now.AddDays(7)), (stored.Status, stored.ExpiresAt));
        Assert.Null(stored.ClosedAt);
    }

    [Fact]
    public async Task ResendAsync_AnExpiredInvitationWithTheSeatsFull_IsAConflict()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        var (expired, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: OrgInvitationStatus.Expired, closedAt: _kit.Now.AddDays(-1), expiresAt: _kit.Now.AddDays(-1));
        await _kit.SeedInvitationAsync(org.Id, "someone.else@example.com");

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _kit.Invitations(db).ResendAsync(org.Id, expired.Id, "auth0|owner"));

        Assert.Equal(OrgSeatErrors.LimitReached, error.Code);
        Assert.Equal(OrgInvitationStatus.Expired, (await _kit.ReadInvitationAsync(expired.Id)).Status);
    }

    [Fact]
    public async Task ResendAsync_APendingInvitationHoldsItsSeatAlready_SoItNeverNeedsANewOne()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        var created = await CreateAsync(Request(org.Id));

        await using var db = _kit.NewDb();
        var sent = await _kit.Invitations(db).ResendAsync(org.Id, created.Invitation.Id, "auth0|owner");

        Assert.Equal(OrgInvitationStatus.Pending, sent.Invitation.Status);
    }

    [Fact]
    public async Task ResendAsync_AnExpiredOneWhenANewerPendingExistsForTheSameEmail_IsAConflict()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (expired, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: OrgInvitationStatus.Expired, closedAt: _kit.Now.AddDays(-9), expiresAt: _kit.Now.AddDays(-9));
        await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com");

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _kit.Invitations(db).ResendAsync(org.Id, expired.Id, "auth0|owner"));

        Assert.Equal(OrgInvitationErrors.AlreadyPending, error.Code);
    }

    [Fact]
    public async Task ResendAsync_AnExpiredOneWhoseEmailHasJoinedMeanwhile_IsAConflict()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (expired, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: OrgInvitationStatus.Expired, closedAt: _kit.Now.AddDays(-9), expiresAt: _kit.Now.AddDays(-9));
        await _kit.SeedMemberAsync(org.Id, "auth0|anna", OrgRole.Collaborator, email: "anna.leone@example.com");

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _kit.Invitations(db).ResendAsync(org.Id, expired.Id, "auth0|owner"));

        Assert.Equal(OrgMembershipErrors.AlreadyMember, error.Code);
    }

    [Theory]
    [InlineData(OrgInvitationStatus.Accepted)]
    [InlineData(OrgInvitationStatus.Revoked)]
    public async Task ResendAsync_AnAcceptedOrRevokedInvitation_IsNotPending(OrgInvitationStatus status)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (closed, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: status, closedAt: _kit.Now);

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _kit.Invitations(db).ResendAsync(org.Id, closed.Id, "auth0|owner"));

        Assert.Equal(OrgInvitationErrors.NotPending, error.Code);
        Assert.Empty(_kit.Emails.Snapshot());
    }

    [Fact]
    public async Task ResendAsync_AnInvitationOfAnotherOrg_IsNotFound()
    {
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        var (foreign, _) = await _kit.SeedInvitationAsync(orgB.Id, "anna.leone@example.com", invitedBy: "auth0|owner-b");

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => _kit.Invitations(db).ResendAsync(orgA.Id, foreign.Id, "auth0|owner-a"));

        Assert.Equal(OrgInvitationErrors.NotFound, error.Code);
    }

    [Fact]
    public async Task ResendAsync_ByAPersonWhoDoesNotManageThePeople_IsRefused()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|member", OrgRole.Collaborator);
        var (invitation, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com");

        await using var db = _kit.NewDb();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _kit.Invitations(db).ResendAsync(org.Id, invitation.Id, "auth0|member"));
    }

    // ─── Revoke ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeAsync_ClosesTheInvitationAndFreesTheSeat()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        var created = await CreateAsync(Request(org.Id));
        var token = OrgInvitationTestKit.TokenInEmail(Assert.Single(_kit.Emails.Snapshot()));
        await Assert.ThrowsAsync<DomainConflictException>(() => CreateAsync(Request(org.Id, email: "other@example.com")));

        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).RevokeAsync(org.Id, created.Invitation.Id, "auth0|owner");

        var stored = await _kit.ReadInvitationAsync(created.Invitation.Id);
        Assert.Equal(OrgInvitationStatus.Revoked, stored.Status);
        Assert.Equal(_kit.Now, stored.ClosedAt);
        await using var lookupDb = _kit.NewDb();
        Assert.Null(await _kit.Invitations(lookupDb).LookupAsync(token));
        // The seat is free again.
        var again = await CreateAsync(Request(org.Id, email: "other@example.com"));
        Assert.Equal(OrgInvitationStatus.Pending, again.Invitation.Status);
    }

    [Fact]
    public async Task RevokeAsync_Twice_IsIdempotent()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var created = await CreateAsync(Request(org.Id));
        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).RevokeAsync(org.Id, created.Invitation.Id, "auth0|owner");
        _kit.Clock.Advance(TimeSpan.FromHours(3));

        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).RevokeAsync(org.Id, created.Invitation.Id, "auth0|owner");

        var stored = await _kit.ReadInvitationAsync(created.Invitation.Id);
        Assert.Equal(OrgInvitationStatus.Revoked, stored.Status);
        Assert.Equal(_kit.Now.AddHours(-3), stored.ClosedAt);
    }

    [Fact]
    public async Task RevokeAsync_AnExpiredInvitation_IsClosedAsRevokedKeepingItsClosingDate()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var closedAt = _kit.Now.AddDays(-2);
        var (expired, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: OrgInvitationStatus.Expired, closedAt: closedAt, expiresAt: closedAt);

        await using var db = _kit.NewDb();
        await _kit.Invitations(db).RevokeAsync(org.Id, expired.Id, "auth0|owner");

        var stored = await _kit.ReadInvitationAsync(expired.Id);
        Assert.Equal((OrgInvitationStatus.Revoked, closedAt), (stored.Status, stored.ClosedAt));
    }

    [Fact]
    public async Task RevokeAsync_AnAcceptedInvitation_IsNotPending()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (accepted, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: OrgInvitationStatus.Accepted, closedAt: _kit.Now);

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _kit.Invitations(db).RevokeAsync(org.Id, accepted.Id, "auth0|owner"));

        Assert.Equal(OrgInvitationErrors.NotPending, error.Code);
    }

    [Fact]
    public async Task RevokeAsync_InvitationOfAnotherOrgOrByAnOutsider_IsRefused()
    {
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        var (foreign, _) = await _kit.SeedInvitationAsync(orgB.Id, "anna.leone@example.com", invitedBy: "auth0|owner-b");

        await using (var db = _kit.NewDb())
            await Assert.ThrowsAsync<NotFoundException>(() => _kit.Invitations(db).RevokeAsync(orgA.Id, foreign.Id, "auth0|owner-a"));
        await using (var db = _kit.NewDb())
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _kit.Invitations(db).RevokeAsync(orgB.Id, foreign.Id, "auth0|owner-a"));

        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(foreign.Id)).Status);
    }

    // ─── Copy link ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyLinkAsync_RotatesTheTokenWithoutTouchingExpiryOrReminderAndSendsNothing()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var created = await CreateAsync(Request(org.Id));
        var firstToken = OrgInvitationTestKit.TokenInEmail(Assert.Single(_kit.Emails.Snapshot()));
        var before = await _kit.ReadInvitationAsync(created.Invitation.Id);
        _kit.Clock.Advance(TimeSpan.FromDays(1));

        OrgInvitationLink link;
        await using (var db = _kit.NewDb())
            link = await _kit.Invitations(db).CopyLinkAsync(org.Id, created.Invitation.Id, "auth0|owner");

        Assert.StartsWith($"{EmailTestHelpers.PublicSiteBaseUrl}/invite/accept?token=", link.Url);
        var secondToken = link.Url[(link.Url.IndexOf("token=", StringComparison.Ordinal) + 6)..];
        var after = await _kit.ReadInvitationAsync(created.Invitation.Id);
        Assert.Equal(OrgInvitationTokens.Hash(secondToken), after.TokenHash);
        Assert.NotEqual(before.TokenHash, after.TokenHash);
        Assert.Equal(before.ExpiresAt, after.ExpiresAt);
        Assert.Equal(before.ReminderSentAt, after.ReminderSentAt);
        Assert.Equal(before.ExpiresAt, link.ExpiresAt);
        Assert.Single(_kit.Emails.Snapshot());

        // The first link, the one of the email, is dead; the copied one works.
        await using var lookupDb = _kit.NewDb();
        Assert.Null(await _kit.Invitations(lookupDb).LookupAsync(firstToken));
        Assert.NotNull(await _kit.Invitations(lookupDb).LookupAsync(secondToken));
    }

    [Theory]
    [InlineData(OrgInvitationStatus.Accepted)]
    [InlineData(OrgInvitationStatus.Revoked)]
    [InlineData(OrgInvitationStatus.Expired)]
    public async Task CopyLinkAsync_ANotPendingInvitation_IsRefused(OrgInvitationStatus status)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (closed, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: status, closedAt: _kit.Now);

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _kit.Invitations(db).CopyLinkAsync(org.Id, closed.Id, "auth0|owner"));

        Assert.Equal(OrgInvitationErrors.NotPending, error.Code);
    }

    [Fact]
    public async Task CopyLinkAsync_APendingInvitationPastItsExpiry_IsRefused()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (overdue, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", expiresAt: _kit.Now.AddMinutes(-1));

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _kit.Invitations(db).CopyLinkAsync(org.Id, overdue.Id, "auth0|owner"));

        Assert.Equal(OrgInvitationErrors.NotPending, error.Code);
    }

    // ─── An administrator does not touch an administrator (decision D15) ────────────────────────────────

    [Theory]
    [InlineData(OrgInvitationStatus.Pending)]
    [InlineData(OrgInvitationStatus.Expired)]
    public async Task ResendAsync_AnAdministratorInvitation_IsRefusedToAnAdministrator(OrgInvitationStatus status)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin", OrgRole.Admin);
        var closed = status == OrgInvitationStatus.Expired ? _kit.Now : (DateTime?)null;
        var (invitation, _) = await _kit.SeedInvitationAsync(
            org.Id, "anna.leone@example.com", OrgRole.Admin, status: status, expiresAt: closed, closedAt: closed);
        var before = await _kit.ReadInvitationAsync(invitation.Id);

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => _kit.Invitations(db).ResendAsync(org.Id, invitation.Id, "auth0|admin"));

        Assert.Equal(OrgInvitationErrors.OwnerRequired, error.Code);
        var after = await _kit.ReadInvitationAsync(invitation.Id);
        Assert.Equal((before.Status, before.TokenHash, before.ExpiresAt), (after.Status, after.TokenHash, after.ExpiresAt));
        Assert.Empty(_kit.Emails.Snapshot());
    }

    [Fact]
    public async Task ResendAsync_TheOwnerMayResendAnAdministratorInvitation()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (invitation, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", OrgRole.Admin);

        await using var db = _kit.NewDb();
        var sent = await _kit.Invitations(db).ResendAsync(org.Id, invitation.Id, "auth0|owner");

        Assert.Equal(OrgRole.Admin, sent.Invitation.Role);
        Assert.NotEqual((await _kit.ReadInvitationAsync(invitation.Id)).TokenHash, invitation.TokenHash);
    }

    [Fact]
    public async Task RevokeAsync_AnAdministratorInvitation_IsRefusedToAnAdministrator()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin", OrgRole.Admin);
        var (invitation, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", OrgRole.Admin);

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => _kit.Invitations(db).RevokeAsync(org.Id, invitation.Id, "auth0|admin"));

        Assert.Equal(OrgInvitationErrors.OwnerRequired, error.Code);
        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(invitation.Id)).Status);
    }

    [Fact]
    public async Task CopyLinkAsync_AnAdministratorInvitation_IsRefusedToAnAdministrator()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin", OrgRole.Admin);
        var (invitation, token) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", OrgRole.Admin);

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => _kit.Invitations(db).CopyLinkAsync(org.Id, invitation.Id, "auth0|admin"));

        Assert.Equal(OrgInvitationErrors.OwnerRequired, error.Code);
        Assert.Equal(OrgInvitationTokens.Hash(token), (await _kit.ReadInvitationAsync(invitation.Id)).TokenHash);
    }

    [Fact]
    public async Task AnAdministrator_MayResendRevokeAndCopyACollaboratorInvitation()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin", OrgRole.Admin);
        var (toResend, _) = await _kit.SeedInvitationAsync(org.Id, "one@example.com");
        var (toRevoke, _) = await _kit.SeedInvitationAsync(org.Id, "two@example.com");
        var (toCopy, _) = await _kit.SeedInvitationAsync(org.Id, "three@example.com");

        await using (var db = _kit.NewDb())
            Assert.Equal(OrgRole.Collaborator, (await _kit.Invitations(db).ResendAsync(org.Id, toResend.Id, "auth0|admin")).Invitation.Role);
        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).RevokeAsync(org.Id, toRevoke.Id, "auth0|admin");
        await using (var db = _kit.NewDb())
            Assert.NotNull(await _kit.Invitations(db).CopyLinkAsync(org.Id, toCopy.Id, "auth0|admin"));

        Assert.Equal(OrgInvitationStatus.Revoked, (await _kit.ReadInvitationAsync(toRevoke.Id)).Status);
        Assert.NotEqual(toCopy.TokenHash, (await _kit.ReadInvitationAsync(toCopy.Id)).TokenHash);
    }

    // ─── Lookup ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LookupAsync_AValidToken_ShowsOrgEmailNameRoleAreasAndExpiry_AndNothingAboutTheInviter()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (invitation, token) = await _kit.SeedInvitationAsync(
            org.Id, "anna.leone@example.com", OrgRole.Accountant, areas: ["short-rent", "long-rent"]);

        await using var db = _kit.NewDb();
        var preview = await _kit.Invitations(db).LookupAsync(token);

        Assert.NotNull(preview);
        Assert.Equal(
            ("Casa Rossi", "anna.leone@example.com", "Anna Leone", OrgRole.Accountant, invitation.ExpiresAt),
            (preview.OrgName, preview.Email, preview.Name, preview.Role, preview.ExpiresAt));
        Assert.Equal(["short-rent", "long-rent"], preview.Areas);
    }

    [Fact]
    public async Task LookupAsync_TokenWithCapitalsAndSpaces_IsTolerated()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, token) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com");

        await using var db = _kit.NewDb();

        Assert.NotNull(await _kit.Invitations(db).LookupAsync($" {token.ToUpperInvariant()} "));
    }

    [Fact]
    public async Task LookupAsync_EveryLinkThatDoesNotWork_GetsTheSameNullAnswer()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, expired) = await _kit.SeedInvitationAsync(org.Id, "a@example.com", expiresAt: _kit.Now.AddMinutes(-1));
        var (_, markedExpired) = await _kit.SeedInvitationAsync(org.Id, "b@example.com", status: OrgInvitationStatus.Expired, closedAt: _kit.Now);
        var (_, accepted) = await _kit.SeedInvitationAsync(org.Id, "c@example.com", status: OrgInvitationStatus.Accepted, closedAt: _kit.Now);
        var (_, revoked) = await _kit.SeedInvitationAsync(org.Id, "d@example.com", status: OrgInvitationStatus.Revoked, closedAt: _kit.Now);

        await using var db = _kit.NewDb();
        var service = _kit.Invitations(db);

        foreach (var token in new[] { expired, markedExpired, accepted, revoked, OrgInvitationTokens.Generate(), "garbage", "", "   ", new string('f', 63) })
            Assert.Null(await service.LookupAsync(token));
        Assert.Null(await service.LookupAsync(null));
    }

    [Fact]
    public async Task LookupAsync_InvitationOfADeactivatedOrg_IsInvalid()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, token) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com");
        await using (var db = _kit.NewDb())
        {
            (await db.Orgs.SingleAsync(o => o.Id == org.Id)).IsActive = false;
            await db.SaveChangesAsync();
        }

        await using var lookupDb = _kit.NewDb();

        Assert.Null(await _kit.Invitations(lookupDb).LookupAsync(token));
    }

    [Fact]
    public async Task LookupAsync_NeverReadsThroughTheTenantFilter()
    {
        // A context whose tenant filter is on for another org (an authenticated request of someone else) still finds the
        // invitation by its token: the lookup scopes by the hash, not by the tenant.
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, token) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com");

        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_kit.Database).Options,
            new FixedTenantContext(Guid.NewGuid(), filterEnabled: true));

        Assert.NotNull(await _kit.Invitations(db).LookupAsync(token));
    }

    private sealed class FixedTenantContext(Guid? orgId, bool filterEnabled) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled { get; } = filterEnabled;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private async Task<List<Guid>> ListIdsAsync(Guid orgId)
    {
        await using var db = _kit.NewDb();
        return await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrgId == orgId)
            .Select(i => i.Id)
            .ToListAsync();
    }
}
