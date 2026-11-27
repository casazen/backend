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

    [Fact]
    public async Task CreateAsync_AgencyCanSendMoreThanTheFormerDailyCap()
    {
        var orgId = await SeedScaleOrgAsync();
        _kit.Settings[OrgInvitationRules.DailyLimitConfigKey] = "1";
        await InviteManyAsync(orgId, 25);
        Assert.Equal(25, await InvitationRowsAsync(orgId));
        Assert.Equal(25, _kit.Emails.Snapshot().Count);
    }

    [Fact]
    public async Task ResendAsync_ASecondSendWithinTheHour_Is429_WithTheMinutesLeft_AndTheFirstLinkStillWorks()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        var firstToken = OrgInvitationTestKit.TokenInEmail(Assert.Single(_kit.Emails.Snapshot()));
        _kit.Clock.Advance(TimeSpan.FromMinutes(30));

        var error = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => ResendAsync(orgId, sent.Invitation.Id));

        AssertTooMany(error, OrgInvitationErrors.ResendTooSoon, 30);
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
        AssertTooMany(tooSoon, OrgInvitationErrors.ResendTooSoon, 1);

        _kit.Clock.Advance(TimeSpan.FromSeconds(1));
        var again = await ResendAsync(orgId, sent.Invitation.Id);
        Assert.Equal(_kit.Now, again.Invitation.LastSentAt);

        var next = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => ResendAsync(orgId, sent.Invitation.Id));
        AssertTooMany(next, OrgInvitationErrors.ResendTooSoon, 60);
    }

    [Fact]
    public async Task ResendAsync_TheResendMinutesSetting_ChangesTheWait()
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
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Scale);
        var historic = new List<Guid>();
        for (var i = 0; i < 12; i++)
            historic.Add((await _kit.SeedInvitationAsync(org.Id, $"old{i}@example.com")).Invitation.Id);

        var created = await InviteAsync(org.Id, "new@example.com");
        var resent = await ResendAsync(org.Id, historic[0]);

        Assert.Equal(_kit.Now, created.Invitation.LastSentAt);
        Assert.Equal(_kit.Now, resent.Invitation.LastSentAt);
        Assert.Null((await _kit.ReadInvitationAsync(historic[1])).LastSentAt);
    }

    [Fact]
    public async Task LastSentAt_IsTheCreationAndEveryResend_NotTheReminderNorTheCopyOfTheLink()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        var created = _kit.Now;
        Assert.Equal(created, sent.Invitation.LastSentAt);

        _kit.Clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        await using (var db = _kit.NewDb())
            Assert.Equal(1, (await _kit.Maintenance(db).RunAsync()).Reminded);
        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).CopyLinkAsync(orgId, sent.Invitation.Id, OwnerId);
        var afterReminder = await _kit.ReadInvitationAsync(sent.Invitation.Id);
        Assert.Equal(created, afterReminder.LastSentAt);
        Assert.Equal([created], afterReminder.SendLog);

        var resent = await ResendAsync(orgId, sent.Invitation.Id);
        Assert.Equal(_kit.Now, resent.Invitation.LastSentAt);
        var stored = await _kit.ReadInvitationAsync(sent.Invitation.Id);
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

        Assert.All(log, at => Assert.True(at > _kit.Now - OrgInvitationRules.DailyWindow));
        Assert.Equal(4, log.Count);
    }

    [Fact]
    public async Task ListAsync_AnInvitationThatWasSent_ShowsTheLastSend()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        _kit.Clock.Advance(TimeSpan.FromHours(2));
        await ResendAsync(orgId, sent.Invitation.Id);

        await using var db = _kit.NewDb();
        var item = Assert.Single(await _kit.Invitations(db).ListAsync(orgId));

        Assert.Equal(_kit.Now, item.LastSentAt);
    }

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

        _kit.QueueAccepts = true;
        var again = await ResendAsync(orgId, sent.Invitation.Id);
        Assert.True(again.EmailQueued);
        Assert.Equal(_kit.Now, again.Invitation.LastSentAt);
    }

    [Fact]
    public async Task ResendAsync_ARefusedSend_DoesNotRotateTheTokenNorMoveTheExpiry()
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
    public async Task ResendAsync_TheEarlierRefusals_StillComeFirst()
    {
        var orgId = await SeedScaleOrgAsync();
        var sent = await InviteAsync(orgId, "anna@example.com");
        await RevokeAsync(orgId, sent.Invitation.Id);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => ResendAsync(orgId, sent.Invitation.Id));

        Assert.Equal(OrgInvitationErrors.NotPending, error.Code);
    }

    [Fact]
    public async Task ResendAsync_APersonWhoMayNotResendIt_IsRefusedBeforeAnyCapIsLookedAt()
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
