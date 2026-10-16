using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02c: the overview of the org for the home of the account (<c>GET /api/orgs/me/overview</c>). The standing of the plan, the
/// seats, the properties (by mode and still to complete, inside what the caller reaches), the open invitations and how long the
/// oldest has waited, the requests and the suppliers that are zero until their tasks exist, the short-term properties without
/// Alloggiati credentials, and the latest three things people did; a section whose permission the caller lacks is left out. The
/// real service over EF InMemory; the number of statements is proved on PostgreSQL by <c>OrgTeamCompletionsPostgresTests</c> (CI).
/// </summary>
public class OrgOverviewServiceTests
{
    private const string OwnerId = "auth0|owner";

    private static readonly IReadOnlySet<string> Everything = new HashSet<string>(OrgOverviewSections.All, StringComparer.Ordinal);

    private readonly OrgInvitationTestKit _kit = new();

    private async Task<OrgOverview> GetAsync(Guid orgId, HostScope? scope = null, IReadOnlySet<string>? permissions = null)
    {
        await using var db = _kit.NewDb();
        return await _kit.Overview(db).GetAsync(orgId, scope ?? new HostScope(orgId), permissions ?? Everything);
    }

    private async Task<Guid> SeedOrgAsync(PlanTier tier = PlanTier.Pro, SubscriptionStatus status = SubscriptionStatus.Active)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(OwnerId, tier, status);
        return org.Id;
    }

    // ─── The properties ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Four short-term properties (to complete: the first and the third), two long-term, one deleted, and one of another org.</summary>
    private async Task<(Guid Pending, Guid Active, Guid Suspended, Guid Inactive, Guid LongOne, Guid LongTwo)> SeedPropertiesAsync(Guid orgId)
    {
        var pending = await _kit.SeedPropertyAsync(orgId, "In attesa", compliance: PropertyComplianceStatus.Pending);
        var active = await _kit.SeedPropertyAsync(orgId, "Pubblicata", compliance: PropertyComplianceStatus.Active);
        var suspended = await _kit.SeedPropertyAsync(orgId, "Sospesa", compliance: PropertyComplianceStatus.Suspended);
        var inactive = await _kit.SeedPropertyAsync(orgId, "Disattivata", compliance: PropertyComplianceStatus.Pending);
        var longOne = await _kit.SeedPropertyAsync(orgId, "Bilocale", RentalMode.Long, compliance: PropertyComplianceStatus.Pending);
        var longTwo = await _kit.SeedPropertyAsync(orgId, "Trilocale", RentalMode.Long, compliance: PropertyComplianceStatus.Pending);
        await _kit.SeedPropertyAsync(orgId, "Demolita", deleted: true);

        await using var db = _kit.NewDb();
        (await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == inactive)).IsActive = false;
        await db.SaveChangesAsync();
        return (pending, active, suspended, inactive, longOne, longTwo);
    }

    [Fact]
    public async Task Properties_AreCountedByModeAndWhatIsStillToComplete()
    {
        var orgId = await SeedOrgAsync();
        await SeedPropertiesAsync(orgId);

        var overview = await GetAsync(orgId);

        // Six live properties (a deleted one is not one). To complete: short-term, active and not yet able to take bookings: the
        // pending and the suspended; a long-term property has no listing to activate, and an inactive one is not «to complete».
        Assert.Equal(new OrgOverviewProperties(Total: 6, Short: 4, Long: 2, ToComplete: 2), overview.Properties);
    }

    [Fact]
    public async Task Properties_NeverCountThoseOfAnotherOrg()
    {
        var orgId = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        await _kit.SeedPropertyAsync(orgId, "Mia");
        await _kit.SeedPropertyAsync(other.Id, "Altrui", ownerId: "auth0|other-owner");

        Assert.Equal(1, (await GetAsync(orgId)).Properties!.Total);
        Assert.Equal(1, (await GetAsync(other.Id, new HostScope(other.Id))).Properties!.Total);
    }

    [Fact]
    public async Task Properties_ACollaboratorSoloAlcuniScope_CountsOnlyWhatItReaches()
    {
        var orgId = await SeedOrgAsync();
        var (pending, _, _, _, longOne, _) = await SeedPropertiesAsync(orgId);
        await _kit.SeedMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, ["short-rent", "long-rent"]);
        var memberId = (await _kit.ReadMemberAsync("auth0|anna"))!.Id;
        await using (var db = _kit.NewDb())
            await _kit.PropertyAccess(db).SetAsync(orgId, memberId, OwnerId, PropertyScope.Selected, [pending, longOne]);

        var granted = await GetAsync(orgId, new HostScope(orgId, GrantedToUserId: "auth0|anna"));
        // Working in both areas: one short-term (to complete) and one long-term.
        Assert.Equal(new OrgOverviewProperties(Total: 2, Short: 1, Long: 1, ToComplete: 1), granted.Properties);

        // Working in the short-term area only: the long-term grant stays and is not reached.
        var shortOnly = await GetAsync(orgId, new HostScope(orgId, GrantedToUserId: "auth0|anna", GrantedMode: RentalMode.Short));
        Assert.Equal(new OrgOverviewProperties(Total: 1, Short: 1, Long: 0, ToComplete: 1), shortOnly.Properties);
    }

    [Fact]
    public async Task Properties_AnOrgWithNone_IsAllZeros()
    {
        var overview = await GetAsync(await SeedOrgAsync());

        Assert.Equal(new OrgOverviewProperties(0, 0, 0, 0), overview.Properties);
        Assert.Equal(new OrgOverviewAlloggiati(0), overview.Alloggiati);
    }

    // ─── Alloggiati ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Alloggiati_CountsTheShortTermPropertiesWithoutCredentials_AndNotTheLongTermOnes()
    {
        var orgId = await SeedOrgAsync();
        var (pending, active, _, _, _, _) = await SeedPropertiesAsync(orgId);
        await using (var db = _kit.NewDb())
        {
            db.PropertyQuesturaCredentials.Add(new PropertyQuesturaCredentials
            {
                OrgId = orgId,
                PropertyId = active,
                Username = "utente",
                Password = "segreta",
                WsKey = "chiave",
            });
            await db.SaveChangesAsync();
        }

        var overview = await GetAsync(orgId);

        // Four short-term properties, one with credentials; the two long-term ones need none, the deleted one is not counted.
        Assert.Equal(3, overview.Alloggiati!.PropertiesWithoutCredentials);
        Assert.NotEqual(Guid.Empty, pending);
    }

    [Fact]
    public async Task Alloggiati_IsCountedInsideTheScopeOfTheCaller()
    {
        var orgId = await SeedOrgAsync();
        var (pending, _, _, _, longOne, _) = await SeedPropertiesAsync(orgId);
        await _kit.SeedMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, ["short-rent", "long-rent"]);
        var memberId = (await _kit.ReadMemberAsync("auth0|anna"))!.Id;
        await using (var db = _kit.NewDb())
            await _kit.PropertyAccess(db).SetAsync(orgId, memberId, OwnerId, PropertyScope.Selected, [pending, longOne]);

        var overview = await GetAsync(orgId, new HostScope(orgId, GrantedToUserId: "auth0|anna"));

        Assert.Equal(1, overview.Alloggiati!.PropertiesWithoutCredentials);
    }

    // ─── The plan ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SubscriptionStatus.Active, "ok")]
    [InlineData(SubscriptionStatus.Trialing, "ok")]
    [InlineData(SubscriptionStatus.None, "ok")]
    [InlineData(SubscriptionStatus.Canceled, "ok")]
    [InlineData(SubscriptionStatus.PastDue, "attention")]
    [InlineData(SubscriptionStatus.Unpaid, "attention")]
    [InlineData(SubscriptionStatus.Incomplete, "attention")]
    public async Task Standing_FollowsTheStateOfTheSubscription(SubscriptionStatus status, string expected)
    {
        var overview = await GetAsync(await SeedOrgAsync(status: status));

        Assert.Equal(expected, overview.Standing);
    }

    [Fact]
    public async Task OpenAccess_IsTheFlagOfTheEntitlement()
    {
        var orgId = await SeedOrgAsync(PlanTier.Starter, SubscriptionStatus.None);
        Assert.False((await GetAsync(orgId)).OpenAccess);

        _kit.Settings["Entitlement:OpenAccess:Enabled"] = "true";
        _kit.Settings["Entitlement:OpenAccess:Tier"] = "Pro";

        Assert.True((await GetAsync(orgId)).OpenAccess);
    }

    // ─── People ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Seats_AreTheUsedAndTheMaxOfThePlan()
    {
        var orgId = await SeedOrgAsync();
        await _kit.SeedMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        await _kit.SeedMemberAsync(orgId, "auth0|bruno", OrgRole.Accountant, status: OrgMemberStatus.Deactivated);
        await _kit.SeedInvitationAsync(orgId, "invitata@example.com");

        var seats = (await GetAsync(orgId)).Seats!;

        // The owner, one active member and one open invitation; a deactivated member holds no seat. Pro: ten.
        Assert.Equal(new OrgOverviewSeats(Used: 3, Max: 10, Unlimited: false), seats);
    }

    [Fact]
    public async Task Seats_AnUnlimitedPlan_SaysSo()
    {
        var seats = (await GetAsync(await SeedOrgAsync(PlanTier.Scale))).Seats!;

        Assert.True(seats.Unlimited);
        Assert.Equal(int.MaxValue, seats.Max);
    }

    [Fact]
    public async Task Invitations_AreTheOpenOnes_AndTheOldestWhole_DaysItHasWaited()
    {
        var orgId = await SeedOrgAsync();
        var (oldest, _) = await _kit.SeedInvitationAsync(orgId, "vecchia@example.com");
        await _kit.SeedInvitationAsync(orgId, "recente@example.com");
        await _kit.SeedInvitationAsync(orgId, "scaduta@example.com", status: OrgInvitationStatus.Expired, closedAt: _kit.Now, expiresAt: _kit.Now.AddDays(-1));
        await _kit.SeedInvitationAsync(orgId, "scaduta-nel-tempo@example.com", expiresAt: _kit.Now.AddDays(-1));
        await _kit.SeedInvitationAsync(orgId, "revocata@example.com", status: OrgInvitationStatus.Revoked, closedAt: _kit.Now);
        await using (var db = _kit.NewDb())
        {
            (await db.OrgInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == oldest.Id)).CreatedAt = _kit.Now.AddDays(-5).AddHours(-3);
            await db.SaveChangesAsync();
        }

        var invitations = (await GetAsync(orgId)).Invitations!;

        // Two open (the expired ones and the revoked one do not wait for anybody); the oldest has waited five whole days.
        Assert.Equal(new OrgOverviewInvitations(Pending: 2, OldestPendingDays: 5), invitations);
    }

    [Fact]
    public async Task Invitations_NoneOpen_HaveNoOldest()
    {
        Assert.Equal(new OrgOverviewInvitations(0, null), (await GetAsync(await SeedOrgAsync())).Invitations);
    }

    [Fact]
    public async Task Invitations_NeverCountThoseOfAnotherOrg()
    {
        var orgId = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        await _kit.SeedInvitationAsync(other.Id, "altrui@example.com", invitedBy: "auth0|other-owner");

        Assert.Equal(0, (await GetAsync(orgId)).Invitations!.Pending);
    }

    [Fact]
    public async Task AccessRequestsAndSuppliers_AreZeroUntilTheirTasksExist_ButTheFieldsAreThere()
    {
        var overview = await GetAsync(await SeedOrgAsync());

        Assert.Equal(new OrgOverviewAccessRequests(0), overview.AccessRequests);
        Assert.Equal(new OrgOverviewSuppliers(0), overview.Suppliers);
    }

    // ─── The latest activity of people ──────────────────────────────────────────────────────────────────

    private async Task RecordAsync(Guid orgId, OrgActivityType type, string? actor, string subject, params (string, string)[] details)
    {
        await using var db = _kit.NewDb();
        _kit.Activity(db).Record(OrgActivity.Of(orgId, type, actor, subject, details));
        await db.SaveChangesAsync();
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RecentActivity_IsTheLastThreeOfPeople_NewestFirst_WithoutTheSystem()
    {
        var orgId = await SeedOrgAsync();
        await RecordAsync(orgId, OrgActivityType.MemberInvited, OwnerId, "invito-1", (OrgActivityDetailKeys.Role, "Collaborator"));
        await RecordAsync(orgId, OrgActivityType.PlanChanged, null, orgId.ToString(), (OrgActivityDetailKeys.Source, "subscription"));
        await RecordAsync(orgId, OrgActivityType.MemberRoleChanged, OwnerId, "auth0|anna", (OrgActivityDetailKeys.FromRole, "Collaborator"), (OrgActivityDetailKeys.ToRole, "Admin"));
        await RecordAsync(orgId, OrgActivityType.MemberDeactivated, OwnerId, "auth0|bruno", (OrgActivityDetailKeys.Role, "Accountant"));
        await RecordAsync(orgId, OrgActivityType.PropertyModeChanged, null, Guid.NewGuid().ToString(), (OrgActivityDetailKeys.FromMode, "Short"), (OrgActivityDetailKeys.ToMode, "Long"));
        await RecordAsync(orgId, OrgActivityType.MemberReactivated, "auth0|admin", "auth0|bruno", (OrgActivityDetailKeys.Role, "Accountant"));

        var recent = (await GetAsync(orgId)).RecentActivity!;

        Assert.Equal(
            [OrgActivityType.MemberReactivated, OrgActivityType.MemberDeactivated, OrgActivityType.MemberRoleChanged],
            recent.Select(r => r.Type));
        Assert.All(recent, r => Assert.NotNull(r.ActorUserId));
        Assert.Equal("auth0|admin", recent[0].ActorUserId);
        Assert.Equal("auth0|bruno", recent[0].SubjectId);
    }

    [Fact]
    public async Task RecentActivity_NothingYet_IsAnEmptyList_NotMissing()
    {
        var recent = (await GetAsync(await SeedOrgAsync())).RecentActivity;

        Assert.NotNull(recent);
        Assert.Empty(recent);
    }

    [Fact]
    public async Task RecentActivity_NeverShowsTheLinesOfAnotherOrg()
    {
        var orgId = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        await RecordAsync(other.Id, OrgActivityType.MemberInvited, "auth0|other-owner", "invito-x", (OrgActivityDetailKeys.Role, "Collaborator"));

        Assert.Empty((await GetAsync(orgId)).RecentActivity!);
    }

    // ─── Sections the caller may not see ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EverySectionIsThere_ForTheCallerWhoHoldsEveryPermission()
    {
        var overview = await GetAsync(await SeedOrgAsync());

        Assert.NotNull(overview.Standing);
        Assert.NotNull(overview.OpenAccess);
        Assert.NotNull(overview.Seats);
        Assert.NotNull(overview.Properties);
        Assert.NotNull(overview.Invitations);
        Assert.NotNull(overview.AccessRequests);
        Assert.NotNull(overview.Suppliers);
        Assert.NotNull(overview.Alloggiati);
        Assert.NotNull(overview.RecentActivity);
    }

    [Fact]
    public async Task ASectionWhoseOwnPermissionIsMissing_IsLeftOut()
    {
        var orgId = await SeedOrgAsync();

        var peopleOnly = await GetAsync(orgId, permissions: new HashSet<string> { AccountContext.Permissions.MembersManage });
        Assert.Null(peopleOnly.Standing);
        Assert.Null(peopleOnly.OpenAccess);
        Assert.Null(peopleOnly.Suppliers);
        Assert.Null(peopleOnly.RecentActivity);
        Assert.NotNull(peopleOnly.Seats);
        Assert.NotNull(peopleOnly.Properties);
        Assert.NotNull(peopleOnly.Invitations);
        Assert.NotNull(peopleOnly.AccessRequests);
        Assert.NotNull(peopleOnly.Alloggiati);

        var billingOnly = await GetAsync(orgId, permissions: new HashSet<string> { AccountContext.Permissions.BillingRead });
        Assert.NotNull(billingOnly.Standing);
        Assert.NotNull(billingOnly.OpenAccess);
        Assert.Null(billingOnly.Seats);
        Assert.Null(billingOnly.Properties);
        Assert.Null(billingOnly.Invitations);
        Assert.Null(billingOnly.AccessRequests);
        Assert.Null(billingOnly.Alloggiati);

        var none = await GetAsync(orgId, permissions: new HashSet<string>());
        Assert.Equal(new OrgOverview(null, null, null, null, null, null, null, null, null), none);
    }

    [Fact]
    public void TheSections_AreBehindThePermissionsOfTheAccountContext()
    {
        Assert.Equal("org.billing.read", OrgOverviewSections.Billing);
        Assert.Equal("org.members.manage", OrgOverviewSections.People);
        Assert.Equal("org.suppliers.manage", OrgOverviewSections.Suppliers);
        Assert.Equal("org.activity.read", OrgOverviewSections.Activity);
        // The owner and the administrators hold every one of them (the seeded roles), the other roles none but their own.
        var owner = OrgRoleCatalog.SeededRoles.Single(r => r.RoleKey == AccountContext.RoleKeys.Owner).Permissions;
        Assert.All(OrgOverviewSections.All, permission => Assert.Contains(permission, owner));
    }
}
