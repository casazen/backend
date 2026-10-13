using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.HostInvites;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.HostInvites;

public class HostInviteServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrgA = Guid.Parse("a1a1a1a1-0000-4000-8000-000000000001");
    private static readonly Guid OrgB = Guid.Parse("b2b2b2b2-0000-4000-8000-000000000002");

    private static readonly HostInviteActor HostA = new("auth0|host-a", OrgA, IsAdmin: false);
    private static readonly HostInviteActor HostA2 = new("auth0|host-a2", OrgA, IsAdmin: false);
    private static readonly HostInviteActor HostB = new("auth0|host-b", OrgB, IsAdmin: false);
    private static readonly HostInviteActor Admin = new("auth0|admin", null, IsAdmin: true);

    // ─── creation ───

    [Fact]
    public async Task CreateAsync_Host_StoresHashOnlyAttributionAndQueuesEmailWithPublicLink()
    {
        using var f = new Fixture();

        var sent = await f.Service.CreateAsync(new CreateHostInvite(HostA, "  New.Host@Example.com ", "Ciao <b>Mario</b>", "it"));

        var stored = Assert.Single(f.Db.HostInviteRecords);
        Assert.Equal(sent.InviteId, stored.Id);
        Assert.Equal("new.host@example.com", stored.Email);
        Assert.Equal(HostA.UserId, stored.InvitedByUserId);
        Assert.Equal(OrgA, stored.InvitedByOrgId);
        Assert.Equal(Start.UtcDateTime.AddDays(7), stored.ExpiresAt);
        Assert.Equal(stored.ExpiresAt, sent.ExpiresAt);

        var (to, content, template) = Assert.Single(f.Queue.Queued);
        Assert.Equal("new.host@example.com", to);
        Assert.Equal(EmailTemplates.Names.HostInvite, template);
        var token = Regex.Match(content.HtmlBody, "/invito-host/([0-9a-f]{64})").Groups[1].Value;
        Assert.Equal(SupplierInviteTokens.Hash(token), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Contains($"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/invito-host/{token}\"", content.HtmlBody);
        Assert.DoesNotContain("Ciao <b>Mario</b>", content.HtmlBody);
        Assert.Contains("Ciao &lt;b&gt;Mario&lt;/b&gt;", content.HtmlBody);
        Assert.DoesNotContain(sent.InviteId.ToString(), content.HtmlBody);
    }

    [Fact]
    public async Task CreateAsync_Host_WritesAuditEntryWithActorAndOrg()
    {
        using var f = new Fixture();

        var sent = await f.Service.CreateAsync(new CreateHostInvite(HostA, "new@example.com", null, null));

        var audit = Assert.Single(f.Db.HostInviteAuditEntries);
        Assert.Equal(sent.InviteId, audit.InviteId);
        Assert.Equal(HostInviteAuditAction.Created, audit.Action);
        Assert.Equal(HostA.UserId, audit.ActorUserId);
        Assert.Equal(OrgA, audit.ActorOrgId);
    }

    [Fact]
    public async Task CreateAsync_Admin_HasNoOrgAndNoQuota()
    {
        using var f = new Fixture(new HostInviteOptions { DailyQuotaPerHost = 1, MaxPendingPerHost = 1 });

        await f.Service.CreateAsync(new CreateHostInvite(Admin, "one@example.com", null, null));
        await f.Service.CreateAsync(new CreateHostInvite(Admin, "two@example.com", null, null));
        await f.Service.CreateAsync(new CreateHostInvite(Admin, "three@example.com", null, null));

        Assert.All(f.Db.HostInviteRecords, i => Assert.Null(i.InvitedByOrgId));
        Assert.All(f.Db.HostInviteAuditEntries, a => Assert.Null(a.ActorOrgId));
        Assert.Equal(3, f.Queue.Queued.Count);
    }

    [Fact]
    public async Task CreateAsync_EnglishLocale_SendsEnglishEmail()
    {
        using var f = new Fixture();

        await f.Service.CreateAsync(new CreateHostInvite(Admin, "en@example.com", null, "en"));

        var content = Assert.Single(f.Queue.Queued).Content;
        Assert.Equal("CasaZen invitation — Sign up as a host", content.Subject);
        Assert.Equal("en", Assert.Single(f.Db.HostInviteRecords).Locale);
    }

    [Fact]
    public async Task CreateAsync_PublicSiteUrlMissing_FailsWithoutSavingAnything()
    {
        using var f = new Fixture(publicSiteBaseUrl: null);

        await Assert.ThrowsAsync<EmailConfigurationException>(() =>
            f.Service.CreateAsync(new CreateHostInvite(HostA, "new@example.com", null, null)));

        Assert.Empty(f.Db.HostInviteRecords);
        Assert.Empty(f.Db.HostInviteAuditEntries);
    }

    [Fact]
    public async Task CreateAsync_EmailNotQueued_KeepsInviteAndDoesNotThrow()
    {
        using var f = new Fixture();
        f.Queue.Refuse = true;

        var sent = await f.Service.CreateAsync(new CreateHostInvite(HostA, "new@example.com", null, null));

        Assert.Equal(sent.InviteId, Assert.Single(f.Db.HostInviteRecords).Id);
    }

    [Fact]
    public async Task CreateAsync_OwnEmail_Throws422Self()
    {
        using var f = new Fixture();
        f.AddUser(HostA.UserId, "Host.A@example.com");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.CreateAsync(new CreateHostInvite(HostA, " host.a@EXAMPLE.com", null, null)));

        Assert.Equal(HostInviteErrors.Self, ex.Code);
        Assert.Empty(f.Db.HostInviteRecords);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public async Task CreateAsync_InvalidEmail_Throws422(string email)
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.CreateAsync(new CreateHostInvite(HostA, email, null, null)));

        Assert.Equal(HostInviteErrors.EmailInvalid, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_MessageTooLong_IsTruncatedToTheColumn()
    {
        using var f = new Fixture();

        await f.Service.CreateAsync(new CreateHostInvite(HostA, "new@example.com", new string('x', 900), null));

        Assert.Equal(HostInviteRecord.MessageMaxLength, Assert.Single(f.Db.HostInviteRecords).Message!.Length);
    }

    // ─── duplicates ───

    [Fact]
    public async Task CreateAsync_PendingInviteOfTheSameInviterForTheEmail_Throws409Duplicate()
    {
        using var f = new Fixture();
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "new@example.com", null, null));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            f.Service.CreateAsync(new CreateHostInvite(HostA, "NEW@example.com", null, null)));

        Assert.Equal(HostInviteErrors.Duplicate, ex.Code);
        Assert.Single(f.Db.HostInviteRecords);
    }

    [Fact]
    public async Task CreateAsync_AnotherInviterAlreadyInvitedTheEmail_SucceedsWithoutRevealingIt()
    {
        using var f = new Fixture();
        await f.Service.CreateAsync(new CreateHostInvite(HostB, "new@example.com", null, null));

        await f.Service.CreateAsync(new CreateHostInvite(HostA, "new@example.com", null, null));

        Assert.Equal(2, f.Db.HostInviteRecords.Count());
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("used")]
    public async Task CreateAsync_PreviousInviteNoLongerPending_AllowsANewOne(string previous)
    {
        using var f = new Fixture();
        f.Add(Invite(HostA, "new@example.com", i =>
        {
            if (previous == "revoked") i.RevokedAt = Start.UtcDateTime;
            if (previous == "expired") i.ExpiresAt = Start.UtcDateTime.AddMinutes(-1);
            if (previous == "used") i.IsUsed = true;
        }));

        await f.Service.CreateAsync(new CreateHostInvite(HostA, "new@example.com", null, null));

        Assert.Equal(2, f.Db.HostInviteRecords.Count());
    }

    // ─── account enumeration ───

    [Fact]
    public async Task CreateAsync_HostInvitesRegisteredEmail_AnswersLikeSuccessAndSendsNothing()
    {
        using var f = new Fixture();
        f.AddUser("auth0|existing", "Existing@Example.com");

        var sent = await f.Service.CreateAsync(new CreateHostInvite(HostA, "existing@example.com", null, null));

        Assert.NotEqual(Guid.Empty, sent.InviteId);
        Assert.Equal(Start.UtcDateTime.AddDays(7), sent.ExpiresAt);
        Assert.Empty(f.Queue.Queued);
        var stored = Assert.Single(f.Db.HostInviteRecords);
        Assert.Equal(HostInviteState.Pending, HostInviteRules.StateOf(stored, Start.UtcDateTime));
    }

    [Fact]
    public async Task CreateAsync_HostInvitesRegisteredEmail_UsesQuotaLikeAnyOtherInvite()
    {
        using var f = new Fixture(new HostInviteOptions { DailyQuotaPerHost = 1 });
        f.AddUser("auth0|existing", "existing@example.com");
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "existing@example.com", null, null));

        var ex = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() =>
            f.Service.CreateAsync(new CreateHostInvite(HostA, "other@example.com", null, null)));

        Assert.Equal(HostInviteErrors.QuotaExceeded, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_HostInvitesRegisteredEmail_DuplicateStillAnswers409LikeForAnyEmail()
    {
        using var f = new Fixture();
        f.AddUser("auth0|existing", "existing@example.com");
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "existing@example.com", null, null));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            f.Service.CreateAsync(new CreateHostInvite(HostA, "existing@example.com", null, null)));

        Assert.Equal(HostInviteErrors.Duplicate, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_AdminInvitesRegisteredEmail_Throws409EmailTakenAndSavesNothing()
    {
        using var f = new Fixture();
        f.AddUser("auth0|existing", "existing@example.com");

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            f.Service.CreateAsync(new CreateHostInvite(Admin, "existing@example.com", null, null)));

        Assert.Equal(HostInviteErrors.EmailTaken, ex.Code);
        Assert.Empty(f.Db.HostInviteRecords);
        Assert.Empty(f.Queue.Queued);
    }

    // ─── quota and pending cap ───

    [Fact]
    public async Task CreateAsync_DailyQuotaUsedUp_Throws429WithQuotaCode()
    {
        using var f = new Fixture(new HostInviteOptions { DailyQuotaPerHost = 2 });
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "one@example.com", null, null));
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "two@example.com", null, null));

        var ex = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() =>
            f.Service.CreateAsync(new CreateHostInvite(HostA, "three@example.com", null, null)));

        Assert.Equal(HostInviteErrors.QuotaExceeded, ex.Code);
        Assert.Equal(2, f.Db.HostInviteRecords.Count());
        Assert.Equal(2, f.Queue.Queued.Count);
    }

    [Fact]
    public async Task CreateAsync_QuotaIsPerHostOrg_NotPerAccount()
    {
        using var f = new Fixture(new HostInviteOptions { DailyQuotaPerHost = 2 });
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "one@example.com", null, null));
        await f.Service.CreateAsync(new CreateHostInvite(HostA2, "two@example.com", null, null));

        await Assert.ThrowsAsync<DomainTooManyRequestsException>(() =>
            f.Service.CreateAsync(new CreateHostInvite(HostA, "three@example.com", null, null)));
        await f.Service.CreateAsync(new CreateHostInvite(HostB, "three@example.com", null, null));
    }

    [Fact]
    public async Task CreateAsync_QuotaWindowRolls_After24HoursTheHostCanInviteAgain()
    {
        using var f = new Fixture(new HostInviteOptions { DailyQuotaPerHost = 1 });
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "one@example.com", null, null));
        await Assert.ThrowsAsync<DomainTooManyRequestsException>(() =>
            f.Service.CreateAsync(new CreateHostInvite(HostA, "two@example.com", null, null)));

        f.Clock.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1)));

        await f.Service.CreateAsync(new CreateHostInvite(HostA, "two@example.com", null, null));
        Assert.Equal(2, f.Db.HostInviteRecords.Count());
    }

    [Fact]
    public async Task CreateAsync_PendingCapReached_Throws429WithPendingCode()
    {
        using var f = new Fixture(new HostInviteOptions { MaxPendingPerHost = 2, DailyQuotaPerHost = 100 });
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "one@example.com", null, null));
        await f.Service.CreateAsync(new CreateHostInvite(HostA2, "two@example.com", null, null));

        var ex = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() =>
            f.Service.CreateAsync(new CreateHostInvite(HostA, "three@example.com", null, null)));

        Assert.Equal(HostInviteErrors.PendingLimit, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_RevokedAndExpiredDoNotCountAsPending()
    {
        using var f = new Fixture(new HostInviteOptions { MaxPendingPerHost = 1, DailyQuotaPerHost = 100 });
        f.Add(Invite(HostA, "revoked@example.com", i => i.RevokedAt = Start.UtcDateTime));
        f.Add(Invite(HostA, "expired@example.com", i => i.ExpiresAt = Start.UtcDateTime.AddHours(-1)));

        await f.Service.CreateAsync(new CreateHostInvite(HostA, "new@example.com", null, null));

        Assert.Equal(3, f.Db.HostInviteRecords.Count());
    }

    [Fact]
    public async Task GetQuotaAsync_AfterTwoInvites_ReportsLimitUsedRemainingAndReset()
    {
        using var f = new Fixture(new HostInviteOptions { DailyQuotaPerHost = 5 });
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "one@example.com", null, null));
        f.Clock.Advance(TimeSpan.FromHours(1));
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "two@example.com", null, null));
        await f.Service.CreateAsync(new CreateHostInvite(HostB, "other@example.com", null, null));

        var quota = await f.Service.GetQuotaAsync(OrgA);

        Assert.Equal(5, quota.Limit);
        Assert.Equal(2, quota.Used);
        Assert.Equal(3, quota.Remaining);
        Assert.Equal(Start.UtcDateTime.AddHours(24), quota.ResetsAt);
    }

    [Fact]
    public async Task GetQuotaAsync_NothingUsed_ResetsInTwentyFourHours()
    {
        using var f = new Fixture();

        var quota = await f.Service.GetQuotaAsync(OrgA);

        Assert.Equal(new HostInviteQuota(10, 0, 10, Start.UtcDateTime.AddHours(24)), quota);
    }

    // ─── list and authorization ───

    [Fact]
    public async Task ListAsync_Host_SeesOnlyItsOwnInvites()
    {
        using var f = new Fixture();
        f.Add(Invite(HostA, "mine@example.com"));
        f.Add(Invite(HostA2, "colleague@example.com"));
        f.Add(Invite(HostB, "other-org@example.com"));
        f.Add(Invite(Admin, "admin@example.com"));

        var (items, total) = await f.Service.ListAsync(new HostInviteListQuery(HostA, null, null, 1, 20));

        Assert.Equal(1, total);
        Assert.Equal("mine@example.com", Assert.Single(items).Email);
        Assert.Null(items[0].InvitedBy);
    }

    [Fact]
    public async Task ListAsync_Admin_SeesEveryInviteWithWhoInvited()
    {
        using var f = new Fixture();
        f.AddUser(HostA.UserId, "host.a@example.com", "Anna", "Verdi");
        f.AddOrg(OrgA, "Casa Verdi");
        f.Add(Invite(HostA, "one@example.com"));
        f.Add(Invite(HostB, "two@example.com"));

        var (items, total) = await f.Service.ListAsync(new HostInviteListQuery(Admin, null, null, 1, 20));

        Assert.Equal(2, total);
        var byAnna = Assert.Single(items, i => i.Email == "one@example.com");
        Assert.Equal("Anna Verdi", byAnna.InvitedBy!.Name);
        Assert.Equal("Casa Verdi", byAnna.InvitedBy.OrgName);
        Assert.Null(Assert.Single(items, i => i.Email == "two@example.com").InvitedBy);
    }

    [Fact]
    public async Task ListAsync_StateFilterAndPaging_AppliedInTheQuery()
    {
        using var f = new Fixture();
        for (var n = 0; n < 5; n++)
            f.Add(Invite(HostA, $"p{n}@example.com", i => i.CreatedAt = Start.UtcDateTime.AddMinutes(-n)));
        f.Add(Invite(HostA, "used@example.com", i => i.IsUsed = true));
        f.Add(Invite(HostA, "revoked@example.com", i => i.RevokedAt = Start.UtcDateTime));

        var (pending, pendingTotal) = await f.Service.ListAsync(new HostInviteListQuery(HostA, null, HostInviteState.Pending, 1, 2));
        var (revoked, _) = await f.Service.ListAsync(new HostInviteListQuery(HostA, null, HostInviteState.Revoked, 1, 20));

        Assert.Equal(5, pendingTotal);
        Assert.Equal(2, pending.Count);
        Assert.Equal("p0@example.com", pending[0].Email);
        Assert.Equal("revoked@example.com", Assert.Single(revoked).Email);
    }

    [Fact]
    public async Task ResendAsync_HostWithAnotherHostsInvite_Throws404LikeAnUnknownId()
    {
        using var f = new Fixture();
        var theirs = f.Add(Invite(HostB, "theirs@example.com"));

        var other = await Assert.ThrowsAsync<NotFoundException>(() => f.Service.ResendAsync(HostA, theirs.Id));
        var unknown = await Assert.ThrowsAsync<NotFoundException>(() => f.Service.ResendAsync(HostA, Guid.NewGuid()));

        Assert.Equal(HostInviteErrors.NotFound, other.Code);
        Assert.Equal(unknown.Code, other.Code);
        Assert.Equal(unknown.MessageKey, other.MessageKey);
    }

    [Fact]
    public async Task RevokeAsync_HostWithAnotherHostsInvite_Throws404AndLeavesItPending()
    {
        using var f = new Fixture();
        var theirs = f.Add(Invite(HostB, "theirs@example.com"));

        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.RevokeAsync(HostA, theirs.Id));

        Assert.Null((await f.Db.HostInviteRecords.FindAsync(theirs.Id))!.RevokedAt);
    }

    [Fact]
    public async Task RevokeAsync_AdminRevokesAnyInvite_RecordsTheAdminInTheAudit()
    {
        using var f = new Fixture();
        var invite = f.Add(Invite(HostA, "one@example.com"));

        await f.Service.RevokeAsync(Admin, invite.Id);

        Assert.Equal(Start.UtcDateTime, (await f.Db.HostInviteRecords.FindAsync(invite.Id))!.RevokedAt);
        var audit = Assert.Single(f.Db.HostInviteAuditEntries);
        Assert.Equal(HostInviteAuditAction.Revoked, audit.Action);
        Assert.Equal(Admin.UserId, audit.ActorUserId);
        Assert.Null(audit.ActorOrgId);
    }

    [Fact]
    public async Task ResendAsync_AdminWhileHostOrgIsAtQuota_IsNotBlockedAndDoesNotUseTheHostQuota()
    {
        using var f = new Fixture(new HostInviteOptions { DailyQuotaPerHost = 1 });
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "one@example.com", null, null));
        var invite = f.Db.HostInviteRecords.Single();

        await f.Service.ResendAsync(Admin, invite.Id);

        Assert.Equal(1, (await f.Service.GetQuotaAsync(OrgA)).Used);
    }

    // ─── resend / revoke state rules ───

    [Fact]
    public async Task ResendAsync_Pending_RotatesTheTokenSoTheOldLinkStopsWorking()
    {
        using var f = new Fixture();
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "new@example.com", null, null));
        var oldToken = TokenOfLastEmail(f);
        var invite = f.Db.HostInviteRecords.Single();
        f.Clock.Advance(TimeSpan.FromDays(2));

        var sent = await f.Service.ResendAsync(HostA, invite.Id);

        var newToken = TokenOfLastEmail(f);
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(SupplierInviteTokens.Hash(newToken), (await f.Db.HostInviteRecords.FindAsync(invite.Id))!.TokenHash);
        Assert.Equal(Start.UtcDateTime.AddDays(9), sent.ExpiresAt);
        Assert.False((await f.Service.LookupAsync(oldToken)).Valid);
        Assert.True((await f.Service.LookupAsync(newToken)).Valid);
        Assert.Equal(2, f.Db.HostInviteAuditEntries.Count());
        Assert.Contains(f.Db.HostInviteAuditEntries, a => a.Action == HostInviteAuditAction.Resent && a.ActorUserId == HostA.UserId);
    }

    [Fact]
    public async Task ResendAsync_Expired_ComesBackPending()
    {
        using var f = new Fixture();
        var invite = f.Add(Invite(HostA, "old@example.com", i => i.ExpiresAt = Start.UtcDateTime.AddDays(-1)));

        await f.Service.ResendAsync(HostA, invite.Id);

        Assert.Equal(HostInviteState.Pending, HostInviteRules.StateOf(f.Db.HostInviteRecords.Single(), Start.UtcDateTime));
    }

    [Theory]
    [InlineData("used")]
    [InlineData("revoked")]
    public async Task ResendAsync_UsedOrRevoked_Throws409NotResendable(string state)
    {
        using var f = new Fixture();
        var invite = f.Add(Invite(HostA, "x@example.com", i =>
        {
            if (state == "used") i.IsUsed = true;
            else i.RevokedAt = Start.UtcDateTime;
        }));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => f.Service.ResendAsync(HostA, invite.Id));

        Assert.Equal(HostInviteErrors.NotResendable, ex.Code);
    }

    [Fact]
    public async Task ResendAsync_CountsAgainstTheDailyQuota()
    {
        using var f = new Fixture(new HostInviteOptions { DailyQuotaPerHost = 2 });
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "one@example.com", null, null));
        var invite = f.Db.HostInviteRecords.Single();
        await f.Service.ResendAsync(HostA, invite.Id);

        var ex = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => f.Service.ResendAsync(HostA, invite.Id));

        Assert.Equal(HostInviteErrors.QuotaExceeded, ex.Code);
    }

    [Fact]
    public async Task ResendAsync_AnotherPendingInviteOfTheInviterForTheEmail_Throws409Duplicate()
    {
        using var f = new Fixture();
        var expired = f.Add(Invite(HostA, "dup@example.com", i => i.ExpiresAt = Start.UtcDateTime.AddDays(-1)));
        f.Add(Invite(HostA, "dup@example.com"));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => f.Service.ResendAsync(HostA, expired.Id));

        Assert.Equal(HostInviteErrors.Duplicate, ex.Code);
    }

    [Fact]
    public async Task ResendAsync_AdminForRegisteredEmail_Throws409EmailTaken_HostGetsNoEmailAndNoError()
    {
        using var f = new Fixture();
        var invite = f.Add(Invite(HostA, "late@example.com"));
        f.AddUser("auth0|late", "late@example.com");

        var asAdmin = await Assert.ThrowsAsync<DomainConflictException>(() => f.Service.ResendAsync(Admin, invite.Id));
        var asHost = await f.Service.ResendAsync(HostA, invite.Id);

        Assert.Equal(HostInviteErrors.EmailTaken, asAdmin.Code);
        Assert.Equal(invite.Id, asHost.InviteId);
        Assert.Empty(f.Queue.Queued);
    }

    [Fact]
    public async Task RevokeAsync_Pending_RevokesAndTheLinkStopsWorking()
    {
        using var f = new Fixture();
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "new@example.com", null, null));
        var token = TokenOfLastEmail(f);
        var invite = f.Db.HostInviteRecords.Single();

        await f.Service.RevokeAsync(HostA, invite.Id);

        var lookup = await f.Service.LookupAsync(token);
        Assert.False(lookup.Valid);
        Assert.Equal("Revoked", lookup.State);
    }

    [Theory]
    [InlineData("used")]
    [InlineData("revoked")]
    [InlineData("expired")]
    public async Task RevokeAsync_NotPending_Throws409NotPending(string state)
    {
        using var f = new Fixture();
        var invite = f.Add(Invite(HostA, "x@example.com", i =>
        {
            if (state == "used") i.IsUsed = true;
            if (state == "revoked") i.RevokedAt = Start.UtcDateTime;
            if (state == "expired") i.ExpiresAt = Start.UtcDateTime.AddMinutes(-1);
        }));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => f.Service.RevokeAsync(HostA, invite.Id));

        Assert.Equal(HostInviteErrors.NotPending, ex.Code);
    }

    // ─── lookup ───

    [Fact]
    public async Task LookupAsync_PendingInvite_ReturnsInviterNameAndMaskedEmailNeverTheWholeAddress()
    {
        using var f = new Fixture();
        f.AddUser(HostA.UserId, "host.a@example.com", "Anna", "Verdi");
        var token = SupplierInviteTokens.Generate();
        f.Add(Invite(HostA, "mario.rossi@example.com", i => i.TokenHash = SupplierInviteTokens.Hash(token)));

        var lookup = await f.Service.LookupAsync(token);

        Assert.True(lookup.Valid);
        Assert.Equal("Pending", lookup.State);
        Assert.Equal("Anna Verdi", lookup.InviterName);
        Assert.NotNull(lookup.MaskedEmail);
        Assert.EndsWith("@example.com", lookup.MaskedEmail, StringComparison.Ordinal);
        Assert.DoesNotContain("mario.rossi", lookup.MaskedEmail, StringComparison.Ordinal);
        Assert.DoesNotContain("rossi", System.Text.Json.JsonSerializer.Serialize(lookup), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupAsync_AdminInvite_HasNoInviterName()
    {
        using var f = new Fixture();
        var token = SupplierInviteTokens.Generate();
        f.Add(Invite(Admin, "x@example.com", i => i.TokenHash = SupplierInviteTokens.Hash(token)));

        var lookup = await f.Service.LookupAsync(token);

        Assert.True(lookup.Valid);
        Assert.Null(lookup.InviterName);
    }

    [Theory]
    [InlineData("used", "Used")]
    [InlineData("revoked", "Revoked")]
    [InlineData("expired", "Expired")]
    public async Task LookupAsync_NotPending_IsInvalidWithItsStateAndNoPersonalData(string state, string expectedState)
    {
        using var f = new Fixture();
        var token = SupplierInviteTokens.Generate();
        f.Add(Invite(HostA, "mario@example.com", i =>
        {
            i.TokenHash = SupplierInviteTokens.Hash(token);
            if (state == "used") i.IsUsed = true;
            if (state == "revoked") i.RevokedAt = Start.UtcDateTime;
            if (state == "expired") i.ExpiresAt = Start.UtcDateTime.AddMinutes(-1);
        }));

        var lookup = await f.Service.LookupAsync(token);

        Assert.Equal(new HostInviteLookup(false, expectedState, null, null), lookup);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public async Task LookupAsync_MalformedOrUnknownToken_IsInvalidLikeAnyOther(string? token)
    {
        using var f = new Fixture();

        var malformed = await f.Service.LookupAsync(token);
        var unknown = await f.Service.LookupAsync(SupplierInviteTokens.Generate());

        Assert.Equal(new HostInviteLookup(false, "Invalid", null, null), malformed);
        Assert.Equal(malformed, unknown);
    }

    // ─── acceptance ───

    [Fact]
    public async Task AcceptAsync_VerifiedMatchingEmail_MarksUsedWithoutGrantingAnything()
    {
        using var f = new Fixture();
        f.AddUser("auth0|new", "New.Host@example.com");
        var token = AddInvite(f, "new.host@example.com");

        await f.Service.AcceptAsync(new AcceptHostInvite(token, "auth0|new", "NEW.host@example.com ", EmailVerified: true));

        var invite = f.Db.HostInviteRecords.Single();
        Assert.True(invite.IsUsed);
        Assert.Equal(Start.UtcDateTime, invite.UsedAt);
        Assert.Equal("auth0|new", invite.UsedByUserId);
        var user = f.Db.Users.Single();
        Assert.Equal(UserRole.None, user.Role);
        Assert.Null(user.OrgId);
        Assert.Empty(user.ContextMemberships);
    }

    [Fact]
    public async Task AcceptAsync_SameAccountTwice_IsIdempotent()
    {
        using var f = new Fixture();
        var token = AddInvite(f, "new@example.com");
        var request = new AcceptHostInvite(token, "auth0|new", "new@example.com", true);
        await f.Service.AcceptAsync(request);
        var usedAt = f.Db.HostInviteRecords.Single().UsedAt;
        f.Clock.Advance(TimeSpan.FromDays(10));

        await f.Service.AcceptAsync(request);

        Assert.Equal(usedAt, f.Db.HostInviteRecords.Single().UsedAt);
    }

    [Fact]
    public async Task AcceptAsync_AnotherAccountAfterUse_Throws409Used()
    {
        using var f = new Fixture();
        var token = AddInvite(f, "new@example.com");
        await f.Service.AcceptAsync(new AcceptHostInvite(token, "auth0|new", "new@example.com", true));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            f.Service.AcceptAsync(new AcceptHostInvite(token, "auth0|other", "new@example.com", true)));

        Assert.Equal(HostInviteErrors.Used, ex.Code);
        Assert.Equal("auth0|new", f.Db.HostInviteRecords.Single().UsedByUserId);
    }

    [Fact]
    public async Task AcceptAsync_DifferentEmail_Throws422MismatchAndDoesNotRevealTheInvitedEmail()
    {
        using var f = new Fixture();
        var token = AddInvite(f, "invited@example.com");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.AcceptAsync(new AcceptHostInvite(token, "auth0|other", "someone.else@example.com", true)));

        Assert.Equal(HostInviteErrors.EmailMismatch, ex.Code);
        Assert.DoesNotContain("invited@example.com", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("invited@example.com", string.Join(',', ex.MessageArgs), StringComparison.Ordinal);
        Assert.False(f.Db.HostInviteRecords.Single().IsUsed);
    }

    [Fact]
    public async Task AcceptAsync_MatchingButUnverifiedEmail_Throws422NotVerified()
    {
        using var f = new Fixture();
        var token = AddInvite(f, "invited@example.com");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.AcceptAsync(new AcceptHostInvite(token, "auth0|new", "invited@example.com", EmailVerified: false)));

        Assert.Equal(HostInviteErrors.EmailNotVerified, ex.Code);
        Assert.False(f.Db.HostInviteRecords.Single().IsUsed);
    }

    [Fact]
    public async Task AcceptAsync_NoEmailOnTheAccount_Throws422Mismatch()
    {
        using var f = new Fixture();
        var token = AddInvite(f, "invited@example.com");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.AcceptAsync(new AcceptHostInvite(token, "auth0|new", null, true)));

        Assert.Equal(HostInviteErrors.EmailMismatch, ex.Code);
    }

    [Fact]
    public async Task AcceptAsync_Expired_Throws422Expired()
    {
        using var f = new Fixture();
        var token = AddInvite(f, "invited@example.com");
        f.Clock.Advance(TimeSpan.FromDays(7));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.AcceptAsync(new AcceptHostInvite(token, "auth0|new", "invited@example.com", true)));

        Assert.Equal(HostInviteErrors.Expired, ex.Code);
    }

    [Fact]
    public async Task AcceptAsync_Revoked_Throws422Revoked()
    {
        using var f = new Fixture();
        var token = AddInvite(f, "invited@example.com", i => i.RevokedAt = Start.UtcDateTime);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.AcceptAsync(new AcceptHostInvite(token, "auth0|new", "invited@example.com", true)));

        Assert.Equal(HostInviteErrors.Revoked, ex.Code);
    }

    [Fact]
    public async Task AcceptAsync_OldLinkAfterResend_Throws422Invalid()
    {
        using var f = new Fixture();
        await f.Service.CreateAsync(new CreateHostInvite(HostA, "invited@example.com", null, null));
        var oldToken = TokenOfLastEmail(f);
        await f.Service.ResendAsync(HostA, f.Db.HostInviteRecords.Single().Id);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.AcceptAsync(new AcceptHostInvite(oldToken, "auth0|new", "invited@example.com", true)));

        Assert.Equal(HostInviteErrors.Invalid, ex.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task AcceptAsync_MalformedOrUnknownToken_Throws422Invalid(string? token)
    {
        using var f = new Fixture();

        var malformed = await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.AcceptAsync(new AcceptHostInvite(token, "auth0|new", "x@example.com", true)));
        var unknown = await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.AcceptAsync(new AcceptHostInvite(SupplierInviteTokens.Generate(), "auth0|new", "x@example.com", true)));

        Assert.Equal(HostInviteErrors.Invalid, malformed.Code);
        Assert.Equal(HostInviteErrors.Invalid, unknown.Code);
    }

    // ─── retention ───

    [Fact]
    public async Task RunAsync_ClosedUnusedInvitesPastRetention_AreDeletedAndAuditIsKept()
    {
        using var f = new Fixture();
        var old = f.Add(Invite(HostA, "old@example.com", i => i.ExpiresAt = Start.UtcDateTime.AddDays(-31)));
        var oldRevoked = f.Add(Invite(HostA, "revoked@example.com", i => i.RevokedAt = Start.UtcDateTime.AddDays(-31)));
        var recent = f.Add(Invite(HostA, "recent@example.com", i => i.ExpiresAt = Start.UtcDateTime.AddDays(-5)));
        var pending = f.Add(Invite(HostA, "pending@example.com"));
        f.Db.HostInviteAuditEntries.Add(new HostInviteAuditEntry { InviteId = old.Id, ActorUserId = HostA.UserId, ActorOrgId = OrgA, OccurredAt = Start.UtcDateTime.AddDays(-40) });
        await f.Db.SaveChangesAsync();

        var result = await f.Retention.RunAsync();

        Assert.Equal(2, result.Deleted);
        Assert.Equal(0, result.Erased);
        var remaining = f.Db.HostInviteRecords.Select(i => i.Id).ToList();
        Assert.DoesNotContain(old.Id, remaining);
        Assert.DoesNotContain(oldRevoked.Id, remaining);
        Assert.Contains(recent.Id, remaining);
        Assert.Contains(pending.Id, remaining);
        Assert.Single(f.Db.HostInviteAuditEntries);
    }

    [Fact]
    public async Task RunAsync_UsedInvitePastRetention_KeepsAttributionButErasesEmailAndMessage()
    {
        using var f = new Fixture();
        var used = f.Add(Invite(HostA, "used@example.com", i =>
        {
            i.IsUsed = true;
            i.UsedAt = Start.UtcDateTime.AddDays(-31);
            i.UsedByUserId = "auth0|new";
            i.Message = "ciao";
        }));

        var first = await f.Retention.RunAsync();
        var second = await f.Retention.RunAsync();

        var stored = await f.Db.HostInviteRecords.FindAsync(used.Id);
        Assert.Equal(1, first.Erased);
        Assert.Equal(0, second.Erased);
        Assert.Equal(HostInviteRules.ErasedEmail(used.Id), stored!.Email);
        Assert.Null(stored.Message);
        Assert.NotNull(stored.PersonalDataErasedAt);
        Assert.Equal(HostA.UserId, stored.InvitedByUserId);
        Assert.Equal(OrgA, stored.InvitedByOrgId);
        Assert.Equal("auth0|new", stored.UsedByUserId);
    }

    // ─── fixture ───

    private static HostInviteRecord Invite(HostInviteActor actor, string email, Action<HostInviteRecord>? configure = null)
    {
        var invite = new HostInviteRecord
        {
            Email = email,
            TokenHash = SupplierInviteTokens.Hash(SupplierInviteTokens.Generate()),
            InvitedByUserId = actor.UserId,
            InvitedByOrgId = actor.IsAdmin ? null : actor.OrgId,
            CreatedAt = Start.UtcDateTime.AddHours(-1),
            ExpiresAt = Start.UtcDateTime.AddDays(6),
        };
        configure?.Invoke(invite);
        return invite;
    }

    private static string AddInvite(Fixture f, string email, Action<HostInviteRecord>? configure = null)
    {
        var token = SupplierInviteTokens.Generate();
        f.Add(Invite(HostA, email, i =>
        {
            i.TokenHash = SupplierInviteTokens.Hash(token);
            i.ExpiresAt = Start.UtcDateTime.AddDays(7);
            configure?.Invoke(i);
        }));
        return token;
    }

    private static string TokenOfLastEmail(Fixture f) =>
        Regex.Match(f.Queue.Snapshot()[^1].Content.HtmlBody, "/invito-host/([0-9a-f]{64})").Groups[1].Value;

    private sealed class Fixture : IDisposable
    {
        public Fixture(HostInviteOptions? options = null, string? publicSiteBaseUrl = EmailTestHelpers.PublicSiteBaseUrl)
        {
            Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Clock = new FakeTimeProvider(Start);
            Queue = new RecordingEmailQueue();
            var settings = Options.Create(options ?? new HostInviteOptions());
            Service = new HostInviteService(
                Db, Queue, EmailTestHelpers.Links(publicSiteBaseUrl), settings, Clock, NullLogger<HostInviteService>.Instance);
            Retention = new HostInviteRetentionService(Db, settings, Clock, NullLogger<HostInviteRetentionService>.Instance);
        }

        public AppDbContext Db { get; }
        public FakeTimeProvider Clock { get; }
        public RecordingEmailQueue Queue { get; }
        public HostInviteService Service { get; }
        public HostInviteRetentionService Retention { get; }

        public HostInviteRecord Add(HostInviteRecord invite)
        {
            Db.HostInviteRecords.Add(invite);
            Db.SaveChanges();
            return invite;
        }

        public void AddUser(string id, string email, string firstName = "Test", string lastName = "User")
        {
            Db.Users.Add(new User { Id = id, Email = email, FirstName = firstName, LastName = lastName });
            Db.SaveChanges();
        }

        public void AddOrg(Guid id, string name)
        {
            Db.Orgs.Add(new Casazen.Core.Entities.Org { Id = id, Name = name });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }
}
