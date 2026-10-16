using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02c (AM-D15): the cap on the emails of the invitations. An org sends at most ten in 24 hours (the creations and every
/// «reinvia», the revoked ones still counted, so create-revoke-create is no way round it) and an invitation can be sent again once
/// an hour; both are configuration. Refused with 429 and a code of their own, all or nothing (no invitation, no email, the link
/// of the first email still works); an email that could not be queued gives its allowance back. The real services over EF
/// InMemory; the race for the tenth place is proved on PostgreSQL by <c>OrgTeamCompletionsPostgresTests</c> (CI).
/// </summary>
public class OrgInvitationEmailCapTests
{
    private const string OwnerId = "auth0|owner";

    private readonly OrgInvitationTestKit _kit = new();

    private async Task<Guid> SeedScaleOrgAsync(string ownerId = OwnerId)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(ownerId, PlanTier.Scale);
        return org.Id;
    }

    private async Task<OrgInvitationSent> InviteAsync(Guid orgId, string email, string actor = OwnerId)
    {
        await using var db = _kit.NewDb();
        return await _kit.Invitations(db).CreateAsync(
            new CreateOrgInvitation(orgId, actor, email, "Persona di Prova", OrgRole.Collaborator, ["short-rent"]));
    }

    private async Task<OrgInvitationSent> ResendAsync(Guid orgId, Guid invitationId, string actor = OwnerId)
    {
        await using var db = _kit.NewDb();
        return await _kit.Invitations(db).ResendAsync(orgId, invitationId, actor);
    }

    private async Task RevokeAsync(Guid orgId, Guid invitationId)
    {
        await using var db = _kit.NewDb();
        await _kit.Invitations(db).RevokeAsync(orgId, invitationId, OwnerId);
    }

    private async Task<List<Guid>> InviteManyAsync(Guid orgId, int count, string prefix = "p", string actor = OwnerId)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
            ids.Add((await InviteAsync(orgId, $"{prefix}{i}@example.com", actor)).Invitation.Id);

        return ids;
    }

    private async Task<int> InvitationRowsAsync(Guid orgId)
    {
        await using var db = _kit.NewDb();
        return await db.OrgInvitations.IgnoreQueryFilters().CountAsync(i => i.OrgId == orgId);
    }

    private static void AssertTooMany(DomainTooManyRequestsException error, string code, int argument)
    {
        Assert.Equal(code, error.Code);
        Assert.Equal(argument, Assert.IsType<int>(Assert.Single(error.MessageArgs)));
    }

    // ─── The daily cap of the org ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_TheEleventhEmailInADay_Is429WithItsOwnCode_AndNothingIsSaved()
    {
        var orgId = await SeedScaleOrgAsync();
        await InviteManyAsync(orgId, OrgInvitationRules.DefaultDailyLimit);
        var emailsBefore = _kit.Emails.Snapshot().Count;

        var error = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => InviteAsync(orgId, "eleventh@example.com"));

        AssertTooMany(error, OrgInvitationErrors.DailyLimitReached, OrgInvitationRules.DefaultDailyLimit);
        Assert.Equal(10, await InvitationRowsAsync(orgId));
        Assert.Equal(emailsBefore, _kit.Emails.Snapshot().Count);
        Assert.DoesNotContain(await _kit.ReadActivityAsync(orgId), e => e.SubjectId == "eleventh@example.com");
    }

    [Fact]
    public async Task CreateAsync_TheRevokedInvitationsStillCount_SoCreateRevokeCreateIsNoWayRound()
    {
        // A Starter org holds two people: the loop frees the seat each time, so only the cap can stop it.
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        for (var i = 0; i < 10; i++)
        {
            var sent = await InviteAsync(org.Id, "same@example.com");
            await RevokeAsync(org.Id, sent.Invitation.Id);
        }

        var error = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => InviteAsync(org.Id, "same@example.com"));

        Assert.Equal(OrgInvitationErrors.DailyLimitReached, error.Code);
    }

    [Fact]
    public async Task CreateAsync_TheWindowSlides_AnEmailLeavesTheCountAfter24Hours()
    {
        var orgId = await SeedScaleOrgAsync();
        await InviteManyAsync(orgId, 10);

        _kit.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => InviteAsync(orgId, "late@example.com"));

        _kit.Clock.Advance(TimeSpan.FromMinutes(2));
        var sent = await InviteAsync(orgId, "later@example.com");

        Assert.Equal(OrgInvitationStatus.Pending, sent.Invitation.Status);
        // The window moves with the emails: the ones sent at different moments leave one at a time.
        Assert.Equal(11, await InvitationRowsAsync(orgId));
    }

    [Fact]
    public async Task CreateAsync_EmailsSentAtDifferentMoments_LeaveTheWindowOneAtATime()
    {
        _kit.Settings[OrgInvitationRules.DailyLimitConfigKey] = "3";
        var orgId = await SeedScaleOrgAsync();
        await InviteAsync(orgId, "a@example.com");
        _kit.Clock.Advance(TimeSpan.FromHours(10));
        await InviteAsync(orgId, "b@example.com");
        _kit.Clock.Advance(TimeSpan.FromHours(10));
        await InviteAsync(orgId, "c@example.com");
        await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => InviteAsync(orgId, "d@example.com"));

        // 24 hours and a minute after the first one, only the first has left the window: one place is free, not three.
        _kit.Clock.Advance(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(1));
        await InviteAsync(orgId, "d@example.com");
        await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => InviteAsync(orgId, "e@example.com"));
    }

    [Fact]
    public async Task ResendAsync_EveryEmailCountsNotEveryInvitation_SoTheSameFewInvitationsCannotBeSentAgainForever()
    {
        _kit.Settings[OrgInvitationRules.DailyLimitConfigKey] = "4";
        var orgId = await SeedScaleOrgAsync();
        var ids = await InviteManyAsync(orgId, 3);

        // Three creations, then one «reinvia» an hour later is the fourth email.
        _kit.Clock.Advance(TimeSpan.FromHours(1));
        await ResendAsync(orgId, ids[0]);

        // The fifth is refused even for an invitation that was never sent again: the org has sent its four.
        _kit.Clock.Advance(TimeSpan.FromHours(1));
        var error = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => ResendAsync(orgId, ids[1]));
        AssertTooMany(error, OrgInvitationErrors.DailyLimitReached, 4);

        // And so is a creation.
        await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => InviteAsync(orgId, "new@example.com"));
    }

    [Fact]
    public async Task TheCap_IsPerOrg()
    {
        var orgA = await SeedScaleOrgAsync("auth0|owner-a");
        var orgB = await SeedScaleOrgAsync("auth0|owner-b");
        await InviteManyAsync(orgA, 10, "a", "auth0|owner-a");

        await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => InviteAsync(orgA, "more@example.com", "auth0|owner-a"));
        var sent = await InviteAsync(orgB, "first@example.com", "auth0|owner-b");

        Assert.Equal(OrgInvitationStatus.Pending, sent.Invitation.Status);
    }

    [Fact]
    public async Task TheCap_IsConfigurable()
    {
        _kit.Settings[OrgInvitationRules.DailyLimitConfigKey] = "2";
        var orgId = await SeedScaleOrgAsync();
        await InviteManyAsync(orgId, 2);

        var error = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => InviteAsync(orgId, "third@example.com"));

        AssertTooMany(error, OrgInvitationErrors.DailyLimitReached, 2);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("")]
    [InlineData("2.5")]
    public async Task TheCap_AValueThatIsNotAPositiveWholeNumber_IsIgnoredAndTheDefaultApplies(string configured)
    {
        _kit.Settings[OrgInvitationRules.DailyLimitConfigKey] = configured;
        var orgId = await SeedScaleOrgAsync();

        await InviteManyAsync(orgId, OrgInvitationRules.DefaultDailyLimit);
        var error = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => InviteAsync(orgId, "over@example.com"));

        AssertTooMany(error, OrgInvitationErrors.DailyLimitReached, OrgInvitationRules.DefaultDailyLimit);
    }

    // ─── «Reinvia»: one an hour for each invitation ─────────────────────────────────────────────────────

    [Fact]
    public async Task ResendAsync_ASecondSendWithinTheHour_Is429_WithTheMinutesLeft_AndTheFirstLinkStillWorks()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        var firstToken = OrgInvitationTestKit.TokenInEmail(Assert.Single(_kit.Emails.Snapshot()));
        _kit.Clock.Advance(TimeSpan.FromMinutes(30));

        var error = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => ResendAsync(orgId, sent.Invitation.Id));

        AssertTooMany(error, OrgInvitationErrors.ResendTooSoon, 30);
        // Nothing was sent, nothing rotated: the link of the first email is still the invitation's.
        Assert.Single(_kit.Emails.Snapshot());
        await using var db = _kit.NewDb();
        Assert.NotNull(await _kit.Invitations(db).LookupAsync(firstToken));
        Assert.Equal(_kit.Now.AddMinutes(-30), (await _kit.ReadInvitationAsync(sent.Invitation.Id)).LastSentAt);
    }

    [Fact]
    public async Task ResendAsync_AfterTheHour_Works_AndTheNextOneWaitsAnotherHour()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");

        _kit.Clock.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        var tooSoon = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => ResendAsync(orgId, sent.Invitation.Id));
        // A second short of the hour still says one minute: the wait is rounded up, never down to zero.
        AssertTooMany(tooSoon, OrgInvitationErrors.ResendTooSoon, 1);

        _kit.Clock.Advance(TimeSpan.FromSeconds(1));
        var again = await ResendAsync(orgId, sent.Invitation.Id);
        Assert.Equal(_kit.Now, again.Invitation.LastSentAt);

        var next = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => ResendAsync(orgId, sent.Invitation.Id));
        AssertTooMany(next, OrgInvitationErrors.ResendTooSoon, 60);
    }

    [Fact]
    public async Task ResendAsync_TheWaitIsConfigurable()
    {
        _kit.Settings[OrgInvitationRules.ResendMinutesConfigKey] = "5";
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");

        _kit.Clock.Advance(TimeSpan.FromMinutes(4));
        await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => ResendAsync(orgId, sent.Invitation.Id));
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));

        await ResendAsync(orgId, sent.Invitation.Id);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task ResendAsync_AValueThatIsNotAPositiveWholeNumber_IsIgnoredAndTheHourApplies(string configured)
    {
        _kit.Settings[OrgInvitationRules.ResendMinutesConfigKey] = configured;
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        _kit.Clock.Advance(TimeSpan.FromMinutes(59));

        var error = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => ResendAsync(orgId, sent.Invitation.Id));

        AssertTooMany(error, OrgInvitationErrors.ResendTooSoon, 1);
    }

    [Fact]
    public async Task ResendAsync_AnInvitationNobodyHasTrackedYet_IsNeitherLimitedNorCounted()
    {
        // Rows written before the send was tracked (LastSentAt null, an empty list): nothing is invented for them.
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Scale);
        var historic = new List<Guid>();
        for (var i = 0; i < 12; i++)
            historic.Add((await _kit.SeedInvitationAsync(org.Id, $"old{i}@example.com")).Invitation.Id);

        // The twelve old ones do not use the cap of the day...
        var created = await InviteAsync(org.Id, "new@example.com");
        // ...and any of them can be sent again at once (the first «reinvia» of a tracked invitation).
        var resent = await ResendAsync(org.Id, historic[0]);

        Assert.Equal(_kit.Now, created.Invitation.LastSentAt);
        Assert.Equal(_kit.Now, resent.Invitation.LastSentAt);
        Assert.Null((await _kit.ReadInvitationAsync(historic[1])).LastSentAt);
    }

    // ─── What counts as a send ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LastSentAt_IsTheCreationAndEveryResend_NotTheReminderNorTheCopyOfTheLink()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        var created = _kit.Now;
        Assert.Equal(created, sent.Invitation.LastSentAt);

        // The reminder of the third day is the system's: it moves neither the last send nor the list the cap counts.
        _kit.Clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        await using (var db = _kit.NewDb())
            Assert.Equal(1, (await _kit.Maintenance(db).RunAsync()).Reminded);
        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).CopyLinkAsync(orgId, sent.Invitation.Id, OwnerId);
        var afterReminder = await _kit.ReadInvitationAsync(sent.Invitation.Id);
        Assert.Equal(created, afterReminder.LastSentAt);
        Assert.Equal([created], afterReminder.SendLog);

        // A «reinvia» does.
        var resent = await ResendAsync(orgId, sent.Invitation.Id);
        Assert.Equal(_kit.Now, resent.Invitation.LastSentAt);
        var stored = await _kit.ReadInvitationAsync(sent.Invitation.Id);
        // The creation, three days earlier, is out of the 24 hours the list keeps: only the last send is in it.
        Assert.Equal([_kit.Now], stored.SendLog);
    }

    [Fact]
    public async Task SendLog_KeepsOnlyTheLast24Hours_SoItNeverGrows()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        for (var i = 0; i < 5; i++)
        {
            _kit.Clock.Advance(TimeSpan.FromHours(7));
            await ResendAsync(orgId, sent.Invitation.Id);
        }

        var log = (await _kit.ReadInvitationAsync(sent.Invitation.Id)).SendLog;

        // The creation and the first four resends are past 24 hours from the last one; what is kept is the last day.
        Assert.All(log, at => Assert.True(at > _kit.Now - OrgInvitationRules.DailyWindow));
        Assert.Equal(4, log.Count);
    }

    [Fact]
    public async Task ListAsync_ShowsTheLastSend()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        _kit.Clock.Advance(TimeSpan.FromHours(2));
        await ResendAsync(orgId, sent.Invitation.Id);

        await using var db = _kit.NewDb();
        var item = Assert.Single(await _kit.Invitations(db).ListAsync(orgId));

        Assert.Equal(_kit.Now, item.LastSentAt);
    }

    // ─── An email that never went out ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_WhenTheEmailCannotBeQueued_TheAllowanceIsNotSpent()
    {
        _kit.QueueAccepts = false;
        _kit.Settings[OrgInvitationRules.DailyLimitConfigKey] = "1";
        var orgId = await SeedScaleOrgAsync();

        var sent = await InviteAsync(orgId, "anna@example.com");

        Assert.False(sent.EmailQueued);
        Assert.Null(sent.Invitation.LastSentAt);
        var stored = await _kit.ReadInvitationAsync(sent.Invitation.Id);
        Assert.Null(stored.LastSentAt);
        Assert.Empty(stored.SendLog);
        // Nothing went out, so the cap of one a day is still free, and the invitation can be sent again at once.
        _kit.QueueAccepts = true;
        var resent = await ResendAsync(orgId, sent.Invitation.Id);
        Assert.True(resent.EmailQueued);
        Assert.Equal(_kit.Now, resent.Invitation.LastSentAt);
    }

    [Fact]
    public async Task ResendAsync_WhenTheEmailCannotBeQueued_GivesTheAllowanceBack()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        var created = _kit.Now;
        _kit.Clock.Advance(TimeSpan.FromHours(2));

        _kit.QueueAccepts = false;
        var failed = await ResendAsync(orgId, sent.Invitation.Id);

        Assert.False(failed.EmailQueued);
        Assert.Equal(created, failed.Invitation.LastSentAt);
        Assert.Equal([created], (await _kit.ReadInvitationAsync(sent.Invitation.Id)).SendLog);

        // The person can try again at once, and this time it goes out.
        _kit.QueueAccepts = true;
        var again = await ResendAsync(orgId, sent.Invitation.Id);
        Assert.True(again.EmailQueued);
        Assert.Equal(_kit.Now, again.Invitation.LastSentAt);
    }

    // ─── Refusals are all or nothing ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARefusedSend_DoesNotRotateTheTokenNorMoveTheExpiry()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        var before = await _kit.ReadInvitationAsync(sent.Invitation.Id);
        _kit.Clock.Advance(TimeSpan.FromMinutes(10));

        await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => ResendAsync(orgId, sent.Invitation.Id));

        var after = await _kit.ReadInvitationAsync(sent.Invitation.Id);
        Assert.Equal((before.TokenHash, before.ExpiresAt, before.LastSentAt, before.Status), (after.TokenHash, after.ExpiresAt, after.LastSentAt, after.Status));
    }

    [Fact]
    public async Task TheEarlierRefusalsOfAResend_StillComeFirst()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        await RevokeAsync(orgId, sent.Invitation.Id);

        // An invitation that is closed is a conflict, whatever the wait says.
        var error = await Assert.ThrowsAsync<DomainConflictException>(() => ResendAsync(orgId, sent.Invitation.Id));

        Assert.Equal(OrgInvitationErrors.NotPending, error.Code);
    }

    [Fact]
    public async Task ThePersonWhoMayNotResendIt_IsRefusedBeforeAnyCapIsLookedAt()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        await _kit.SeedMemberAsync(orgId, "auth0|pia", OrgRole.PropertyManager);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ResendAsync(orgId, sent.Invitation.Id, "auth0|pia"));
    }

    [Fact]
    public void TheCodes_AreStableSnakeCase_AndTheMessageKeysExist()
    {
        Assert.Equal("invitation_daily_limit_reached", OrgInvitationErrors.DailyLimitReached);
        Assert.Equal("invitation_resend_too_soon", OrgInvitationErrors.ResendTooSoon);
        Assert.Equal(10, OrgInvitationRules.DefaultDailyLimit);
        Assert.Equal(60, OrgInvitationRules.DefaultResendMinutes);
        Assert.Equal(TimeSpan.FromHours(24), OrgInvitationRules.DailyWindow);
        Assert.Equal("OrgTeam:InvitationDailyLimit", OrgInvitationRules.DailyLimitConfigKey);
        Assert.Equal("OrgTeam:ResendMinutes", OrgInvitationRules.ResendMinutesConfigKey);
    }
}
