using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02b (wave spec A6): a member who cannot do something asks the administrators for access. Who is told (the active owner and
/// administrators, the holders of <c>org.members.manage</c>, and nobody else), in which language, what the email says and that
/// the note is in it and nowhere else; what the activity log keeps; the limit of three requests a day for each person (counted
/// from the log, so it holds across API instances); and every refusal. The real service over EF InMemory with a clock under the
/// test's control; the race of parallel requests is proved on PostgreSQL by <c>OrgActivityPostgresTests</c> (CI).
/// </summary>
public class OrgAccessRequestServiceTests
{
    private const string OwnerId = "auth0|owner";
    private const string CollaboratorId = "auth0|collab";

    private readonly OrgInvitationTestKit _kit = new();

    private async Task<Guid> OrgWithATeamAsync()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin", OrgRole.Admin);
        await _kit.SeedMemberAsync(org.Id, "auth0|manager", OrgRole.PropertyManager);
        await _kit.SeedMemberAsync(org.Id, CollaboratorId, OrgRole.Collaborator);
        await _kit.SeedMemberAsync(org.Id, "auth0|accountant", OrgRole.Accountant);
        return org.Id;
    }

    private async Task<OrgAccessRequested> RequestAsync(
        Guid orgId,
        string area = "billing",
        string? note = null,
        string? language = null,
        string userId = CollaboratorId)
    {
        await using var db = _kit.NewDb();
        return await _kit.AccessRequests(db).RequestAsync(new RequestOrgAccess(orgId, userId, area, note, language));
    }

    private IReadOnlyList<(string? To, EmailContent Content, string Template)> Sent() => _kit.Emails.Snapshot();

    // ─── Who is told ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_TellsTheOwnerAndTheAdministrators_AndNobodyElse()
    {
        var orgId = await OrgWithATeamAsync();

        var result = await RequestAsync(orgId);

        Assert.Equal(2, result.Notified);
        Assert.Equal(
            ["admin@example.com", "owner@example.com"],
            Sent().Select(e => e.To).OrderBy(to => to));
        Assert.All(Sent(), e => Assert.Equal(EmailTemplates.Names.OrgAccessRequest, e.Template));
    }

    [Fact]
    public async Task Request_ADeactivatedAdministrator_AndAnAccountThatIsNotActive_AreNotTold()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin-off", OrgRole.Admin, status: OrgMemberStatus.Deactivated);
        await _kit.SeedMemberAsync(org.Id, "auth0|admin-blocked", OrgRole.Admin);
        await _kit.SeedMemberAsync(org.Id, CollaboratorId, OrgRole.Collaborator);
        await using (var db = _kit.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == "auth0|admin-blocked")).IsActive = false;
            await db.SaveChangesAsync();
        }

        var result = await RequestAsync(org.Id);

        Assert.Equal(1, result.Notified);
        Assert.Equal(["owner@example.com"], Sent().Select(e => e.To));
    }

    [Fact]
    public async Task Request_NobodyOfAnotherOrgIsTold()
    {
        var orgId = await OrgWithATeamAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await _kit.SeedMemberAsync(other.Id, "auth0|admin-b", OrgRole.Admin);

        await RequestAsync(orgId);

        Assert.DoesNotContain(Sent(), e => e.To!.Contains("-b@", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_TheRequesterItselfIsNeverTold_EvenWhenItIsAnAdministrator()
    {
        var orgId = await OrgWithATeamAsync();

        var result = await RequestAsync(orgId, area: "billing", userId: "auth0|admin");

        Assert.Equal(1, result.Notified);
        Assert.Equal(["owner@example.com"], Sent().Select(e => e.To));
    }

    [Fact]
    public void Request_TheRecipients_AreExactlyTheRolesThatHoldMembersManage()
    {
        // The service tells owner and administrators; the permission org.members.manage is what the roles of the catalog say.
        // If the catalog ever gives it to another role (or takes it from one), this fails and the recipients are decided again.
        var holders = Enum.GetValues<OrgRole>()
            .Where(role => OrgRoleCatalog.AccountRoleKey(role) is { } key
                           && OrgRoleCatalog.SeededRoles.Any(r =>
                               r.ContextKey == AccountContext.Key && r.RoleKey == key && r.Permissions.Contains(AccountContext.Permissions.MembersManage)))
            .Order()
            .ToList();

        Assert.Equal(holders, Enum.GetValues<OrgRole>().Where(OrgTeamRules.CanManageTeam).Order());
        Assert.Equal([OrgRole.Owner, OrgRole.Admin], holders);
    }

    [Fact]
    public async Task Request_NobodyToTell_IsStillRecorded_AndSaysZero()
    {
        // The owner is the only administrator and it is the one asking.
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var result = await RequestAsync(org.Id, userId: OwnerId);

        Assert.Equal(0, result.Notified);
        Assert.Empty(Sent());
        Assert.Single(await _kit.ReadActivityAsync(org.Id));
    }

    [Fact]
    public async Task Request_TheQueueRefusesTheEmails_TheRequestIsRecordedAndSaysZero()
    {
        _kit.QueueAccepts = false;
        var orgId = await OrgWithATeamAsync();

        var result = await RequestAsync(orgId);

        Assert.Equal(0, result.Notified);
        Assert.Single(await _kit.ReadActivityAsync(orgId));
    }

    // ─── What the email says ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_Italian_NamesTheRequesterItsRoleTheAreaAndTheNote_AndLinksThePeoplePage()
    {
        var orgId = await OrgWithATeamAsync();

        await RequestAsync(orgId, area: "payments", note: "Mi servono gli incassi di settembre", language: "it");

        var mail = Sent().Single(e => e.To == "owner@example.com").Content;
        Assert.Equal("Membro collab chiede di accedere a «Incassi»", mail.Subject);
        Assert.Contains("Ciao Giulia Rinaldi,", mail.HtmlBody);
        Assert.Contains("<strong>Membro collab</strong> (Collaboratore) ha chiesto di accedere a <strong>Incassi</strong> su CasaZen.", mail.HtmlBody);
        Assert.Contains("Il suo messaggio", mail.HtmlBody);
        Assert.Contains("Mi servono gli incassi di settembre", mail.HtmlBody);
        Assert.Contains($"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/app/account/people\"", mail.HtmlBody);
        Assert.Contains("Apri Persone e permessi", mail.HtmlBody);
    }

    [Fact]
    public async Task Request_English_IsTranslatedEverywhere()
    {
        var orgId = await OrgWithATeamAsync();

        await RequestAsync(orgId, area: "billing", note: "I need the invoices", language: "en");

        var mail = Sent().Single(e => e.To == "owner@example.com").Content;
        Assert.Equal("Membro collab asks for access to \"Plan and billing\"", mail.Subject);
        Assert.Contains("<html lang=\"en\">", mail.HtmlBody);
        Assert.Contains("Hello Giulia Rinaldi,", mail.HtmlBody);
        Assert.Contains("(Collaborator) asked for access to <strong>Plan and billing</strong> on CasaZen.", mail.HtmlBody);
        Assert.Contains("Their message", mail.HtmlBody);
        Assert.Contains("I need the invoices", mail.HtmlBody);
        Assert.Contains("Open People and permissions", mail.HtmlBody);
        Assert.DoesNotContain("Ciao", mail.HtmlBody);
        Assert.DoesNotContain("Apri", mail.HtmlBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("IT")]
    public async Task Request_AnyOtherLanguage_IsItalian(string? language)
    {
        var orgId = await OrgWithATeamAsync();

        await RequestAsync(orgId, language: language);

        Assert.All(Sent(), e => Assert.Contains("<html lang=\"it\">", e.Content.HtmlBody));
    }

    [Fact]
    public async Task Request_TheNote_IsHtmlEncoded_AndOnOneLine()
    {
        var orgId = await OrgWithATeamAsync();
        const string payload = "<a href=\"https://phish.example\">clicca</a>\r\nsecond line";

        await RequestAsync(orgId, note: payload);

        var body = Sent().First().Content.HtmlBody;
        Assert.DoesNotContain("<a href=\"https://phish.example\"", body);
        Assert.Contains("&lt;a href=&quot;https://phish.example&quot;&gt;clicca&lt;/a&gt; second line", body);
    }

    [Fact]
    public async Task Request_WithoutANote_SaysNothingAboutOne()
    {
        var orgId = await OrgWithATeamAsync();

        await RequestAsync(orgId, note: "   ");

        Assert.All(Sent(), e => Assert.DoesNotContain("Il suo messaggio", e.Content.HtmlBody));
    }

    [Fact]
    public async Task Request_TheAreaIsNormalized()
    {
        var orgId = await OrgWithATeamAsync();

        await RequestAsync(orgId, area: " Billing ");

        var line = Assert.Single(await _kit.ReadActivityAsync(orgId));
        Assert.Equal("{\"requestedArea\":\"billing\"}", line.DetailsJson);
    }

    // ─── What is kept ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_WritesOneLine_WithTheRequesterTheOrgAndTheAreaAsACode_NeverTheNote()
    {
        var orgId = await OrgWithATeamAsync();

        await RequestAsync(orgId, area: "short-rent", note: "Per favore, sono Mario Rossi e mi serve l'accesso");

        var line = Assert.Single(await _kit.ReadActivityAsync(orgId));
        Assert.Equal(
            (OrgActivityType.AccessRequested, OrgActivityArea.Account, OrgActivitySubjectType.Org, CollaboratorId, orgId.ToString(), _kit.Now),
            (line.Type, line.Area, line.SubjectType, line.ActorUserId, line.SubjectId, line.When));
        Assert.Equal(new Dictionary<string, string> { ["requestedArea"] = "short-rent" }, OrgActivityDetails.Parse(line.DetailsJson));

        var everything = JsonSerializer.Serialize(line);
        Assert.DoesNotContain("Mario", everything);
        Assert.DoesNotContain("Rossi", everything);
        Assert.DoesNotContain("accesso", everything);
        Assert.DoesNotContain("example.com", everything);
    }

    [Fact]
    public async Task Request_ARefusedRequest_WritesNoLineAndTellsNobody()
    {
        var orgId = await OrgWithATeamAsync();

        await Assert.ThrowsAsync<DomainRuleException>(() => RequestAsync(orgId, area: "salaries"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RequestAsync(orgId, userId: "auth0|stranger"));

        Assert.Empty(await _kit.ReadActivityAsync(orgId));
        Assert.Empty(Sent());
    }

    // ─── Who may ask, and what may be asked ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("salaries")]
    [InlineData("billing,people")]
    [InlineData("admin")]
    public async Task Request_AnAreaThatCannotBeAskedFor_Is422(string area)
    {
        var orgId = await OrgWithATeamAsync();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => RequestAsync(orgId, area: area));

        Assert.Equal(OrgAccessRequestErrors.AreaUnknown, refused.Code);
    }

    [Fact]
    public async Task Request_EveryAreaOfTheList_CanBeAskedFor()
    {
        var orgId = await OrgWithATeamAsync();
        _kit.Settings[OrgAccessRequestRules.DailyLimitConfigKey] = "100";

        foreach (var area in OrgAccessRequestRules.Areas)
            await RequestAsync(orgId, area: area);

        Assert.Equal(OrgAccessRequestRules.Areas.Count, (await _kit.ReadActivityAsync(orgId)).Count);
    }

    [Theory]
    [InlineData(OrgRole.Owner)]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Collaborator)]
    [InlineData(OrgRole.Accountant)]
    public async Task Request_AnyActiveMemberMayAsk_WhateverItsRole(OrgRole role)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var userId = role == OrgRole.Owner ? OwnerId : $"auth0|{role}";
        if (role != OrgRole.Owner)
            await _kit.SeedMemberAsync(org.Id, userId, role);

        await RequestAsync(org.Id, userId: userId);

        Assert.Equal(userId, Assert.Single(await _kit.ReadActivityAsync(org.Id)).ActorUserId);
    }

    [Fact]
    public async Task Request_ADeactivatedMember_AMemberOfAnotherOrg_AndAStranger_AreRefused()
    {
        var orgId = await OrgWithATeamAsync();
        await _kit.SeedMemberAsync(orgId, "auth0|gone", OrgRole.Collaborator, status: OrgMemberStatus.Deactivated);
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RequestAsync(orgId, userId: "auth0|gone"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RequestAsync(orgId, userId: "auth0|owner-b"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RequestAsync(orgId, userId: "auth0|nobody"));
        Assert.Empty(await _kit.ReadActivityAsync(orgId));
        Assert.NotEqual(orgId, other.Id);
    }

    // ─── The daily limit ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_ThreeADay_TheFourthIsRefused_AndTheNextDayItWorksAgain()
    {
        var orgId = await OrgWithATeamAsync();
        for (var i = 0; i < 3; i++)
        {
            await RequestAsync(orgId, area: "billing");
            _kit.Clock.Advance(TimeSpan.FromMinutes(10));
        }

        var sentBefore = Sent().Count;
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => RequestAsync(orgId, area: "reports"));

        Assert.Equal(OrgAccessRequestErrors.LimitReached, refused.Code);
        Assert.Equal(sentBefore, Sent().Count);
        Assert.Equal(3, (await _kit.ReadActivityAsync(orgId)).Count);

        // 24 hours after the first request it no longer counts: one place is free.
        _kit.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1));
        await RequestAsync(orgId, area: "reports");
        Assert.Equal(4, (await _kit.ReadActivityAsync(orgId)).Count);
    }

    [Fact]
    public async Task Request_TheLimitIsOfThePerson_NotOfTheOrgOrOfTheArea()
    {
        var orgId = await OrgWithATeamAsync();
        for (var i = 0; i < 3; i++)
            await RequestAsync(orgId, area: "billing");

        // Other people of the same org are not held back by what one person sent.
        await RequestAsync(orgId, userId: "auth0|accountant");
        await RequestAsync(orgId, userId: "auth0|manager");

        Assert.Equal(5, (await _kit.ReadActivityAsync(orgId)).Count);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    public async Task Request_TheLimitIsConfigurable(string configured, int limit)
    {
        _kit.Settings[OrgAccessRequestRules.DailyLimitConfigKey] = configured;
        var orgId = await OrgWithATeamAsync();
        for (var i = 0; i < limit; i++)
            await RequestAsync(orgId);

        await Assert.ThrowsAsync<DomainConflictException>(() => RequestAsync(orgId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-2")]
    public async Task Request_ALimitThatMakesNoSense_IsIgnored_AndThreeApply(string configured)
    {
        _kit.Settings[OrgAccessRequestRules.DailyLimitConfigKey] = configured;
        var orgId = await OrgWithATeamAsync();
        for (var i = 0; i < 3; i++)
            await RequestAsync(orgId);

        await Assert.ThrowsAsync<DomainConflictException>(() => RequestAsync(orgId));
    }

    // ─── Configuration ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_WithNoPublicUrl_IsAConfigurationError_AndNothingIsSaved()
    {
        _kit.PublicSiteBaseUrl = null;
        var orgId = await OrgWithATeamAsync();

        await Assert.ThrowsAsync<EmailConfigurationException>(() => RequestAsync(orgId));

        Assert.Empty(await _kit.ReadActivityAsync(orgId));
    }
}
