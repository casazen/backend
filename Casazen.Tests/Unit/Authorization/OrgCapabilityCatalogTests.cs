using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// AM-02c: the matrix of the roles (<see cref="OrgCapabilityCatalog"/>, <see cref="OrgRoleMatrix"/>). The levels are read
/// from the permissions of the roles the model seeds (what the migrations leave in the database) and checked three ways: against
/// the table of the analysis (§2.3, written by hand), against the actions of the API (a role that has «Yes» on a capability can
/// call the action that writes it, one that has «Read» can only call the one that reads it), and as a tripwire (a permission
/// that a seeded role holds and the matrix does not know fails the test).
/// </summary>
public class OrgCapabilityCatalogTests
{
    private static readonly string[] CapabilityIds =
    [
        "booking", "prices", "guests", "money", "leases", "rents", "jobs", "people", "billing", "suppliers", "settings", "security",
    ];

    /// <summary>The permissions of every seeded role, by context and role key (the model's seed data).</summary>
    private static readonly Lazy<IReadOnlyDictionary<(string ContextKey, string RoleKey), IReadOnlyCollection<string>>> Seeded = new(() =>
    {
        using var db = OrgTeamTestData.NewDb();
        return db.RolePermissions.Include(rp => rp.Role).AsEnumerable()
            .GroupBy(rp => (rp.Role.ContextKey, rp.Role.RoleKey))
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(rp => rp.PermissionKey).ToList());
    });

    private static OrgRoleView RoleOf(OrgRole role, OrgRole caller = OrgRole.Owner) =>
        OrgRoleMatrix.Build(Seeded.Value, caller).Single(r => r.Role == role);

    // ─── The twelve capabilities ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_TheTwelveCapabilities_HaveStableIdsInTheOrderOfTheScreen()
    {
        Assert.Equal(CapabilityIds, OrgCapabilityCatalog.All.Select(c => c.Id));
        Assert.Equal(12, OrgCapabilityCatalog.All.Select(c => c.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("booking", "short-rent")]
    [InlineData("prices", "short-rent")]
    [InlineData("guests", "short-rent")]
    [InlineData("money", "short-rent")]
    [InlineData("leases", "long-rent")]
    [InlineData("rents", "long-rent")]
    [InlineData("jobs", "both")]
    [InlineData("people", "account")]
    [InlineData("billing", "account")]
    [InlineData("suppliers", "account")]
    [InlineData("settings", "account")]
    [InlineData("security", "account")]
    public void Catalog_EveryCapability_BelongsToItsGroup(string id, string group) =>
        Assert.Equal(group, OrgCapabilityCatalog.All.Single(c => c.Id == id).Group);

    // ─── The table of the analysis (§2.3), by hand ──────────────────────────────────────────────────────

    /// <summary>The levels of each role, in the order of <see cref="CapabilityIds"/>: Y = Yes, R = Read, N = No.</summary>
    public static TheoryData<OrgRole, string> ExpectedLevels => new()
    {
        //                          booking prices guests money leases rents jobs people billing suppliers settings security
        { OrgRole.Owner,           "Y Y Y Y Y Y Y Y Y Y Y Y" },
        { OrgRole.Admin,           "Y Y Y Y Y Y Y Y Y Y Y Y" },
        { OrgRole.PropertyManager, "Y Y Y Y Y Y Y N N Y N N" },
        // D14: the collaborator reads the prices. Guests and interventions are the limited «Yes» (no erasure, no Alloggiati
        // declaration; no final amount, no online payment): the matrix says Yes, the words say limited.
        { OrgRole.Collaborator,    "R R Y N N N Y N N N N N" },
        // LR-01: the accountant reads the list and the register of the rents with lease.read.
        { OrgRole.Accountant,      "R R N R R R R N R N N N" },
    };

    [Theory]
    [MemberData(nameof(ExpectedLevels))]
    public void Levels_OfEachSeededRole_AreTheOnesOfTheAnalysis(OrgRole role, string expected)
    {
        var levels = RoleOf(role).Capabilities.Select(c => c.Level switch
        {
            OrgCapabilityLevel.Yes => "Y",
            OrgCapabilityLevel.Read => "R",
            _ => "N",
        });

        Assert.Equal(expected, string.Join(' ', levels));
        Assert.Equal(CapabilityIds, RoleOf(role).Capabilities.Select(c => c.Id));
    }

    [Fact]
    public void Levels_OfAnyRole_HaveNoLevelForOnlyHisProperties()
    {
        // Which properties a person reaches is the person's (PropertyScope), never the role's: three levels, no more.
        Assert.Equal(["No", "Read", "Yes"], Enum.GetNames<OrgCapabilityLevel>().Order(StringComparer.Ordinal));
    }

    // ─── The roles, as the endpoint describes them ──────────────────────────────────────────────────────

    [Fact]
    public void Build_TheRoles_AreTheFiveOnesOwnerFirst()
    {
        Assert.Equal(
            [OrgRole.Owner, OrgRole.Admin, OrgRole.PropertyManager, OrgRole.Collaborator, OrgRole.Accountant],
            OrgRoleMatrix.Build(Seeded.Value, OrgRole.Owner).Select(r => r.Role));
    }

    [Fact]
    public void Build_TheScopeSoloAlcuni_IsSupportedByTheCollaboratorOnly()
    {
        var scope = OrgRoleMatrix.Build(Seeded.Value, OrgRole.Owner).ToDictionary(r => r.Role, r => r.ScopeSupported);

        Assert.True(scope[OrgRole.Collaborator]);
        Assert.All(scope.Where(s => s.Key != OrgRole.Collaborator), s => Assert.False(s.Value));
    }

    [Theory]
    [InlineData(OrgRole.Owner, "Admin,PropertyManager,Collaborator,Accountant")]
    [InlineData(OrgRole.Admin, "PropertyManager,Collaborator,Accountant")]
    [InlineData(OrgRole.PropertyManager, "")]
    [InlineData(OrgRole.Collaborator, "")]
    [InlineData(OrgRole.Accountant, "")]
    public void Build_AssignableIsWhatTheCallerMayGive_TheAdministratorOnlyFromTheOwner(OrgRole caller, string assignable)
    {
        var given = OrgRoleMatrix.Build(Seeded.Value, caller).Where(r => r.Assignable).Select(r => r.Role.ToString());

        Assert.Equal(assignable, string.Join(',', given));
    }

    [Fact]
    public void Build_TheOwnerRole_IsNeverAssignable()
    {
        foreach (var caller in Enum.GetValues<OrgRole>())
            Assert.False(RoleOf(OrgRole.Owner, caller).Assignable);
    }

    [Fact]
    public void Build_PermissionsByContext_ArePerContextWithTheRoleKeyAndTheSortedPermissions()
    {
        var manager = RoleOf(OrgRole.PropertyManager);

        Assert.Equal(["account", "long-rent", "short-rent"], manager.PermissionsByContext.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("org_manager", manager.PermissionsByContext["account"].RoleKey);
        Assert.Equal(["org.suppliers.manage"], manager.PermissionsByContext["account"].Permissions);
        Assert.Equal("property_manager", manager.PermissionsByContext["short-rent"].RoleKey);
        Assert.Equal(
            manager.PermissionsByContext["short-rent"].Permissions.Order(StringComparer.Ordinal),
            manager.PermissionsByContext["short-rent"].Permissions);
    }

    [Fact]
    public void Build_ARoleWithoutARowInAContext_HasNoEntryThere()
    {
        // The collaborator has no account membership at all; its long-term row only reads the property list.
        var collaborator = RoleOf(OrgRole.Collaborator);

        Assert.False(collaborator.PermissionsByContext.ContainsKey("account"));
        Assert.Equal(["property.read"], collaborator.PermissionsByContext["long-rent"].Permissions);
    }

    [Fact]
    public void Build_TheOwnerRows_AreTheOnesOfTheOnboarding()
    {
        var owner = RoleOf(OrgRole.Owner);

        Assert.Equal("org_owner", owner.PermissionsByContext["account"].RoleKey);
        Assert.Equal("property_owner", owner.PermissionsByContext["short-rent"].RoleKey);
        Assert.Equal("long_term_landlord", owner.PermissionsByContext["long-rent"].RoleKey);
    }

    [Fact]
    public void LevelOf_AContextTheRoleHasNoRowIn_HoldsNothing()
    {
        var onlyShort = new Dictionary<string, IReadOnlyCollection<string>> { ["short-rent"] = ["booking.write", "property.read"] };

        var levels = OrgCapabilityCatalog.LevelsFor(onlyShort).ToDictionary(g => g.Id, g => g.Level);

        Assert.Equal(OrgCapabilityLevel.Yes, levels["booking"]);
        Assert.Equal(OrgCapabilityLevel.Read, levels["prices"]);
        Assert.Equal(OrgCapabilityLevel.Read, levels["jobs"]);
        Assert.Equal(OrgCapabilityLevel.No, levels["leases"]);
        Assert.Equal(OrgCapabilityLevel.No, levels["people"]);
    }

    [Fact]
    public void LevelOf_APermissionOfAnotherContext_DoesNotCount()
    {
        // org.members.manage in short-rent is not the team permission: contexts do not leak into each other.
        var wrongContext = new Dictionary<string, IReadOnlyCollection<string>> { ["short-rent"] = ["org.members.manage", "lease.create"] };

        var levels = OrgCapabilityCatalog.LevelsFor(wrongContext).ToDictionary(g => g.Id, g => g.Level);

        Assert.Equal(OrgCapabilityLevel.No, levels["people"]);
        Assert.Equal(OrgCapabilityLevel.No, levels["leases"]);
    }

    // ─── Tripwire: a seeded permission without a place in the matrix ────────────────────────────────────

    [Fact]
    public void Tripwire_EveryPermissionOfTheSeededRoles_HasACapability()
    {
        var unknown = new List<string>();
        foreach (var role in Enum.GetValues<OrgRole>())
        {
            foreach (var (context, view) in RoleOf(role).PermissionsByContext)
            {
                unknown.AddRange(view.Permissions
                    .Where(p => !OrgCapabilityCatalog.Claims.Contains(new ContextPermission(context, p)))
                    .Select(p => $"{role}: {context}/{view.RoleKey} holds '{p}'"));
            }
        }

        Assert.True(
            unknown.Count == 0,
            "A permission was given to a role without a capability of the matrix that decides its level (OrgCapabilityCatalog): "
            + string.Join("; ", unknown.Distinct()));
    }

    [Fact]
    public void Tripwire_EveryPermissionTheMatrixReads_IsHeldByAtLeastOneSeededRole()
    {
        // The other direction: a capability that reads a permission nobody holds is a typo that would make everybody «No».
        var held = Enum.GetValues<OrgRole>()
            .SelectMany(role => RoleOf(role).PermissionsByContext.SelectMany(c => c.Value.Permissions.Select(p => new ContextPermission(c.Key, p))))
            .ToHashSet();

        var neverHeld = OrgCapabilityCatalog.All
            .SelectMany(c => c.Full.Concat(c.ReadOnly).Select(p => (c.Id, Permission: p)))
            .Where(x => !held.Contains(x.Permission))
            .Select(x => $"{x.Id}: {x.Permission.ContextKey}/{x.Permission.PermissionKey}")
            .ToList();

        Assert.True(neverHeld.Count == 0, "No seeded role holds: " + string.Join("; ", neverHeld));
    }

    // ─── Coherence with the actions of the API (the same policies OrgRoleActionMatrixTests reads) ───────

    /// <summary>
    /// For each capability, an action of the API that reads it and one that changes it (null where the API has none yet: the
    /// billing is behind the org policy, the suppliers, the settings and the integrations arrive with their tasks).
    /// </summary>
    public static TheoryData<string, Type, string, Type?, string?> Probes => new()
    {
        { "booking", typeof(BookingsController), nameof(BookingsController.GetAll), typeof(BookingsController), nameof(BookingsController.Create) },
        { "prices", typeof(PropertiesController), nameof(PropertiesController.GetAll), typeof(PricingAdapterController), nameof(PricingAdapterController.SaveConfig) },
        { "guests", typeof(GuestsController), nameof(GuestsController.GetAll), typeof(GuestsController), nameof(GuestsController.Update) },
        { "money", typeof(PaymentsController), nameof(PaymentsController.GetAll), typeof(PaymentsController), nameof(PaymentsController.Create) },
        { "leases", typeof(LeasesController), nameof(LeasesController.GetAll), typeof(LeasesController), nameof(LeasesController.Create) },
        { "rents", typeof(LongRentRentsController), nameof(LongRentRentsController.Get), typeof(LongRentRentsController), nameof(LongRentRentsController.SendReminder) },
        { "jobs", typeof(SuppliersController), nameof(SuppliersController.GetSuppliers), typeof(ServiceRequestsController), nameof(ServiceRequestsController.Create) },
        { "people", typeof(OrgMembersController), nameof(OrgMembersController.List), typeof(OrgMembersController), nameof(OrgMembersController.ChangeRole) },
        { "security", typeof(OrgActivityController), nameof(OrgActivityController.List), null, null },
    };

    [Theory]
    [MemberData(nameof(Probes))]
    public void Levels_OfTheCapabilitiesWithAnAction_AgreeWithWhatTheActionsOfTheApiAllow(
        string capabilityId,
        Type readController,
        string readAction,
        Type? writeController,
        string? writeAction)
    {
        var read = readController.GetMethod(readAction)!;
        var write = writeController?.GetMethod(writeAction!);
        var mismatches = new List<string>();

        foreach (var role in Enum.GetValues<OrgRole>())
        {
            var level = RoleOf(role).Capabilities.Single(c => c.Id == capabilityId).Level;
            var canRead = Allows(role, read);
            var canWrite = write is not null && Allows(role, write);

            // Yes: it can change it (and so can look at it). Read: it can look at it and cannot change it. No: neither.
            // The capabilities that have only one action (the activity log is read-only) are compared on that one.
            var expectedRead = level != OrgCapabilityLevel.No;
            var expectedWrite = level == OrgCapabilityLevel.Yes;

            if (canRead != expectedRead)
                mismatches.Add($"{role}/{capabilityId}: level {level} but the read action {(canRead ? "is" : "is not")} allowed");
            if (write is not null && canWrite != expectedWrite)
                mismatches.Add($"{role}/{capabilityId}: level {level} but the write action {(canWrite ? "is" : "is not")} allowed");
        }

        Assert.True(mismatches.Count == 0, string.Join("; ", mismatches));
    }

    private static HashSet<string> PermissionsOf(OrgRole role, string context)
    {
        var roleKey = AccountContext.IsAccountContext(context) ? OrgRoleCatalog.AccountRoleKey(role) : OrgRoleCatalog.HostRoleKey(role, context);
        return roleKey is not null && Seeded.Value.TryGetValue((context, roleKey), out var permissions) ? permissions.ToHashSet() : [];
    }

    /// <summary>The policies of an action are all required; a role passes when it holds the permission in one of the contexts of each.</summary>
    private static bool Allows(OrgRole role, MethodInfo action)
    {
        var policies = action.DeclaringType!.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .Select(a => a.Policy)
            .Where(p => p is not null && p.StartsWith(CasazenPolicies.ContextPolicyPrefix, StringComparison.Ordinal))
            .Select(p => p!)
            .ToList();
        Assert.NotEmpty(policies);

        return policies.All(policy =>
        {
            var (contexts, permission) = CasazenPolicies.ParseContextPolicy(policy);
            return contexts.Any(context => PermissionsOf(role, context).Contains(permission));
        });
    }
}
