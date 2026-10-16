using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.Orgs;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02c (AM-03-FU1): the properties an invitation carries. A collaborator «Solo alcuni» is invited with the properties it will
/// reach; they are kept with the invitation (ids only) and become the grants of the member in the very transaction of the
/// acceptance, so it reaches exactly those and nothing else. Which invitations may carry them, which ids are accepted, what
/// happens to a property deleted in the meantime, what the anonymous lookup shows (none of it) and what the purge takes away. The
/// real services over EF InMemory; the acceptance against a revocation and against itself, under the locks, is proved on
/// PostgreSQL by <c>OrgTeamCompletionsPostgresTests</c> (CI).
/// </summary>
public class OrgInvitationPropertiesTests
{
    private const string OwnerId = "auth0|owner";
    private const string AnnaId = "auth0|anna";
    private const string AnnaEmail = "anna.leone@example.com";

    private readonly OrgInvitationTestKit _kit = new();

    private async Task<Guid> SeedOrgAsync()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        return org.Id;
    }

    private async Task<OrgInvitationSent> InviteAsync(
        Guid orgId,
        string email = AnnaEmail,
        OrgRole role = OrgRole.Collaborator,
        PropertyScope scope = PropertyScope.Selected,
        IReadOnlyCollection<Guid>? propertyIds = null,
        string actor = OwnerId)
    {
        await using var db = _kit.NewDb();
        return await _kit.Invitations(db).CreateAsync(
            new CreateOrgInvitation(orgId, actor, email, "Anna Leone", role, ["short-rent"], scope, PropertyIds: propertyIds));
    }

    private async Task<OrgInvitationAccepted> AcceptAsync(string token, string userId = AnnaId, string email = AnnaEmail)
    {
        await using var db = _kit.NewDb();
        return await _kit.Invitations(db).AcceptAsync(new AcceptOrgInvitation(
            token, userId, email, true, false, OrgInvitationTestKit.Consents(), "203.0.113.7"));
    }

    private string LastTokenSent() => OrgInvitationTestKit.TokenInEmail(_kit.Emails.Snapshot()[^1]);

    private async Task<int> InvitationRowsAsync(Guid orgId)
    {
        await using var db = _kit.NewDb();
        return await db.OrgInvitations.IgnoreQueryFilters().CountAsync(i => i.OrgId == orgId);
    }

    // ─── Writing the invitation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ACollaboratorSoloAlcuni_CarriesItsProperties_SortedAndEachOnce()
    {
        var orgId = await SeedOrgAsync();
        var trullo = await _kit.SeedPropertyAsync(orgId, "Trullo");
        var bilocale = await _kit.SeedPropertyAsync(orgId, "Bilocale", RentalMode.Long);

        var sent = await InviteAsync(orgId, propertyIds: [bilocale, trullo, trullo]);

        var expected = new[] { trullo, bilocale }.Order().ToList();
        Assert.Equal(expected, sent.Invitation.PropertyIds);
        Assert.Equal(PropertyScope.Selected, sent.Invitation.PropertyScope);
        Assert.Equal(expected, await _kit.ReadInvitationPropertiesAsync(sent.Invitation.Id));
        // The rows are the org's (tenant key) and nobody else's.
        await using var db = _kit.NewDb();
        Assert.All(await db.OrgInvitationProperties.IgnoreQueryFilters().ToListAsync(), row => Assert.Equal(orgId, row.OrgId));
    }

    [Fact]
    public async Task ListAsync_ShowsThePropertiesEachInvitationCarries_AndNoneForTheOthers()
    {
        var orgId = await SeedOrgAsync();
        var trullo = await _kit.SeedPropertyAsync(orgId, "Trullo");
        var limited = await InviteAsync(orgId, "uno@example.com", propertyIds: [trullo]);
        var plain = await InviteAsync(orgId, "due@example.com", OrgRole.Accountant, PropertyScope.All);

        await using var db = _kit.NewDb();
        var list = await _kit.Invitations(db).ListAsync(orgId);

        Assert.Equal([trullo], list.Single(i => i.Id == limited.Invitation.Id).PropertyIds);
        Assert.Empty(list.Single(i => i.Id == plain.Invitation.Id).PropertyIds);
    }

    [Fact]
    public async Task CreateAsync_SoloAlcuniWithNoProperty_IsAllowed_TheCollaboratorReachesNoneUntilTheyAreGiven()
    {
        var orgId = await SeedOrgAsync();

        var sent = await InviteAsync(orgId, propertyIds: null);

        Assert.Empty(sent.Invitation.PropertyIds);
        Assert.Empty(await _kit.ReadInvitationPropertiesAsync(sent.Invitation.Id));
        Assert.Equal(PropertyScope.Selected, (await _kit.ReadInvitationAsync(sent.Invitation.Id)).PropertyScope);
    }

    [Fact]
    public async Task CreateAsync_AnAdministratorInvitingACollaborator_CanCarryProperties()
    {
        var orgId = await SeedOrgAsync();
        await _kit.SeedMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        var trullo = await _kit.SeedPropertyAsync(orgId, "Trullo");

        var sent = await InviteAsync(orgId, propertyIds: [trullo], actor: "auth0|admin");

        Assert.Equal([trullo], sent.Invitation.PropertyIds);
    }

    [Theory]
    [InlineData(PropertyScope.All, OrgRole.Collaborator)]
    [InlineData(PropertyScope.All, OrgRole.PropertyManager)]
    [InlineData(PropertyScope.Selected, OrgRole.PropertyManager)]
    [InlineData(PropertyScope.Selected, OrgRole.Accountant)]
    [InlineData(PropertyScope.Selected, OrgRole.Admin)]
    public async Task CreateAsync_PropertiesWithAScopeOrARoleThatCannotHaveThem_Are422_AndNothingIsSaved(PropertyScope scope, OrgRole role)
    {
        var orgId = await SeedOrgAsync();
        var trullo = await _kit.SeedPropertyAsync(orgId, "Trullo");

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => InviteAsync(orgId, role: role, scope: scope, propertyIds: [trullo]));

        Assert.Equal(OrgMembershipErrors.ScopeNotSupported, error.Code);
        Assert.Equal(0, await InvitationRowsAsync(orgId));
        Assert.Empty(_kit.Emails.Snapshot());
    }

    [Fact]
    public async Task CreateAsync_APropertyThatIsUnknownDeletedOrOfAnotherOrg_Is422_AndNothingIsSaved()
    {
        var orgId = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        var trullo = await _kit.SeedPropertyAsync(orgId, "Trullo");
        var deleted = await _kit.SeedPropertyAsync(orgId, "Demolito", deleted: true);
        var foreign = await _kit.SeedPropertyAsync(other.Id, "Altrui", ownerId: "auth0|other-owner");

        foreach (var wrong in new[] { Guid.NewGuid(), deleted, foreign })
        {
            // One good property and one wrong: the whole invitation is refused, not the good part of it.
            var error = await Assert.ThrowsAsync<DomainRuleException>(() => InviteAsync(orgId, propertyIds: [trullo, wrong]));
            Assert.Equal(OrgMembershipErrors.PropertyUnknown, error.Code);
        }

        Assert.Equal(0, await InvitationRowsAsync(orgId));
        await using var db = _kit.NewDb();
        Assert.Empty(await db.OrgInvitationProperties.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_AnAdministratorInvitingAnAdministrator_IsRefusedBeforeThePropertiesAreLookedAt()
    {
        var orgId = await SeedOrgAsync();
        await _kit.SeedMemberAsync(orgId, "auth0|admin", OrgRole.Admin);

        // Not the owner: 403 first, whatever the ids say (the same rule as before this change).
        var error = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => InviteAsync(orgId, role: OrgRole.Admin, scope: PropertyScope.All, actor: "auth0|admin"));

        Assert.Equal(OrgInvitationErrors.OwnerRequired, error.Code);
    }

    // ─── The acceptance ─────────────────────────────────────────────────────────────────────────────────

    private async Task<(Guid OrgId, Guid Trullo, Guid Bilocale, Guid Casa)> OrgWithThreePropertiesAsync()
    {
        var orgId = await SeedOrgAsync();
        await _kit.SeedNewUserAsync(AnnaId, AnnaEmail);
        return (
            orgId,
            await _kit.SeedPropertyAsync(orgId, "Trullo"),
            await _kit.SeedPropertyAsync(orgId, "Bilocale", RentalMode.Long),
            await _kit.SeedPropertyAsync(orgId, "Casa Bianca"));
    }

    [Fact]
    public async Task AcceptAsync_GivesTheCollaboratorExactlyThePropertiesTheInvitationCarried()
    {
        var (orgId, trullo, bilocale, casa) = await OrgWithThreePropertiesAsync();
        var sent = await InviteAsync(orgId, propertyIds: [trullo, bilocale]);

        await AcceptAsync(LastTokenSent());

        Assert.Equal(new[] { trullo, bilocale }.Order(), await _kit.ReadGrantsAsync(AnnaId));
        Assert.DoesNotContain(casa, await _kit.ReadGrantsAsync(AnnaId));
        var member = (await _kit.ReadMemberAsync(AnnaId))!;
        Assert.Equal((OrgRole.Collaborator, PropertyScope.Selected, OrgMemberStatus.Active), (member.Role, member.PropertyScope, member.Status));
        Assert.Equal(OrgInvitationStatus.Accepted, (await _kit.ReadInvitationAsync(sent.Invitation.Id)).Status);

        // Who gave it, when: the inviter, at the acceptance.
        await using var db = _kit.NewDb();
        var rows = await db.PropertyMemberAccesses.IgnoreQueryFilters().Where(a => a.UserId == AnnaId).ToListAsync();
        Assert.All(rows, row => Assert.Equal((orgId, OwnerId), (row.OrgId, row.CreatedByUserId)));
    }

    [Fact]
    public async Task AcceptAsync_WritesTheLineOfWhoGaveWhatToWhom_AsTheInviter()
    {
        var (orgId, trullo, bilocale, _) = await OrgWithThreePropertiesAsync();
        await InviteAsync(orgId, propertyIds: [trullo, bilocale]);

        await AcceptAsync(LastTokenSent());

        var line = Assert.Single(
            await _kit.ReadActivityAsync(orgId), e => e.Type == OrgActivityType.MemberPropertyAccessChanged);
        Assert.Equal((OwnerId, AnnaId, OrgActivityArea.Account, OrgActivitySubjectType.Member),
            (line.ActorUserId, line.SubjectId, line.Area, line.SubjectType));
        var details = OrgActivityDetails.Parse(line.DetailsJson);
        Assert.Equal(("Selected", "2", "0"), (details["scope"], details["granted"], details["revoked"]));
        // The invitation was accepted by the person, the properties were given by the one who invited.
        Assert.Contains(await _kit.ReadActivityAsync(orgId), e => e.Type == OrgActivityType.InvitationAccepted && e.ActorUserId == AnnaId);
    }

    [Fact]
    public async Task AcceptAsync_APropertyDeletedSinceTheInvitation_IsSkippedWithoutAnError()
    {
        var (orgId, trullo, bilocale, _) = await OrgWithThreePropertiesAsync();
        await InviteAsync(orgId, propertyIds: [trullo, bilocale]);
        await using (var db = _kit.NewDb())
        {
            var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == bilocale);
            property.IsDeleted = true;
            property.DeletedAt = _kit.Now;
            await db.SaveChangesAsync();
        }

        var accepted = await AcceptAsync(LastTokenSent());

        Assert.Equal(orgId, accepted.OrgId);
        Assert.Equal([trullo], await _kit.ReadGrantsAsync(AnnaId));
        var details = OrgActivityDetails.Parse(
            Assert.Single(await _kit.ReadActivityAsync(orgId), e => e.Type == OrgActivityType.MemberPropertyAccessChanged).DetailsJson);
        Assert.Equal("1", details["granted"]);
    }

    [Fact]
    public async Task AcceptAsync_EveryPropertyDeleted_TheCollaboratorIsInAndReachesNone_AndNoLineIsWritten()
    {
        var (orgId, trullo, _, _) = await OrgWithThreePropertiesAsync();
        await InviteAsync(orgId, propertyIds: [trullo]);
        await using (var db = _kit.NewDb())
        {
            (await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == trullo)).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await AcceptAsync(LastTokenSent());

        Assert.Empty(await _kit.ReadGrantsAsync(AnnaId));
        Assert.Equal(PropertyScope.Selected, (await _kit.ReadMemberAsync(AnnaId))!.PropertyScope);
        Assert.DoesNotContain(await _kit.ReadActivityAsync(orgId), e => e.Type == OrgActivityType.MemberPropertyAccessChanged);
    }

    [Fact]
    public async Task AcceptAsync_AnInvitationWithoutProperties_GivesNoGrantAndWritesNoPropertyLine()
    {
        var (orgId, _, _, _) = await OrgWithThreePropertiesAsync();
        await InviteAsync(orgId, role: OrgRole.Collaborator, scope: PropertyScope.All);

        await AcceptAsync(LastTokenSent());

        Assert.Empty(await _kit.ReadGrantsAsync(AnnaId));
        Assert.Equal(PropertyScope.All, (await _kit.ReadMemberAsync(AnnaId))!.PropertyScope);
        Assert.DoesNotContain(await _kit.ReadActivityAsync(orgId), e => e.Type == OrgActivityType.MemberPropertyAccessChanged);
    }

    [Fact]
    public async Task AcceptAsync_TheSameAcceptanceTwice_GivesTheGrantsOnce()
    {
        var (orgId, trullo, bilocale, _) = await OrgWithThreePropertiesAsync();
        await InviteAsync(orgId, propertyIds: [trullo, bilocale]);
        var token = LastTokenSent();

        await AcceptAsync(token);
        await AcceptAsync(token);

        Assert.Equal(new[] { trullo, bilocale }.Order(), await _kit.ReadGrantsAsync(AnnaId));
        Assert.Single(await _kit.ReadActivityAsync(orgId), e => e.Type == OrgActivityType.MemberPropertyAccessChanged);
    }

    [Fact]
    public async Task AcceptAsync_APropertyOfAnotherOrgInTheTable_IsNeverGiven()
    {
        // Written by hand (the service never writes it): the acceptance reads the live properties of the org it joins.
        var (orgId, trullo, _, _) = await OrgWithThreePropertiesAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        var foreign = await _kit.SeedPropertyAsync(other.Id, "Altrui", ownerId: "auth0|other-owner");
        var sent = await InviteAsync(orgId, propertyIds: [trullo]);
        await using (var db = _kit.NewDb())
        {
            db.OrgInvitationProperties.Add(new OrgInvitationProperty { OrgId = orgId, InvitationId = sent.Invitation.Id, PropertyId = foreign });
            await db.SaveChangesAsync();
        }

        await AcceptAsync(LastTokenSent());

        Assert.Equal([trullo], await _kit.ReadGrantsAsync(AnnaId));
    }

    [Fact]
    public async Task AcceptAsync_AnInvitationOfBeforeTheRuleForAnotherRole_JoinsWithEveryProperty_AndGivesNoGrant()
    {
        // An invitation written when «Solo alcuni» was not yet the collaborator's alone: the person is not blocked, it reaches the
        // whole org as its role does (AM-03), and what the invitation carried is not turned into grants.
        var (orgId, trullo, _, _) = await OrgWithThreePropertiesAsync();
        var (invitation, token) = await _kit.SeedInvitationAsync(orgId, AnnaEmail, OrgRole.PropertyManager);
        await using (var db = _kit.NewDb())
        {
            (await db.OrgInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == invitation.Id)).PropertyScope = PropertyScope.Selected;
            db.OrgInvitationProperties.Add(new OrgInvitationProperty { OrgId = orgId, InvitationId = invitation.Id, PropertyId = trullo });
            await db.SaveChangesAsync();
        }

        await AcceptAsync(token);

        Assert.Empty(await _kit.ReadGrantsAsync(AnnaId));
        Assert.Equal(PropertyScope.All, (await _kit.ReadMemberAsync(AnnaId))!.PropertyScope);
    }

    [Fact]
    public async Task AcceptAsync_ARefusedAcceptance_GivesNoGrant()
    {
        var (orgId, trullo, _, _) = await OrgWithThreePropertiesAsync();
        await InviteAsync(orgId, propertyIds: [trullo]);
        var token = LastTokenSent();

        // The account that accepts is not the one the invitation is for.
        await _kit.SeedNewUserAsync("auth0|mallory", "mallory@example.com");
        var error = await Assert.ThrowsAsync<DomainForbiddenException>(() => AcceptAsync(token, "auth0|mallory", "mallory@example.com"));

        Assert.Equal(OrgInvitationErrors.EmailMismatch, error.Code);

        Assert.Empty(await _kit.ReadGrantsAsync("auth0|mallory"));
        Assert.Empty(await _kit.ReadGrantsAsync(AnnaId));
    }

    // ─── What nobody else sees, and what goes with the invitation ───────────────────────────────────────

    [Fact]
    public async Task Lookup_TheAnonymousPreview_ShowsNothingAboutTheProperties()
    {
        var (orgId, trullo, _, _) = await OrgWithThreePropertiesAsync();
        await InviteAsync(orgId, propertyIds: [trullo]);

        await using var db = _kit.NewDb();
        var preview = await _kit.Invitations(db).LookupAsync(LastTokenSent());

        Assert.NotNull(preview);
        // The preview and the response of the public lookup name no property, by id or by name.
        foreach (var type in new[] { typeof(OrgInvitationPreview), typeof(OrgInvitationLookupResponse) })
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), p => p.Name.Contains("Propert", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Purge_TheCarriedPropertiesGoWithTheClosedInvitation_AfterTheRetention()
    {
        var (orgId, trullo, _, _) = await OrgWithThreePropertiesAsync();
        var sent = await InviteAsync(orgId, propertyIds: [trullo]);
        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).RevokeAsync(orgId, sent.Invitation.Id, OwnerId);

        // Within the retention the closed invitation and what it carried are still there; after it, both are gone.
        _kit.Clock.Advance(TimeSpan.FromDays(OrgInvitationRules.DefaultRetentionDays - 1));
        await using (var db = _kit.NewDb())
            await _kit.Maintenance(db).RunAsync();
        Assert.Equal([trullo], await _kit.ReadInvitationPropertiesAsync(sent.Invitation.Id));

        _kit.Clock.Advance(TimeSpan.FromDays(2));
        await using (var db = _kit.NewDb())
            Assert.Equal(1, (await _kit.Maintenance(db).RunAsync()).Purged);

        Assert.Equal(0, await InvitationRowsAsync(orgId));
        Assert.Empty(await _kit.ReadInvitationPropertiesAsync(sent.Invitation.Id));
    }

    [Fact]
    public async Task Revoke_AnInvitationThatCarriesProperties_GivesNoOneAnything()
    {
        var (orgId, trullo, _, _) = await OrgWithThreePropertiesAsync();
        var sent = await InviteAsync(orgId, propertyIds: [trullo]);
        var token = LastTokenSent();
        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).RevokeAsync(orgId, sent.Invitation.Id, OwnerId);

        var error = await Assert.ThrowsAsync<DomainGoneException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.Revoked, error.Code);
        Assert.Empty(await _kit.ReadGrantsAsync(AnnaId));
        Assert.Null(await _kit.ReadMemberAsync(AnnaId));
    }

    [Fact]
    public async Task ResendAsync_AnInvitationThatCarriesProperties_KeepsThemAndShowsThemAgain()
    {
        var (orgId, trullo, _, _) = await OrgWithThreePropertiesAsync();
        var sent = await InviteAsync(orgId, propertyIds: [trullo]);
        _kit.Clock.Advance(TimeSpan.FromMinutes(OrgInvitationRules.DefaultResendMinutes));

        await using var db = _kit.NewDb();
        var again = await _kit.Invitations(db).ResendAsync(orgId, sent.Invitation.Id, OwnerId);

        Assert.Equal([trullo], again.Invitation.PropertyIds);
        Assert.Equal([trullo], await _kit.ReadInvitationPropertiesAsync(sent.Invitation.Id));
    }
}
