using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02: the hourly maintenance of the invitations. The reminder of the third day with a fresh link, the expiry (and the
/// note to the inviter), the deletion of the closed ones after the retention; each idempotent and none of them sending a
/// mail while the feature is off. The real service over EF InMemory with a clock under the test's control; the locks are
/// proved on PostgreSQL by <c>OrgInvitationsPostgresTests</c> (CI).
/// </summary>
public class OrgInvitationMaintenanceServiceTests
{
    private readonly OrgInvitationTestKit _kit = new();

    private async Task<OrgInvitationMaintenanceResult> RunAsync()
    {
        await using var db = _kit.NewDb();
        return await _kit.Maintenance(db).RunAsync();
    }

    /// <summary>An invitation sent <paramref name="daysAgo"/> days ago (a week of validity from then).</summary>
    private async Task<(OrgInvitation Invitation, string Token)> SentAsync(
        Guid orgId,
        string email,
        double daysAgo,
        string language = "it",
        string name = "Anna Leone")
    {
        var sentAt = _kit.Now.AddDays(-daysAgo);
        var (invitation, token) = await _kit.SeedInvitationAsync(
            orgId, email, expiresAt: sentAt + OrgInvitationRules.Validity, language: language, name: name);
        return (invitation, token);
    }

    // ─── Reminder ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_OnTheThirdDay_SendsTheReminderWithAFreshLinkAndRemembersIt()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (invitation, oldToken) = await SentAsync(org.Id, "anna.leone@example.com", daysAgo: 3);

        var result = await RunAsync();

        Assert.Equal((1, 0, 0), (result.Reminded, result.Expired, result.Purged));
        var email = Assert.Single(_kit.Emails.Snapshot());
        Assert.Equal(("anna.leone@example.com", EmailTemplates.Names.OrgInvitationReminder), (email.To, email.Template));
        var newToken = OrgInvitationTestKit.TokenInEmail(email);
        Assert.NotEqual(oldToken, newToken);
        Assert.Contains("Promemoria", email.Content.Subject);
        Assert.Contains("Casa Rossi", email.Content.Subject);

        var stored = await _kit.ReadInvitationAsync(invitation.Id);
        Assert.Equal(OrgInvitationTokens.Hash(newToken), stored.TokenHash);
        Assert.Equal(_kit.Now, stored.ReminderSentAt);
        // The expiry is not extended by a reminder.
        Assert.Equal(invitation.ExpiresAt, stored.ExpiresAt);
        Assert.Equal(OrgInvitationStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task Run_TheLinkOfTheFirstEmailStopsWorkingAndTheReminderOneWorks()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, oldToken) = await SentAsync(org.Id, "anna.leone@example.com", daysAgo: 3.2);
        await RunAsync();
        var newToken = OrgInvitationTestKit.TokenInEmail(Assert.Single(_kit.Emails.Snapshot()));

        await using var db = _kit.NewDb();
        var service = _kit.Invitations(db);

        Assert.Null(await service.LookupAsync(oldToken));
        Assert.NotNull(await service.LookupAsync(newToken));
    }

    [Fact]
    public async Task Run_Twice_SendsTheReminderOnce()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await SentAsync(org.Id, "anna.leone@example.com", daysAgo: 3.5);

        var first = await RunAsync();
        _kit.Clock.Advance(TimeSpan.FromHours(1));
        var second = await RunAsync();

        Assert.Equal(1, first.Reminded);
        Assert.Equal(0, second.Reminded);
        Assert.Single(_kit.Emails.Snapshot());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(2.9)]
    public async Task Run_BeforeTheThirdDay_SendsNothing(double daysAgo)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await SentAsync(org.Id, "anna.leone@example.com", daysAgo);

        var result = await RunAsync();

        Assert.Equal(0, result.Reminded);
        Assert.Empty(_kit.Emails.Snapshot());
    }

    [Fact]
    public async Task Run_AfterTheInvitationWasSentAgain_TheReminderIsDueThreeDaysAfterTheNewSending()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        OrgInvitationSent created;
        await using (var db = _kit.NewDb())
            created = await _kit.Invitations(db).CreateAsync(new CreateOrgInvitation(org.Id, "auth0|owner", "anna.leone@example.com", "Anna Leone", OrgRole.Collaborator, ["short-rent"]));
        _kit.Clock.Advance(TimeSpan.FromDays(3));
        await RunAsync();
        Assert.Equal(2, _kit.Emails.Snapshot().Count);

        // Sent again on day 3: the reminder is due on day 6, not before.
        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).ResendAsync(org.Id, created.Invitation.Id, "auth0|owner");
        _kit.Emails.Queued.Clear();
        _kit.Clock.Advance(TimeSpan.FromDays(2.5));
        Assert.Equal(0, (await RunAsync()).Reminded);
        _kit.Clock.Advance(TimeSpan.FromDays(0.6));
        Assert.Equal(1, (await RunAsync()).Reminded);
        Assert.Single(_kit.Emails.Snapshot());
    }

    [Fact]
    public async Task Run_TheReminderIsInTheLanguageOfTheInvitation()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await SentAsync(org.Id, "anna.leone@example.com", daysAgo: 3.1, language: "en");

        await RunAsync();

        var email = Assert.Single(_kit.Emails.Snapshot());
        Assert.StartsWith("Reminder:", email.Content.Subject);
        Assert.Contains("<html lang=\"en\">", email.Content.HtmlBody);
    }

    [Theory]
    [InlineData(OrgInvitationStatus.Accepted)]
    [InlineData(OrgInvitationStatus.Revoked)]
    [InlineData(OrgInvitationStatus.Expired)]
    public async Task Run_ClosedInvitations_GetNoReminder(OrgInvitationStatus status)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: status, closedAt: _kit.Now.AddDays(-1), expiresAt: _kit.Now.AddDays(3));

        var result = await RunAsync();

        Assert.Equal(0, result.Reminded);
        Assert.Empty(_kit.Emails.Snapshot());
    }

    [Fact]
    public async Task Run_TheEmailCannotBeQueued_NothingChangesAndTheNextRunTriesAgain()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (invitation, token) = await SentAsync(org.Id, "anna.leone@example.com", daysAgo: 3.1);
        _kit.QueueAccepts = false;

        var failed = await RunAsync();

        Assert.Equal(0, failed.Reminded);
        var untouched = await _kit.ReadInvitationAsync(invitation.Id);
        // The person is not left with a dead link: the token and the reminder are as they were.
        Assert.Equal(OrgInvitationTokens.Hash(token), untouched.TokenHash);
        Assert.Null(untouched.ReminderSentAt);

        _kit.QueueAccepts = true;
        var retried = await RunAsync();
        Assert.Equal(1, retried.Reminded);
    }

    [Fact]
    public async Task Run_OrgTeamFlagOff_NoReminderIsSent()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (invitation, token) = await SentAsync(org.Id, "anna.leone@example.com", daysAgo: 3.5);
        _kit.OrgTeamFlag = false;

        var result = await RunAsync();

        Assert.Equal(0, result.Reminded);
        Assert.Empty(_kit.Emails.Snapshot());
        Assert.Equal(OrgInvitationTokens.Hash(token), (await _kit.ReadInvitationAsync(invitation.Id)).TokenHash);
    }

    [Fact]
    public async Task Run_AnInvitationOfADeactivatedOrg_GetsNoReminder()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await SentAsync(org.Id, "anna.leone@example.com", daysAgo: 3.5);
        await using (var db = _kit.NewDb())
        {
            (await db.Orgs.SingleAsync(o => o.Id == org.Id)).IsActive = false;
            await db.SaveChangesAsync();
        }

        var result = await RunAsync();

        Assert.Equal(0, result.Reminded);
    }

    // ─── Expiry ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_AfterTheExpiry_MarksTheInvitationExpiredAndTellsTheInviterOnce()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (invitation, _) = await SentAsync(org.Id, "anna.leone@example.com", daysAgo: 7.1, name: "Anna Leone");

        var first = await RunAsync();
        var second = await RunAsync();

        Assert.Equal(1, first.Expired);
        Assert.Equal(0, second.Expired);
        var stored = await _kit.ReadInvitationAsync(invitation.Id);
        Assert.Equal(OrgInvitationStatus.Expired, stored.Status);
        Assert.Equal(invitation.ExpiresAt, stored.ClosedAt);

        var note = Assert.Single(_kit.Emails.Snapshot());
        Assert.Equal(("owner@example.com", EmailTemplates.Names.OrgInvitationExpired), (note.To, note.Template));
        Assert.Contains("Anna Leone", note.Content.Subject);
        Assert.Contains("anna.leone@example.com", note.Content.HtmlBody);
        Assert.Contains("/app/account/people", note.Content.HtmlBody);
        Assert.DoesNotContain("token=", note.Content.HtmlBody);
    }

    [Fact]
    public async Task Run_ThePersonWhoInvitedIsGone_TheOwnerIsTold()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin", OrgRole.Admin);
        var sentAt = _kit.Now.AddDays(-8);
        await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", invitedBy: "auth0|admin", expiresAt: sentAt + OrgInvitationRules.Validity);
        await using (var db = _kit.NewDb())
            await _kit.Membership(db).RemoveAsync("auth0|admin");

        await RunAsync();

        Assert.Equal("owner@example.com", Assert.Single(_kit.Emails.Snapshot()).To);
    }

    [Fact]
    public async Task Run_TheInviterStillManagesTheOrg_ItIsTheInviterWhoIsTold()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin", OrgRole.Admin);
        var sentAt = _kit.Now.AddDays(-8);
        await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", invitedBy: "auth0|admin", expiresAt: sentAt + OrgInvitationRules.Validity);

        await RunAsync();

        Assert.Equal("admin@example.com", Assert.Single(_kit.Emails.Snapshot()).To);
    }

    [Fact]
    public async Task Run_AnExpiredInvitation_FreesItsSeatForTheNextInvitation()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        await SentAsync(org.Id, "anna.leone@example.com", daysAgo: 8);
        await RunAsync();

        await using var db = _kit.NewDb();
        var sent = await _kit.Invitations(db).CreateAsync(new CreateOrgInvitation(org.Id, "auth0|owner", "bruno@example.com", "Bruno", OrgRole.Collaborator, ["short-rent"]));

        Assert.Equal(OrgInvitationStatus.Pending, sent.Invitation.Status);
    }

    [Fact]
    public async Task Run_OrgTeamFlagOff_StillExpiresButTellsNobody()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (invitation, _) = await SentAsync(org.Id, "anna.leone@example.com", daysAgo: 8);
        _kit.OrgTeamFlag = false;

        var result = await RunAsync();

        Assert.Equal(1, result.Expired);
        Assert.Equal(OrgInvitationStatus.Expired, (await _kit.ReadInvitationAsync(invitation.Id)).Status);
        Assert.Empty(_kit.Emails.Snapshot());
    }

    [Fact]
    public async Task Run_AcceptedInvitationsPastTheirDate_AreLeftAlone()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (accepted, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: OrgInvitationStatus.Accepted, closedAt: _kit.Now.AddDays(-9), expiresAt: _kit.Now.AddDays(-2));

        var result = await RunAsync();

        Assert.Equal(0, result.Expired);
        Assert.Equal(OrgInvitationStatus.Accepted, (await _kit.ReadInvitationAsync(accepted.Id)).Status);
    }

    // ─── Retention ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OrgInvitationStatus.Accepted)]
    [InlineData(OrgInvitationStatus.Revoked)]
    [InlineData(OrgInvitationStatus.Expired)]
    public async Task Run_AClosedInvitationOlderThanThirtyDays_IsDeletedWithTheNameAndTheEmail(OrgInvitationStatus status)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (old, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: status, closedAt: _kit.Now.AddDays(-31), expiresAt: _kit.Now.AddDays(-35));
        var (recent, _) = await _kit.SeedInvitationAsync(org.Id, "bruno@example.com", status: status, closedAt: _kit.Now.AddDays(-29), expiresAt: _kit.Now.AddDays(-30));

        var result = await RunAsync();

        Assert.Equal(1, result.Purged);
        await using var db = _kit.NewDb();
        var left = await db.OrgInvitations.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal([recent.Id], left.Select(i => i.Id));
        Assert.DoesNotContain(left, i => i.Id == old.Id);
    }

    [Fact]
    public async Task Run_TheDayThirtyIsKept_TheNextDayIsDeleted()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: OrgInvitationStatus.Revoked, closedAt: _kit.Now.AddDays(-30).AddMinutes(10));

        Assert.Equal(0, (await RunAsync()).Purged);
        _kit.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, (await RunAsync()).Purged);
    }

    [Fact]
    public async Task Run_AnOpenInvitationIsNeverDeleted_HoweverOld()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (open, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", expiresAt: _kit.Now.AddDays(1));
        await using (var db = _kit.NewDb())
        {
            (await db.OrgInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == open.Id)).CreatedAt = _kit.Now.AddDays(-400);
            await db.SaveChangesAsync();
        }

        var result = await RunAsync();

        Assert.Equal(0, result.Purged);
        Assert.NotNull(await _kit.ReadInvitationAsync(open.Id));
    }

    [Fact]
    public async Task Run_TheRetentionIsConfigurable()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: OrgInvitationStatus.Accepted, closedAt: _kit.Now.AddDays(-8));
        _kit.Settings[OrgInvitationRules.RetentionDaysConfigKey] = "7";

        var result = await RunAsync();

        Assert.Equal(1, result.Purged);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public async Task Run_ANonsenseRetention_NeverDeletesEverythingAtOnce(string configured)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (fresh, _) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", status: OrgInvitationStatus.Accepted, closedAt: _kit.Now.AddHours(-2));
        _kit.Settings[OrgInvitationRules.RetentionDaysConfigKey] = configured;

        try
        {
            await RunAsync();
        }
        catch (InvalidOperationException)
        {
            // An unreadable number is a configuration error, not a reason to delete.
        }

        Assert.NotNull(await _kit.ReadInvitationAsync(fresh.Id));
    }

    [Fact]
    public async Task Run_PurgesEveryOrg_NotJustOne()
    {
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await _kit.SeedInvitationAsync(orgA.Id, "a@example.com", status: OrgInvitationStatus.Revoked, closedAt: _kit.Now.AddDays(-40));
        await _kit.SeedInvitationAsync(orgB.Id, "b@example.com", status: OrgInvitationStatus.Accepted, closedAt: _kit.Now.AddDays(-40));

        var result = await RunAsync();

        Assert.Equal(2, result.Purged);
    }

    [Fact]
    public async Task Run_OnNothing_DoesNothing()
    {
        await _kit.SeedOwnerOrgAsync();

        var result = await RunAsync();

        Assert.Equal((false, 0, 0, 0), (result.Skipped, result.Reminded, result.Expired, result.Purged));
        Assert.Empty(_kit.Emails.Snapshot());
    }

    [Fact]
    public async Task Run_EveryStepInOneRun_EachInvitationInItsOwnStep()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await SentAsync(org.Id, "remind@example.com", daysAgo: 3.2);
        await SentAsync(org.Id, "expire@example.com", daysAgo: 7.5);
        await _kit.SeedInvitationAsync(org.Id, "purge@example.com", status: OrgInvitationStatus.Revoked, closedAt: _kit.Now.AddDays(-60));
        await SentAsync(org.Id, "fresh@example.com", daysAgo: 0.5);

        var result = await RunAsync();

        Assert.Equal((1, 1, 1), (result.Reminded, result.Expired, result.Purged));
        Assert.Equal(2, _kit.Emails.Snapshot().Count);
    }
}
