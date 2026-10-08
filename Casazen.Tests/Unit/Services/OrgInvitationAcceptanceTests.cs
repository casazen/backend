using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02: the acceptance of an invitation. Every refusal of <c>gap/06</c> §4.3 (a link used, expired or revoked, another
/// email, an unverified email, a platform admin, an org that is not empty, a member of another org, the consents) and every
/// way in (a person with no org, one with an empty org of its own, a legacy link); and that a refusal writes nothing. The
/// real services over EF InMemory; the transaction, the locks and the race between two acceptances are proved on PostgreSQL by
/// <c>OrgInvitationsPostgresTests</c> (CI).
/// </summary>
public class OrgInvitationAcceptanceTests
{
    private const string AnnaId = "auth0|anna";
    private const string AnnaEmail = "anna.leone@example.com";

    private readonly OrgInvitationTestKit _kit = new();

    private async Task<OrgInvitationAccepted> AcceptAsync(
        string token,
        string userId = AnnaId,
        string email = AnnaEmail,
        bool verified = true,
        bool platformAdmin = false,
        OnboardingConsentsInput? consents = null)
    {
        await using var db = _kit.NewDb();
        return await _kit.Invitations(db).AcceptAsync(new AcceptOrgInvitation(
            token, userId, email, verified, platformAdmin, consents ?? OrgInvitationTestKit.Consents(), "203.0.113.7"));
    }

    /// <summary>An org with its owner, an invitation for Anna and Anna herself with no org.</summary>
    private async Task<(Guid OrgId, OrgInvitation Invitation, string Token)> OrgWithInvitationForNewUserAsync(
        OrgRole role = OrgRole.Collaborator,
        string[]? areas = null,
        PropertyScope scope = PropertyScope.All)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedNewUserAsync(AnnaId, AnnaEmail);
        var (invitation, token) = await _kit.SeedInvitationAsync(org.Id, AnnaEmail, role, areas: areas);
        if (scope != PropertyScope.All)
        {
            await using var db = _kit.NewDb();
            (await db.OrgInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == invitation.Id)).PropertyScope = scope;
            await db.SaveChangesAsync();
        }

        return (org.Id, invitation, token);
    }

    /// <summary>Nothing of the acceptance was written: the invitation is as it was and Anna is no member of the org.</summary>
    private async Task AssertNothingWrittenAsync(OrgInvitation invitation, Guid orgId, string userId = AnnaId)
    {
        var stored = await _kit.ReadInvitationAsync(invitation.Id);
        Assert.Equal(invitation.Status, stored.Status);
        Assert.Null(stored.AcceptedAt);
        Assert.Null(stored.AcceptedByUserId);
        var member = await _kit.ReadMemberAsync(userId);
        Assert.True(member is null || member.OrgId != orgId, "The refused person became a member.");
        await using var db = _kit.NewDb();
        Assert.False(await db.ConsentRecords.IgnoreQueryFilters().AnyAsync(c => c.UserId == userId && c.OrgId == orgId));
    }

    // ─── A person with no org ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptAsync_PersonWithoutOrg_BecomesMemberOfTheOrgWithTheInvitedRole()
    {
        var (orgId, invitation, token) = await OrgWithInvitationForNewUserAsync(OrgRole.Collaborator, ["short-rent"], PropertyScope.Selected);

        var accepted = await AcceptAsync(token);

        Assert.Equal((orgId, "Casa Rossi", OrgRole.Collaborator, false), (accepted.OrgId, accepted.OrgName, accepted.Role, accepted.LeftEmptyOrg));
        Assert.Equal(["short-rent"], accepted.Areas);

        var member = await _kit.ReadMemberAsync(AnnaId);
        Assert.NotNull(member);
        Assert.Equal((orgId, OrgRole.Collaborator, OrgMemberStatus.Active, PropertyScope.Selected), (member.OrgId, member.Role, member.Status, member.PropertyScope));
        Assert.Equal("auth0|owner", member.CreatedByUserId);
        Assert.Equal(["short-rent/staff"], await _kit.MembershipsAsync(AnnaId));

        var user = await _kit.ReadUserAsync(AnnaId);
        Assert.Equal(orgId, user.OrgId);
        Assert.Equal(_kit.Now, user.OnboardingCompletedAt);

        var stored = await _kit.ReadInvitationAsync(invitation.Id);
        Assert.Equal((OrgInvitationStatus.Accepted, _kit.Now, AnnaId, _kit.Now), (stored.Status, stored.AcceptedAt, stored.AcceptedByUserId, stored.ClosedAt));
        _kit.Cache.Verify(c => c.Invalidate(AnnaId), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AcceptAsync_RecordsTheFourConsentsOfTheOrgItJoinsWithTheClientIp_AndNoAuth0Role()
    {
        var (orgId, _, token) = await OrgWithInvitationForNewUserAsync();

        await AcceptAsync(token);

        await using var db = _kit.NewDb();
        var consents = await db.ConsentRecords.IgnoreQueryFilters().Where(c => c.UserId == AnnaId).ToListAsync();
        Assert.Equal(
            new[] { ConsentType.Tos, ConsentType.Privacy, ConsentType.Dpa, ConsentType.SubprocessorsAck }.Order(),
            consents.Select(c => c.Type).Order());
        Assert.All(consents, c =>
        {
            Assert.Equal(orgId, c.OrgId);
            Assert.Equal(OrgInvitationTestKit.ConsentVersion, c.Version);
            Assert.Equal("203.0.113.7", c.IpAddress);
        });
        // The people of an org have no Auth0 role: their rights are their memberships.
        _kit.Auth0.Verify(a => a.AssignRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
        _kit.Auth0.Verify(a => a.RemoveRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(OrgRole.Admin, new[] { "short-rent", "long-rent" }, new[] { "account/org_admin", "long-rent/property_manager", "short-rent/property_manager" })]
    [InlineData(OrgRole.PropertyManager, new[] { "long-rent" }, new[] { "long-rent/property_manager" })]
    [InlineData(OrgRole.Collaborator, new[] { "short-rent", "long-rent" }, new[] { "long-rent/staff", "short-rent/staff" })]
    [InlineData(OrgRole.Accountant, new[] { "short-rent" }, new[] { "account/org_accountant", "short-rent/accountant" })]
    public async Task AcceptAsync_EachRoleGetsTheMembershipsOfItsAreas(OrgRole role, string[] areas, string[] expected)
    {
        var (_, _, token) = await OrgWithInvitationForNewUserAsync(role, areas);

        var accepted = await AcceptAsync(token);

        Assert.Equal(role, accepted.Role);
        Assert.Equal(expected, await _kit.MembershipsAsync(AnnaId));
    }

    [Fact]
    public async Task AcceptAsync_TheEmailOfTheAccountIsCompared_IgnoringCaseAndSpaces()
    {
        var (_, _, token) = await OrgWithInvitationForNewUserAsync();

        var accepted = await AcceptAsync(token, email: "  ANNA.Leone@Example.COM ");

        Assert.Equal(OrgRole.Collaborator, accepted.Role);
    }

    [Fact]
    public async Task AcceptAsync_TheTokenWithCapitalsAndSpaces_IsTolerated()
    {
        var (_, _, token) = await OrgWithInvitationForNewUserAsync();

        await AcceptAsync($"  {token.ToUpperInvariant()} ");

        Assert.NotNull(await _kit.ReadMemberAsync(AnnaId));
    }

    // ─── Acceptance twice ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptAsync_TheSamePersonTwice_FindsItDoneAndWritesNothingMore()
    {
        var (orgId, _, token) = await OrgWithInvitationForNewUserAsync();
        var first = await AcceptAsync(token);

        var second = await AcceptAsync(token);

        Assert.Equal((first.OrgId, first.OrgName, first.Role, first.LeftEmptyOrg), (second.OrgId, second.OrgName, second.Role, second.LeftEmptyOrg));
        Assert.Equal(first.Areas, second.Areas);
        await using var db = _kit.NewDb();
        Assert.Equal(1, await db.OrgMembers.IgnoreQueryFilters().CountAsync(m => m.UserId == AnnaId));
        Assert.Equal(4, await db.ConsentRecords.IgnoreQueryFilters().CountAsync(c => c.UserId == AnnaId && c.OrgId == orgId));
    }

    [Fact]
    public async Task AcceptAsync_AnotherPersonWithTheUsedToken_IsGone()
    {
        var (_, _, token) = await OrgWithInvitationForNewUserAsync();
        await AcceptAsync(token);
        await _kit.SeedNewUserAsync("auth0|bruno", "bruno@example.com");

        var error = await Assert.ThrowsAsync<DomainGoneException>(() => AcceptAsync(token, "auth0|bruno", "bruno@example.com"));

        Assert.Equal(OrgInvitationErrors.Used, error.Code);
    }

    [Fact]
    public async Task AcceptAsync_AfterTheMemberWasRemoved_TheUsedLinkDoesNotBringItBack()
    {
        var (_, _, token) = await OrgWithInvitationForNewUserAsync();
        await AcceptAsync(token);
        await using (var db = _kit.NewDb())
            await _kit.Membership(db).RemoveAsync(AnnaId);

        var error = await Assert.ThrowsAsync<DomainGoneException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.Used, error.Code);
        Assert.Null(await _kit.ReadMemberAsync(AnnaId));
    }

    [Fact]
    public async Task AcceptAsync_ADeactivatedMember_DoesNotGetItsSuccessReplayed()
    {
        var (_, _, token) = await OrgWithInvitationForNewUserAsync();
        await AcceptAsync(token);
        await using (var db = _kit.NewDb())
            await _kit.Membership(db).DeactivateAsync(AnnaId);

        var error = await Assert.ThrowsAsync<DomainGoneException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.Used, error.Code);
    }

    // ─── The link ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("0123456789abcdef")]
    public async Task AcceptAsync_MalformedToken_IsInvalid(string token)
    {
        await OrgWithInvitationForNewUserAsync();

        var error = await Assert.ThrowsAsync<DomainGoneException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.Invalid, error.Code);
    }

    [Fact]
    public async Task AcceptAsync_UnknownToken_IsInvalid()
    {
        var (orgId, invitation, _) = await OrgWithInvitationForNewUserAsync();

        var error = await Assert.ThrowsAsync<DomainGoneException>(() => AcceptAsync(OrgInvitationTokens.Generate()));

        Assert.Equal(OrgInvitationErrors.Invalid, error.Code);
        await AssertNothingWrittenAsync(invitation, orgId);
    }

    [Fact]
    public async Task AcceptAsync_TheLinkOfTheFirstEmailAfterItWasSentAgain_IsInvalid()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedNewUserAsync(AnnaId, AnnaEmail);
        OrgInvitationSent created;
        await using (var db = _kit.NewDb())
            created = await _kit.Invitations(db).CreateAsync(new CreateOrgInvitation(org.Id, "auth0|owner", AnnaEmail, "Anna Leone", OrgRole.Collaborator, ["short-rent"]));
        var firstToken = OrgInvitationTestKit.TokenInEmail(Assert.Single(_kit.Emails.Snapshot()));
        await using (var db = _kit.NewDb())
            await _kit.Invitations(db).ResendAsync(org.Id, created.Invitation.Id, "auth0|owner");
        var secondToken = OrgInvitationTestKit.TokenInEmail(_kit.Emails.Snapshot()[1]);

        var error = await Assert.ThrowsAsync<DomainGoneException>(() => AcceptAsync(firstToken));
        var accepted = await AcceptAsync(secondToken);

        Assert.Equal(OrgInvitationErrors.Invalid, error.Code);
        Assert.Equal(org.Id, accepted.OrgId);
    }

    [Fact]
    public async Task AcceptAsync_ExpiredByTheClock_IsExpiredAndWritesNothing()
    {
        var (orgId, invitation, token) = await OrgWithInvitationForNewUserAsync();
        _kit.Clock.Advance(TimeSpan.FromDays(7));

        var error = await Assert.ThrowsAsync<DomainGoneException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.Expired, error.Code);
        await AssertNothingWrittenAsync(invitation, orgId);
    }

    [Fact]
    public async Task AcceptAsync_OneSecondBeforeTheExpiry_StillWorks()
    {
        var (_, _, token) = await OrgWithInvitationForNewUserAsync();
        _kit.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));

        var accepted = await AcceptAsync(token);

        Assert.Equal(OrgRole.Collaborator, accepted.Role);
    }

    [Fact]
    public async Task AcceptAsync_MarkedExpired_IsExpired()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedNewUserAsync(AnnaId, AnnaEmail);
        var (invitation, token) = await _kit.SeedInvitationAsync(org.Id, AnnaEmail, status: OrgInvitationStatus.Expired, closedAt: _kit.Now);

        var error = await Assert.ThrowsAsync<DomainGoneException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.Expired, error.Code);
        await AssertNothingWrittenAsync(invitation, org.Id);
    }

    [Fact]
    public async Task AcceptAsync_Revoked_IsRevoked()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedNewUserAsync(AnnaId, AnnaEmail);
        var (invitation, token) = await _kit.SeedInvitationAsync(org.Id, AnnaEmail, status: OrgInvitationStatus.Revoked, closedAt: _kit.Now);

        var error = await Assert.ThrowsAsync<DomainGoneException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.Revoked, error.Code);
        await AssertNothingWrittenAsync(invitation, org.Id);
    }

    [Fact]
    public async Task AcceptAsync_OrgDeactivatedMeanwhile_IsInvalid()
    {
        var (orgId, invitation, token) = await OrgWithInvitationForNewUserAsync();
        await using (var db = _kit.NewDb())
        {
            (await db.Orgs.SingleAsync(o => o.Id == orgId)).IsActive = false;
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<DomainGoneException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.Invalid, error.Code);
        await AssertNothingWrittenAsync(invitation, orgId);
    }

    // ─── The account ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptAsync_AnotherEmail_IsForbiddenAndWritesNothing()
    {
        var (orgId, invitation, token) = await OrgWithInvitationForNewUserAsync();
        await _kit.SeedNewUserAsync("auth0|bruno", "bruno@example.com");

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => AcceptAsync(token, "auth0|bruno", "bruno@example.com"));

        Assert.Equal(OrgInvitationErrors.EmailMismatch, error.Code);
        await AssertNothingWrittenAsync(invitation, orgId, "auth0|bruno");
    }

    [Theory]
    [InlineData("")]
    [InlineData("anna.leone+other@example.com")]
    [InlineData("anna.leone@example.org")]
    public async Task AcceptAsync_AlmostTheEmail_IsAMismatch(string email)
    {
        var (_, _, token) = await OrgWithInvitationForNewUserAsync();

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(() => AcceptAsync(token, email: email));

        Assert.Equal(OrgInvitationErrors.EmailMismatch, error.Code);
    }

    [Fact]
    public async Task AcceptAsync_EmailNotVerified_IsForbiddenEvenIfItIsTheInvitedOne()
    {
        var (orgId, invitation, token) = await OrgWithInvitationForNewUserAsync();

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(() => AcceptAsync(token, verified: false));

        Assert.Equal(OrgInvitationErrors.EmailNotVerified, error.Code);
        await AssertNothingWrittenAsync(invitation, orgId);
    }

    [Fact]
    public async Task AcceptAsync_APlatformAdminFromTheToken_IsForbidden()
    {
        var (orgId, invitation, token) = await OrgWithInvitationForNewUserAsync();

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(() => AcceptAsync(token, platformAdmin: true));

        Assert.Equal(OrgInvitationErrors.PlatformAdmin, error.Code);
        await AssertNothingWrittenAsync(invitation, orgId);
    }

    [Fact]
    public async Task AcceptAsync_APlatformAdminByTheDatabaseRole_IsForbidden()
    {
        var (orgId, invitation, token) = await OrgWithInvitationForNewUserAsync();
        await using (var db = _kit.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == AnnaId)).Role = UserRole.Admin;
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.PlatformAdmin, error.Code);
        await AssertNothingWrittenAsync(invitation, orgId);
    }

    [Fact]
    public async Task AcceptAsync_APlatformAdminByTheAdminContextMembership_IsForbidden()
    {
        var (orgId, invitation, token) = await OrgWithInvitationForNewUserAsync();
        await using (var db = _kit.NewDb())
        {
            OrgTeamTestData.AddMembership(db, AnnaId, "admin", "platform_admin");
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.PlatformAdmin, error.Code);
        await AssertNothingWrittenAsync(invitation, orgId);
    }

    [Fact]
    public async Task AcceptAsync_APlatformAdminWithAnOrgOfItsOwn_IsStillRefusedAndKeepsEverything()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (staffOrg, _) = await _kit.SeedPersonWithEmptyOrgAsync(AnnaId, AnnaEmail);
        await using (var db = _kit.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == AnnaId)).Role = UserRole.Admin;
            await db.SaveChangesAsync();
        }

        var (invitation, token) = await _kit.SeedInvitationAsync(org.Id, AnnaEmail);

        await Assert.ThrowsAsync<DomainForbiddenException>(() => AcceptAsync(token));

        // Not even an empty org of the staff account is touched.
        var member = await _kit.ReadMemberAsync(AnnaId);
        Assert.Equal(staffOrg.Id, member!.OrgId);
        await using var verify = _kit.NewDb();
        Assert.True((await verify.Orgs.AsNoTracking().SingleAsync(o => o.Id == staffOrg.Id)).IsActive);
        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(invitation.Id)).Status);
    }

    // ─── The consents ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptAsync_ConsentsOfAnOldVersion_AreRefusedBeforeAnythingIsWritten()
    {
        var (orgId, invitation, token) = await OrgWithInvitationForNewUserAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => AcceptAsync(token, consents: OrgInvitationTestKit.Consents(version: "2025-01-old")));

        Assert.Equal("stale_documents", error.Code);
        await AssertNothingWrittenAsync(invitation, orgId);
    }

    [Fact]
    public async Task AcceptAsync_ConsentsNotAccepted_AreRefusedBeforeAnythingIsWritten()
    {
        var (orgId, invitation, token) = await OrgWithInvitationForNewUserAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => AcceptAsync(token, consents: OrgInvitationTestKit.Consents(accepted: false)));

        Assert.Equal("consents_incomplete", error.Code);
        await AssertNothingWrittenAsync(invitation, orgId);
    }

    // ─── An org of its own: leave it if it is empty, refuse otherwise ──────────────────────────────────

    private async Task<(Guid TargetOrgId, OrgInvitation Invitation, string Token, Guid OldOrgId)> InvitationForPersonWithEmptyOrgAsync(
        OrgRole role = OrgRole.Collaborator)
    {
        var (target, _) = await _kit.SeedOwnerOrgAsync();
        var (oldOrg, _) = await _kit.SeedPersonWithEmptyOrgAsync(AnnaId, AnnaEmail);
        var (invitation, token) = await _kit.SeedInvitationAsync(target.Id, AnnaEmail, role);
        return (target.Id, invitation, token, oldOrg.Id);
    }

    [Fact]
    public async Task AcceptAsync_APersonWithAnEmptyOrgOfItsOwn_LeavesItAndJoinsTheOtherOne()
    {
        var (targetId, invitation, token, oldOrgId) = await InvitationForPersonWithEmptyOrgAsync();

        var accepted = await AcceptAsync(token);

        Assert.True(accepted.LeftEmptyOrg);
        Assert.Equal(targetId, accepted.OrgId);

        // The empty org is deactivated, not deleted; what was recorded for it (the consents) stays.
        await using var db = _kit.NewDb();
        var old = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == oldOrgId);
        Assert.False(old.IsActive);
        Assert.Equal(4, await db.ConsentRecords.IgnoreQueryFilters().CountAsync(c => c.UserId == AnnaId && c.OrgId == oldOrgId));
        Assert.Equal(4, await db.ConsentRecords.IgnoreQueryFilters().CountAsync(c => c.UserId == AnnaId && c.OrgId == targetId));
        Assert.Empty(await db.OrgMembers.IgnoreQueryFilters().Where(m => m.OrgId == oldOrgId).ToListAsync());

        // The person is a member of the new org only, with the memberships of its role and none of the old owner's.
        var member = await _kit.ReadMemberAsync(AnnaId);
        Assert.Equal((targetId, OrgRole.Collaborator), (member!.OrgId, member.Role));
        Assert.Equal(["short-rent/staff"], await _kit.MembershipsAsync(AnnaId));

        var user = await _kit.ReadUserAsync(AnnaId);
        Assert.Equal(targetId, user.OrgId);
        Assert.Equal(UserRole.None, user.Role);
        Assert.Null(user.RentalType);
        Assert.Null(user.LastUsedContextKey);
        Assert.Equal((OrgInvitationStatus.Accepted, AnnaId), ((await _kit.ReadInvitationAsync(invitation.Id)).Status, (await _kit.ReadInvitationAsync(invitation.Id)).AcceptedByUserId));
    }

    [Fact]
    public async Task AcceptAsync_LeavingAnEmptyOrg_TakesTheOwnerRolesOfTheOnboardingOffAuth0()
    {
        var (_, _, token, _) = await InvitationForPersonWithEmptyOrgAsync();

        await AcceptAsync(token);

        _kit.Auth0.Verify(
            a => a.RemoveRolesAsync(
                AnnaId,
                It.Is<IReadOnlyCollection<UserRole>>(roles => roles.Order().SequenceEqual(new[] { UserRole.PropertyOwner, UserRole.LongTermLandlord })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AcceptAsync_Auth0FailsToRemoveTheRoles_TheAcceptanceStandsAndTheBillingStaysClosed()
    {
        var (targetId, _, token, _) = await InvitationForPersonWithEmptyOrgAsync();
        _kit.Auth0
            .Setup(a => a.RemoveRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Failed(Auth0SyncResult.ApiErrorCode));

        var accepted = await AcceptAsync(token);

        Assert.Equal(targetId, accepted.OrgId);
        Assert.NotNull(await _kit.ReadMemberAsync(AnnaId));
    }

    [Fact]
    public async Task AcceptAsync_Auth0Throws_TheAcceptanceStands()
    {
        var (targetId, _, token, _) = await InvitationForPersonWithEmptyOrgAsync();
        _kit.Auth0
            .Setup(a => a.RemoveRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("auth0 is down"));

        var accepted = await AcceptAsync(token);

        Assert.Equal(targetId, accepted.OrgId);
    }

    [Fact]
    public async Task AcceptAsync_ALegacyOwnerWithoutAMemberRowAndAnEmptyOrg_LeavesItToo()
    {
        var (target, _) = await _kit.SeedOwnerOrgAsync();
        var (oldOrg, _) = await _kit.SeedPersonWithEmptyOrgAsync(AnnaId, AnnaEmail);
        await using (var db = _kit.NewDb())
        {
            db.OrgMembers.RemoveRange(await db.OrgMembers.IgnoreQueryFilters().Where(m => m.UserId == AnnaId).ToListAsync());
            await db.SaveChangesAsync();
        }

        var (_, token) = await _kit.SeedInvitationAsync(target.Id, AnnaEmail);

        var accepted = await AcceptAsync(token);

        Assert.True(accepted.LeftEmptyOrg);
        await using var verify = _kit.NewDb();
        Assert.False((await verify.Orgs.AsNoTracking().SingleAsync(o => o.Id == oldOrg.Id)).IsActive);
        Assert.Equal(target.Id, (await _kit.ReadMemberAsync(AnnaId))!.OrgId);
    }

    /// <summary>What makes the org of the person "in use": each of these makes the acceptance a 409 and changes nothing.</summary>
    public static TheoryData<string> OrgInUse => new()
    {
        "property",
        "deleted-property",
        "another-user",
        "another-member",
        "pro-plan",
        "stripe-customer",
        "subscription",
        "connect-account",
        "custom-domain",
        "subdomain",
        "branding",
        "invoice",
        "invitation",
    };

    [Theory]
    [MemberData(nameof(OrgInUse))]
    public async Task AcceptAsync_AnOrgThatIsInUse_IsNotLeft_409AndNothingChanges(string reason)
    {
        var (targetId, invitation, token, oldOrgId) = await InvitationForPersonWithEmptyOrgAsync();
        await MakeOrgUsedAsync(oldOrgId, reason);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.UserHasOrganization, error.Code);
        await AssertNothingWrittenAsync(invitation, targetId);
        var member = await _kit.ReadMemberAsync(AnnaId);
        Assert.Equal((oldOrgId, OrgRole.Owner), (member!.OrgId, member.Role));
        await using var db = _kit.NewDb();
        Assert.True((await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == oldOrgId)).IsActive);
        Assert.Equal(oldOrgId, (await _kit.ReadUserAsync(AnnaId)).OrgId);
        Assert.Contains("short-rent/property_owner", await _kit.MembershipsAsync(AnnaId));
        _kit.Auth0.Verify(a => a.RemoveRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private async Task MakeOrgUsedAsync(Guid orgId, string reason)
    {
        await using var db = _kit.NewDb();
        var org = await db.Orgs.SingleAsync(o => o.Id == orgId);
        switch (reason)
        {
            case "property":
                db.Properties.Add(new Property { OwnerId = AnnaId, OrgId = orgId, Name = "Casa", Address = "Via Roma 1", City = "Lecce" });
                break;
            case "deleted-property":
                db.Properties.Add(new Property { OwnerId = AnnaId, OrgId = orgId, Name = "Casa", Address = "Via Roma 1", City = "Lecce", IsDeleted = true });
                break;
            case "another-user":
                OrgTeamTestData.AddUser(db, "auth0|colleague", orgId);
                break;
            case "another-member":
                OrgTeamTestData.AddUser(db, "auth0|colleague", orgId: null);
                OrgTeamTestData.AddMember(db, "auth0|colleague", orgId, OrgRole.Collaborator);
                break;
            case "pro-plan":
                org.PlanTier = PlanTier.Pro;
                break;
            case "stripe-customer":
                org.StripeCustomerId = "cus_123";
                break;
            case "subscription":
                org.SubscriptionStatus = SubscriptionStatus.Canceled;
                break;
            case "connect-account":
                org.StripeConnectedAccountId = "acct_123";
                break;
            case "custom-domain":
                org.CustomDomain = "prenota.example.com";
                break;
            case "subdomain":
                org.Subdomain = "annaleone";
                break;
            case "branding":
                org.Tagline = "Case al mare";
                break;
            case "invoice":
                db.PlatformInvoices.Add(new PlatformInvoice { OrgId = orgId });
                break;
            case "invitation":
                db.OrgInvitations.Add(new OrgInvitation
                {
                    OrgId = orgId,
                    Email = "someone@example.com",
                    Name = "Someone",
                    TokenHash = OrgInvitationTokens.Hash(OrgInvitationTokens.Generate()),
                    Areas = ["short-rent"],
                    InvitedByUserId = AnnaId,
                    ExpiresAt = _kit.Now.AddDays(7),
                    Status = OrgInvitationStatus.Revoked,
                    ClosedAt = _kit.Now,
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, null);
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AcceptAsync_WhatTheOnboardingLeavesBehind_DoesNotMakeAnOrgUsed()
    {
        // Consents, the signup attribution and a previous slug are what an untouched org holds.
        var (targetId, _, token, oldOrgId) = await InvitationForPersonWithEmptyOrgAsync();
        await using (var db = _kit.NewDb())
        {
            db.SignupAttributions.Add(new SignupAttribution { OrgId = oldOrgId, UtmSource = "google" });
            db.OrgSlugAliases.Add(new OrgSlugAlias { Slug = $"old-{Guid.NewGuid():N}", OrgId = oldOrgId, CreatedAt = _kit.Now });
            await db.SaveChangesAsync();
        }

        var accepted = await AcceptAsync(token);

        Assert.True(accepted.LeftEmptyOrg);
        Assert.Equal(targetId, accepted.OrgId);
    }

    [Fact]
    public async Task AcceptAsync_ThePropertyOfAnotherOrgDoesNotMakeThisOneUsed()
    {
        var (targetId, _, token, _) = await InvitationForPersonWithEmptyOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        await using (var db = _kit.NewDb())
        {
            db.Properties.Add(new Property { OwnerId = "auth0|other-owner", OrgId = other.Id, Name = "Casa", Address = "Via Roma 1", City = "Lecce" });
            await db.SaveChangesAsync();
        }

        var accepted = await AcceptAsync(token);

        Assert.True(accepted.LeftEmptyOrg);
        Assert.Equal(targetId, accepted.OrgId);
    }

    // ─── A member of another org, or already of this one ───────────────────────────────────────────────

    [Theory]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Collaborator)]
    [InlineData(OrgRole.Accountant)]
    public async Task AcceptAsync_AMemberOfAnotherOrg_IsRefusedWhateverItsRole(OrgRole role)
    {
        var (target, _) = await _kit.SeedOwnerOrgAsync();
        var (elsewhere, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-elsewhere");
        await _kit.SeedMemberAsync(elsewhere.Id, AnnaId, role, email: AnnaEmail);
        var (invitation, token) = await _kit.SeedInvitationAsync(target.Id, AnnaEmail);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => AcceptAsync(token));

        Assert.Equal(OrgInvitationErrors.UserHasOrganization, error.Code);
        await AssertNothingWrittenAsync(invitation, target.Id);
        Assert.Equal(elsewhere.Id, (await _kit.ReadMemberAsync(AnnaId))!.OrgId);
    }

    [Fact]
    public async Task AcceptAsync_TheOwnerOfAnotherOrgThatIsInUse_IsRefused()
    {
        var (target, _) = await _kit.SeedOwnerOrgAsync();
        var (elsewhere, _) = await _kit.SeedOwnerOrgAsync(AnnaId);
        await using (var db = _kit.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == AnnaId)).Email = AnnaEmail;
            await db.SaveChangesAsync();
        }

        var (invitation, token) = await _kit.SeedInvitationAsync(target.Id, AnnaEmail);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => AcceptAsync(token));

        // SeedOwnerOrgAsync gives the org a Pro subscription: paid, so it stays where it is.
        Assert.Equal(OrgInvitationErrors.UserHasOrganization, error.Code);
        await AssertNothingWrittenAsync(invitation, target.Id);
        Assert.Equal(elsewhere.Id, (await _kit.ReadMemberAsync(AnnaId))!.OrgId);
    }

    [Fact]
    public async Task AcceptAsync_AlreadyAMemberOfThisOrgWithAnotherInvitation_IsAConflict()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, AnnaId, OrgRole.Collaborator, email: AnnaEmail);
        var (invitation, token) = await _kit.SeedInvitationAsync(org.Id, AnnaEmail, OrgRole.Admin);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => AcceptAsync(token));

        Assert.Equal(OrgMembershipErrors.AlreadyMember, error.Code);
        Assert.Equal(OrgRole.Collaborator, (await _kit.ReadMemberAsync(AnnaId))!.Role);
        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(invitation.Id)).Status);
    }

    [Theory]
    [InlineData(OrgMembershipErrors.AlreadyMember)]
    [InlineData(OrgMembershipErrors.OtherOrg)]
    public async Task AcceptAsync_AnotherOrgGotThePersonFirst_IsTheConflictOfAPersonWhoHasAnOrg_AndTheInvitationStaysOpen(string raceCode)
    {
        // Two orgs invite the same person and it accepts both at once: the unique index of the member row lets one through
        // and the membership service tells the other that the person is taken (proved on PostgreSQL). The loser gets the
        // answer of a person who has an org, not a code of the membership service.
        var (orgId, invitation, token) = await OrgWithInvitationForNewUserAsync();
        var membership = new Mock<IOrgMembershipService>();
        membership
            .Setup(m => m.AddMemberAsync(
                AnnaId,
                orgId,
                It.IsAny<OrgRole>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<string?>(),
                It.IsAny<PropertyScope>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainConflictException(raceCode, "OrgMemberAlreadyMember"));

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainConflictException>(() => _kit.Invitations(db, membership.Object).AcceptAsync(
            new AcceptOrgInvitation(token, AnnaId, AnnaEmail, true, false, OrgInvitationTestKit.Consents(), null)));

        Assert.Equal(OrgInvitationErrors.UserHasOrganization, error.Code);
        await AssertNothingWrittenAsync(invitation, orgId);
    }

    // ─── Legacy links ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptAsync_AccountLinkedToItsSupplierOrgAsOrgId_JoinsAndKeepsTheSupplierLink()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        Guid supplierOrgId;
        await using (var db = _kit.NewDb())
        {
            var supplier = OrgTeamTestData.AddOrg(db, OrgType.Supplier);
            supplierOrgId = supplier.Id;
            var anna = OrgTeamTestData.AddUser(db, AnnaId, supplier.Id);
            anna.Email = AnnaEmail;
            await db.SaveChangesAsync();
        }

        var (_, token) = await _kit.SeedInvitationAsync(org.Id, AnnaEmail);

        var accepted = await AcceptAsync(token);

        Assert.False(accepted.LeftEmptyOrg);
        var user = await _kit.ReadUserAsync(AnnaId);
        Assert.Equal((org.Id, supplierOrgId), (user.OrgId, user.SupplierOrgId));
        await using var verify = _kit.NewDb();
        Assert.True((await verify.Orgs.AsNoTracking().SingleAsync(o => o.Id == supplierOrgId)).IsActive);
    }

    // ─── Seats ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptAsync_ChangesNothingInTheSeatCount_ThePendingInvitationAlreadyHeldItsSeat()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        await _kit.SeedNewUserAsync(AnnaId, AnnaEmail);
        var (_, token) = await _kit.SeedInvitationAsync(org.Id, AnnaEmail);
        await using (var db = _kit.NewDb())
        {
            var before = await _kit.Seats(db).GetUsageAsync(org.Id);
            Assert.Equal((2, 1, 1), (before.Used, before.ActiveMembers, before.PendingInvitations));
        }

        await AcceptAsync(token);

        // The owner and Anna now: the pending invitation became the member that takes the same seat.
        await using var after = _kit.NewDb();
        var usage = await _kit.Seats(after).GetUsageAsync(org.Id);
        Assert.Equal((2, 2, 0), (usage.Used, usage.ActiveMembers, usage.PendingInvitations));
        Assert.Equal(2, usage.Max);
    }

    [Fact]
    public async Task AcceptAsync_AfterADowngradeThePendingInvitationStillJoins_TheOrgIsOverItsSeatsAndStaysSo()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro, status: SubscriptionStatus.Unpaid);
        await _kit.SeedMemberAsync(org.Id, "auth0|m1", OrgRole.Collaborator);
        await _kit.SeedMemberAsync(org.Id, "auth0|m2", OrgRole.Collaborator);
        await _kit.SeedNewUserAsync(AnnaId, AnnaEmail);
        var (_, token) = await _kit.SeedInvitationAsync(org.Id, AnnaEmail);

        var accepted = await AcceptAsync(token);

        Assert.Equal(org.Id, accepted.OrgId);
        await using var db = _kit.NewDb();
        var usage = await _kit.Seats(db).GetUsageAsync(org.Id);
        Assert.Equal((2, 4), (usage.Max, usage.Used));
        Assert.False(usage.CanInvite);
    }

    // ─── The tenant of the request ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptAsync_WithTheTenantFilterOnForAnotherOrg_StillReadsAndWritesTheInvitationsOrg()
    {
        var (orgId, _, token) = await OrgWithInvitationForNewUserAsync();
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_kit.Database).Options,
            new FixedTenantContext(Guid.NewGuid(), filterEnabled: true));

        var accepted = await _kit.Invitations(db).AcceptAsync(new AcceptOrgInvitation(
            token, AnnaId, AnnaEmail, true, false, OrgInvitationTestKit.Consents(), null));

        Assert.Equal(orgId, accepted.OrgId);
        Assert.Equal(orgId, (await _kit.ReadMemberAsync(AnnaId))!.OrgId);
    }

    private sealed class FixedTenantContext(Guid? orgId, bool filterEnabled) : Casazen.Core.Multitenancy.ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled { get; } = filterEnabled;
    }
}
