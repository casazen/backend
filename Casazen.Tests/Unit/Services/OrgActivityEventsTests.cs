using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02b: the events of the first version of the activity log, written by the services that already existed, inside the unit
/// of work of the change they record: an invitation, its revocation and its acceptance; a role, a deactivation, a reactivation, a
/// removal; the properties given to a member (AM-03: who gave what to whom); the plan, the name and the slug of the org. Each
/// line has the actor and the subject as ids and a few codes; a refusal or a repeat that changes nothing writes nothing; and
/// no name, email or slug of the scenario ends up in any line. The services over EF InMemory with a clock under the test's
/// control; that a rolled-back change takes its line with it is proved on PostgreSQL (<c>OrgActivityPostgresTests</c>).
/// </summary>
public class OrgActivityEventsTests
{
    private const string OwnerId = "auth0|owner";
    private const string AnnaId = "auth0|66aa01c2";
    private const string AnnaEmail = "anna.leone@example.com";

    private readonly OrgInvitationTestKit _kit = new();

    private async Task<List<OrgActivityEntry>> LinesAsync(Guid orgId) => await _kit.ReadActivityAsync(orgId);

    private static Dictionary<string, string> Details(OrgActivityEntry line) => OrgActivityDetails.Parse(line.DetailsJson).ToDictionary(d => d.Key, d => d.Value);

    private async Task<OrgInvitationSent> InviteAsync(Guid orgId, string email = AnnaEmail, OrgRole role = OrgRole.Collaborator, string actor = OwnerId)
    {
        await using var db = _kit.NewDb();
        return await _kit.Invitations(db).CreateAsync(new CreateOrgInvitation(orgId, actor, email, "Anna Leone", role, ["short-rent"]));
    }

    private static AcceptOrgInvitation Accept(string token, string userId = AnnaId, string email = AnnaEmail) =>
        new(token, userId, email, true, false, OrgInvitationTestKit.Consents(), "203.0.113.7");

    // ─── Invitations ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Invite_WritesTheLine_WithTheInviterTheInvitationAndTheRole()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var sent = await InviteAsync(org.Id, role: OrgRole.Admin);

        var line = Assert.Single(await LinesAsync(org.Id));
        Assert.Equal(
            (OrgActivityType.MemberInvited, OrgActivityArea.Account, OrgActivitySubjectType.Invitation, OwnerId, sent.Invitation.Id.ToString(), _kit.Now),
            (line.Type, line.Area, line.SubjectType, line.ActorUserId, line.SubjectId, line.When));
        Assert.Equal(new Dictionary<string, string> { ["role"] = "Admin" }, Details(line));
    }

    [Fact]
    public async Task Invite_ARefusedInvitation_WritesNoLine()
    {
        // Starter: two seats, the owner has one. The first invitation takes the other; the second finds none.
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        await InviteAsync(org.Id);

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => InviteAsync(org.Id, "bruno@example.com"));

        Assert.Equal(OrgSeatErrors.LimitReached, refused.Code);
        Assert.Single(await LinesAsync(org.Id));
    }

    [Fact]
    public async Task Invite_ByWhoDoesNotManageThePeople_WritesNoLine()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|collab", OrgRole.Collaborator);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InviteAsync(org.Id, actor: "auth0|collab"));

        Assert.Empty(await LinesAsync(org.Id));
    }

    [Fact]
    public async Task Revoke_WritesTheLine_AndASecondRevokeWritesNone()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var sent = await InviteAsync(org.Id, role: OrgRole.PropertyManager);
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));

        for (var i = 0; i < 2; i++)
        {
            await using var db = _kit.NewDb();
            await _kit.Invitations(db).RevokeAsync(org.Id, sent.Invitation.Id, OwnerId);
        }

        var lines = await LinesAsync(org.Id);
        Assert.Equal([OrgActivityType.MemberInvited, OrgActivityType.InvitationRevoked], lines.Select(l => l.Type));
        var revoked = lines[1];
        Assert.Equal((OwnerId, sent.Invitation.Id.ToString(), OrgActivitySubjectType.Invitation), (revoked.ActorUserId, revoked.SubjectId, revoked.SubjectType));
        Assert.Equal(new Dictionary<string, string> { ["role"] = "PropertyManager" }, Details(revoked));
    }

    [Fact]
    public async Task Accept_WritesTheLineInTheOrgJoined_WithThePersonWhoAcceptedAsTheActor()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedNewUserAsync(AnnaId, AnnaEmail);
        var sent = await InviteAsync(org.Id, role: OrgRole.Accountant);
        var token = OrgInvitationTestKit.TokenInEmail(_kit.Emails.Snapshot().Last());
        _kit.Clock.Advance(TimeSpan.FromHours(2));

        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).AcceptAsync(Accept(token));

        var lines = await LinesAsync(org.Id);
        Assert.Equal([OrgActivityType.MemberInvited, OrgActivityType.InvitationAccepted], lines.Select(l => l.Type));
        var accepted = lines[1];
        Assert.Equal(
            (AnnaId, sent.Invitation.Id.ToString(), OrgActivitySubjectType.Invitation, OrgActivityArea.Account),
            (accepted.ActorUserId, accepted.SubjectId, accepted.SubjectType, accepted.Area));
        Assert.Equal(new Dictionary<string, string> { ["role"] = "Accountant" }, Details(accepted));
    }

    [Fact]
    public async Task Accept_ARefusedAcceptance_WritesNoLine()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedNewUserAsync("auth0|bruno", "bruno@example.com");
        await InviteAsync(org.Id);
        var token = OrgInvitationTestKit.TokenInEmail(_kit.Emails.Snapshot().Last());

        await using (var db = _kit.NewDb())
        {
            var refused = await Assert.ThrowsAsync<DomainForbiddenException>(
                () => _kit.Invitations(db).AcceptAsync(Accept(token, "auth0|bruno", "bruno@example.com")));
            Assert.Equal(OrgInvitationErrors.EmailMismatch, refused.Code);
        }

        Assert.Equal([OrgActivityType.MemberInvited], (await LinesAsync(org.Id)).Select(l => l.Type));
    }

    // ─── People ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeRole_WritesTheRoleBeforeAndAfter_ForTheMemberByItsAccount()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, AnnaId, OrgRole.Collaborator);

        await using (var db = _kit.NewDb())
            await _kit.Team(db).ChangeRoleAsync(org.Id, member.Id, OrgRole.PropertyManager, OwnerId);

        var line = Assert.Single(await LinesAsync(org.Id));
        Assert.Equal(
            (OrgActivityType.MemberRoleChanged, OwnerId, AnnaId, OrgActivitySubjectType.Member),
            (line.Type, line.ActorUserId, line.SubjectId, line.SubjectType));
        Assert.Equal(new Dictionary<string, string> { ["fromRole"] = "Collaborator", ["toRole"] = "PropertyManager" }, Details(line));
    }

    [Fact]
    public async Task ChangeRole_ToTheRoleItAlreadyHas_WritesNothing()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, AnnaId, OrgRole.Accountant);

        await using (var db = _kit.NewDb())
            await _kit.Team(db).ChangeRoleAsync(org.Id, member.Id, OrgRole.Accountant, OwnerId);

        Assert.Empty(await LinesAsync(org.Id));
    }

    [Fact]
    public async Task ChangeRole_Refused_WritesNoLine()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var ownerRow = (await _kit.ReadMemberAsync(OwnerId))!;
        await _kit.SeedMemberAsync(org.Id, AnnaId, OrgRole.Admin);

        await using (var db = _kit.NewDb())
        {
            // The owner is never changed; an administrator does not make another administrator.
            await Assert.ThrowsAsync<DomainConflictException>(() => _kit.Team(db).ChangeRoleAsync(org.Id, ownerRow.Id, OrgRole.Admin, AnnaId));
        }

        await using (var db = _kit.NewDb())
        {
            var (_, bruno) = await _kit.SeedMemberAsync(org.Id, "auth0|bruno", OrgRole.Collaborator);
            await Assert.ThrowsAsync<DomainForbiddenException>(() => _kit.Team(db).ChangeRoleAsync(org.Id, bruno.Id, OrgRole.Admin, AnnaId));
        }

        Assert.Empty(await LinesAsync(org.Id));
        Assert.Equal(OrgRole.Admin, (await _kit.ReadMemberAsync(AnnaId))!.Role);
    }

    [Fact]
    public async Task DeactivateReactivateAndRemove_EachWriteTheirLine_WithTheRoleOfThePerson()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, AnnaId, OrgRole.Accountant);

        await using (var db = _kit.NewDb())
            await _kit.Team(db).DeactivateAsync(org.Id, member.Id, OwnerId);
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        await using (var db = _kit.NewDb())
            await _kit.Team(db).ReactivateAsync(org.Id, member.Id, OwnerId);
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        await using (var db = _kit.NewDb())
            await _kit.Team(db).RemoveAsync(org.Id, member.Id, OwnerId);

        var lines = await LinesAsync(org.Id);
        Assert.Equal(
            [OrgActivityType.MemberDeactivated, OrgActivityType.MemberReactivated, OrgActivityType.MemberRemoved],
            lines.Select(l => l.Type));
        Assert.All(lines, line =>
        {
            Assert.Equal((OwnerId, AnnaId, OrgActivitySubjectType.Member, OrgActivityArea.Account), (line.ActorUserId, line.SubjectId, line.SubjectType, line.Area));
            Assert.Equal(new Dictionary<string, string> { ["role"] = "Accountant" }, Details(line));
        });
    }

    [Fact]
    public async Task DeactivateAndReactivate_Repeated_WriteOnlyTheFirst()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, AnnaId, OrgRole.Collaborator);

        for (var i = 0; i < 2; i++)
        {
            await using var db = _kit.NewDb();
            await _kit.Team(db).DeactivateAsync(org.Id, member.Id, OwnerId);
        }

        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 2; i++)
        {
            await using var db = _kit.NewDb();
            await _kit.Team(db).ReactivateAsync(org.Id, member.Id, OwnerId);
        }

        Assert.Equal(
            [OrgActivityType.MemberDeactivated, OrgActivityType.MemberReactivated],
            (await LinesAsync(org.Id)).Select(l => l.Type));
    }

    [Fact]
    public async Task Reactivate_WithNoSeatLeft_WritesNoLine()
    {
        // Starter, two seats: the owner and Anna. Anna is deactivated, a new person is invited into the free seat, and Anna's
        // return finds none.
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        var (_, anna) = await _kit.SeedMemberAsync(org.Id, AnnaId, OrgRole.Collaborator, status: OrgMemberStatus.Deactivated);
        await InviteAsync(org.Id, "bruno@example.com");

        await using (var db = _kit.NewDb())
        {
            var refused = await Assert.ThrowsAsync<DomainConflictException>(() => _kit.Team(db).ReactivateAsync(org.Id, anna.Id, OwnerId));
            Assert.Equal(OrgSeatErrors.LimitReached, refused.Code);
        }

        Assert.Equal([OrgActivityType.MemberInvited], (await LinesAsync(org.Id)).Select(l => l.Type));
    }

    // ─── The properties given to a member (AM-03: who gave what to whom) ────────────────────────────────

    private async Task<(Guid OrgId, OrgMember Member, List<Guid> Properties)> OrgWithACollaboratorAndPropertiesAsync()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, AnnaId, OrgRole.Collaborator);
        var properties = new List<Guid>();
        await using (var db = _kit.NewDb())
        {
            for (var i = 0; i < 3; i++)
            {
                var property = HostScopeScenario.NewProperty(org.Id, OwnerId, $"Casa {i}");
                db.Properties.Add(property);
                properties.Add(property.Id);
            }

            await db.SaveChangesAsync();
        }

        return (org.Id, member, properties);
    }

    [Fact]
    public async Task PropertyAccess_Set_WritesWhoGaveWhatToWhom_AsTheScopeAndHowManyWereGivenAndTaken()
    {
        var (orgId, member, properties) = await OrgWithACollaboratorAndPropertiesAsync();

        await using (var db = _kit.NewDb())
            await _kit.PropertyAccess(db).SetAsync(orgId, member.Id, OwnerId, PropertyScope.Selected, [properties[0], properties[1]]);
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        await using (var db = _kit.NewDb())
            await _kit.PropertyAccess(db).SetAsync(orgId, member.Id, OwnerId, PropertyScope.Selected, [properties[1], properties[2]]);
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        await using (var db = _kit.NewDb())
            await _kit.PropertyAccess(db).SetAsync(orgId, member.Id, OwnerId, PropertyScope.All, []);

        var lines = await LinesAsync(orgId);
        Assert.All(lines, line => Assert.Equal(
            (OrgActivityType.MemberPropertyAccessChanged, OwnerId, AnnaId, OrgActivitySubjectType.Member),
            (line.Type, line.ActorUserId, line.SubjectId, line.SubjectType)));
        Assert.Equal(
            [
                new Dictionary<string, string> { ["scope"] = "Selected", ["granted"] = "2", ["revoked"] = "0" },
                new Dictionary<string, string> { ["scope"] = "Selected", ["granted"] = "1", ["revoked"] = "1" },
                new Dictionary<string, string> { ["scope"] = "All", ["granted"] = "0", ["revoked"] = "2" },
            ],
            lines.Select(Details));
    }

    [Fact]
    public async Task PropertyAccess_SetToWhatItAlreadyIs_WritesNothing()
    {
        var (orgId, member, properties) = await OrgWithACollaboratorAndPropertiesAsync();
        await using (var db = _kit.NewDb())
            await _kit.PropertyAccess(db).SetAsync(orgId, member.Id, OwnerId, PropertyScope.Selected, [properties[0]]);

        await using (var db = _kit.NewDb())
            await _kit.PropertyAccess(db).SetAsync(orgId, member.Id, OwnerId, PropertyScope.Selected, [properties[0]]);
        await using (var db = _kit.NewDb())
        {
            var other = await _kit.SeedMemberAsync(orgId, "auth0|bruno", OrgRole.Collaborator);
            await _kit.PropertyAccess(db).SetAsync(orgId, other.Member.Id, OwnerId, PropertyScope.All, []);
        }

        Assert.Single(await LinesAsync(orgId));
    }

    [Fact]
    public async Task PropertyAccess_NarrowingToNoneAtAll_IsALineToo()
    {
        var (orgId, member, _) = await OrgWithACollaboratorAndPropertiesAsync();

        await using (var db = _kit.NewDb())
            await _kit.PropertyAccess(db).SetAsync(orgId, member.Id, OwnerId, PropertyScope.Selected, []);

        var line = Assert.Single(await LinesAsync(orgId));
        Assert.Equal(new Dictionary<string, string> { ["scope"] = "Selected", ["granted"] = "0", ["revoked"] = "0" }, Details(line));
    }

    [Fact]
    public async Task PropertyAccess_Refused_WritesNoLine()
    {
        var (orgId, member, properties) = await OrgWithACollaboratorAndPropertiesAsync();
        var (_, manager) = await _kit.SeedMemberAsync(orgId, "auth0|manager", OrgRole.PropertyManager);

        await using (var db = _kit.NewDb())
        {
            await Assert.ThrowsAsync<DomainRuleException>(
                () => _kit.PropertyAccess(db).SetAsync(orgId, manager.Id, OwnerId, PropertyScope.Selected, [properties[0]]));
        }

        await using (var db = _kit.NewDb())
        {
            await Assert.ThrowsAsync<DomainRuleException>(
                () => _kit.PropertyAccess(db).SetAsync(orgId, member.Id, OwnerId, PropertyScope.Selected, [Guid.NewGuid()]));
        }

        Assert.Empty(await LinesAsync(orgId));
    }

    // ─── Plan and organization ──────────────────────────────────────────────────────────────────────────

    private OrgService Orgs(AppDbContext db) => new(db, _kit.Activity(db));

    [Fact]
    public async Task UpdateSettings_NameAndSlug_WriteOneLineEach_ThatSayTheyChangedAndNotWhatTheyBecame()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        await using (var db = _kit.NewDb())
        {
            await Orgs(db).UpdateSettingsAsync(
                org.Id, "Villa Mare Segreta", "villa-mare-segreta", "host@example.com", false, OwnerId);
        }

        var lines = await LinesAsync(org.Id);
        Assert.Equal(
            new[] { OrgActivityType.OrgNameChanged, OrgActivityType.OrgSlugChanged }.Order(),
            lines.Select(l => l.Type).Order());
        Assert.All(lines, line =>
        {
            Assert.Equal((OwnerId, org.Id.ToString(), OrgActivitySubjectType.Org, "{}"), (line.ActorUserId, line.SubjectId, line.SubjectType, line.DetailsJson));
            Assert.DoesNotContain("Villa", JsonSerializer.Serialize(line), StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task UpdateSettings_OnlyWhatChanged_IsWritten()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        // The same name and slug with another contact email: nothing for the log.
        await using (var db = _kit.NewDb())
        {
            var stored = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id);
            await Orgs(db).UpdateSettingsAsync(org.Id, stored.Name, stored.Slug, "another@example.com", true, OwnerId);
        }

        Assert.Empty(await LinesAsync(org.Id));

        // Only the name: one line.
        await using (var db = _kit.NewDb())
        {
            var stored = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id);
            await Orgs(db).UpdateSettingsAsync(org.Id, "Casa Bianca", stored.Slug, "another@example.com", true, OwnerId);
        }

        Assert.Equal([OrgActivityType.OrgNameChanged], (await LinesAsync(org.Id)).Select(l => l.Type));
    }

    [Fact]
    public async Task UpdateSettings_ASlugAnotherOrgUses_IsRefusedAndWritesNoLine()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        string takenSlug;
        await using (var db = _kit.NewDb())
            takenSlug = (await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == other.Id)).Slug;

        await using (var db = _kit.NewDb())
        {
            await Assert.ThrowsAsync<DomainConflictException>(
                () => Orgs(db).UpdateSettingsAsync(org.Id, "Casa Nuova", takenSlug, "host@example.com", false, "auth0|owner-a"));
        }

        Assert.Empty(await LinesAsync(org.Id));
    }

    [Theory]
    [InlineData(PlanChangeSource.Org, "org")]
    [InlineData(PlanChangeSource.Staff, "staff")]
    public async Task UpdatePlan_WritesTheTiersAndWhoChangedIt(PlanChangeSource source, string code)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro, status: SubscriptionStatus.None);

        await using (var db = _kit.NewDb())
            await Orgs(db).UpdatePlanTierAsync(org.Id, PlanTier.Starter, "auth0|actor", source);

        var line = Assert.Single(await LinesAsync(org.Id));
        Assert.Equal(
            (OrgActivityType.PlanChanged, "auth0|actor", org.Id.ToString(), OrgActivitySubjectType.Org),
            (line.Type, line.ActorUserId, line.SubjectId, line.SubjectType));
        Assert.Equal(new Dictionary<string, string> { ["fromTier"] = "Pro", ["toTier"] = "Starter", ["source"] = code }, Details(line));
    }

    [Fact]
    public async Task UpdatePlan_ToTheTierItAlreadyHas_WritesNothing()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro, status: SubscriptionStatus.None);

        await using (var db = _kit.NewDb())
            await Orgs(db).UpdatePlanTierAsync(org.Id, PlanTier.Pro, OwnerId);

        Assert.Empty(await LinesAsync(org.Id));
    }

    [Fact]
    public async Task SyncFromSubscription_ASubscriptionThatStoppedPaying_WritesThePlanFallingBackToStarter_WithNoActor()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro, status: SubscriptionStatus.Canceled);

        await using (var db = _kit.NewDb())
            await new EntitlementService(db, new ConfigurationBuilder().Build(), _kit.Activity(db)).SyncFromSubscriptionAsync(org.Id);

        var line = Assert.Single(await LinesAsync(org.Id));
        Assert.Null(line.ActorUserId);
        Assert.Equal(new Dictionary<string, string> { ["fromTier"] = "Pro", ["toTier"] = "Starter", ["source"] = "subscription" }, Details(line));

        // Run again: the plan is already Starter, nothing more to say.
        await using (var db = _kit.NewDb())
            await new EntitlementService(db, new ConfigurationBuilder().Build(), _kit.Activity(db)).SyncFromSubscriptionAsync(org.Id);
        Assert.Single(await LinesAsync(org.Id));
    }

    // ─── Nothing personal, whatever the journey ─────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryLineOfARichJourney_HoldsNoNameNoEmailNoSlug_OnlyIdsAndCodes()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedNewUserAsync(AnnaId, AnnaEmail);
        var (_, bruno) = await _kit.SeedMemberAsync(org.Id, "auth0|66bb02d3", OrgRole.Collaborator, email: "bruno.bianchi@example.com");

        var sent = await InviteAsync(org.Id, role: OrgRole.PropertyManager);
        var token = OrgInvitationTestKit.TokenInEmail(_kit.Emails.Snapshot().Last());
        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).AcceptAsync(Accept(token));
        await using (var db = _kit.NewDb())
            await _kit.Team(db).ChangeRoleAsync(org.Id, bruno.Id, OrgRole.Accountant, OwnerId);
        await using (var db = _kit.NewDb())
            await _kit.Team(db).DeactivateAsync(org.Id, bruno.Id, OwnerId);
        await using (var db = _kit.NewDb())
            await Orgs(db).UpdateSettingsAsync(org.Id, "Villa Mare Segreta", "villa-mare-segreta", "segreto@example.com", false, OwnerId);
        await using (var db = _kit.NewDb())
            await Orgs(db).UpdatePlanTierAsync(org.Id, PlanTier.Starter, OwnerId);

        var lines = await LinesAsync(org.Id);
        Assert.True(lines.Count >= 7, $"The journey wrote only {lines.Count} lines.");

        // Every column of every line, as a reader of the table would see it.
        var everything = string.Join('\n', lines.Select(l => JsonSerializer.Serialize(l)));
        foreach (var personal in new[]
                 {
                     "Anna", "Leone", AnnaEmail, "example.com", "Bruno", "Bianchi", "bruno.bianchi", "Giulia", "Rinaldi",
                     "Casa Rossi", "Villa", "Mare", "Segreta", "villa-mare-segreta", "segreto@", "203.0.113.7", token,
                 })
        {
            Assert.DoesNotContain(personal, everything, StringComparison.OrdinalIgnoreCase);
        }

        // And the shape: ids that are opaque, details that are short codes of the type's own keys.
        foreach (var line in lines)
        {
            Assert.DoesNotContain('@', line.SubjectId);
            Assert.True(line.ActorUserId is null || !line.ActorUserId.Contains('@', StringComparison.Ordinal));
            var info = OrgActivityCatalog.Describe(line.Type);
            Assert.All(Details(line), detail =>
            {
                Assert.Contains(detail.Key, info.DetailKeys);
                Assert.True(OrgActivityCatalog.IsSafeDetailValue(detail.Value), $"{line.Type}.{detail.Key} = {detail.Value}");
            });
        }

        Assert.Contains(lines, l => l.SubjectId == sent.Invitation.Id.ToString());
    }

    [Fact]
    public async Task WithTheFlagOff_TheChangesHappenAsBefore_AndNothingIsCollected()
    {
        _kit.OrgTeamFlag = false;
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro, status: SubscriptionStatus.None);
        var (_, member) = await _kit.SeedMemberAsync(org.Id, AnnaId, OrgRole.Collaborator);

        await using (var db = _kit.NewDb())
            await _kit.Team(db).ChangeRoleAsync(org.Id, member.Id, OrgRole.Accountant, OwnerId);
        await using (var db = _kit.NewDb())
            await Orgs(db).UpdatePlanTierAsync(org.Id, PlanTier.Starter, OwnerId);
        await using (var db = _kit.NewDb())
            await Orgs(db).UpdateSettingsAsync(org.Id, "Casa Bianca", "casa-bianca", "host@example.com", false, OwnerId);

        Assert.Empty(await LinesAsync(org.Id));
        Assert.Equal(OrgRole.Accountant, (await _kit.ReadMemberAsync(AnnaId))!.Role);
        await using var verify = _kit.NewDb();
        var stored = await verify.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        Assert.Equal((PlanTier.Starter, "Casa Bianca", "casa-bianca"), (stored.PlanTier, stored.Name, stored.Slug));
    }
}
