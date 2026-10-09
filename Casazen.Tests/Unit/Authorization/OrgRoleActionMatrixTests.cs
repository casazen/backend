using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// AM-03: who may do what, as a table. Every row is an action of the API with the org roles that must be able to call it (the
/// product rule written by hand); the test takes the policies the action really carries, reads the permissions each role
/// really holds from the seeded roles of the model (what the migrations leave in the database), and compares. A permission
/// moved to the wrong role, or an action left on a broad policy, shows up as a row that no longer matches.
/// </summary>
public class OrgRoleActionMatrixTests
{
    private const string Owner = nameof(OrgRole.Owner);
    private const string Admin = nameof(OrgRole.Admin);
    private const string Manager = nameof(OrgRole.PropertyManager);
    private const string Collaborator = nameof(OrgRole.Collaborator);
    private const string Accountant = nameof(OrgRole.Accountant);

    private const string Everyone = Owner + "," + Admin + "," + Manager + "," + Collaborator + "," + Accountant;
    private const string Operators = Owner + "," + Admin + "," + Manager;
    private const string WithoutAccountant = Owner + "," + Admin + "," + Manager + "," + Collaborator;
    private const string WithMoney = Owner + "," + Admin + "," + Manager + "," + Accountant;
    private const string Team = Owner + "," + Admin;

    /// <summary>Permissions by context and role key, as seeded.</summary>
    private static readonly Lazy<Dictionary<(string Context, string RoleKey), HashSet<string>>> Seeded = new(() =>
    {
        using var db = OrgTeamTestData.NewDb();
        return db.RolePermissions.Include(rp => rp.Role).AsEnumerable()
            .GroupBy(rp => (rp.Role.ContextKey, rp.Role.RoleKey))
            .ToDictionary(g => g.Key, g => g.Select(rp => rp.PermissionKey).ToHashSet(StringComparer.Ordinal));
    });

    /// <summary>
    /// What the role holds in the context, for a person who works in both rental areas: the account role of the org role, and
    /// the role of each rental context.
    /// </summary>
    private static HashSet<string> PermissionsOf(OrgRole role, string context)
    {
        var roleKey = AccountContext.IsAccountContext(context) ? OrgRoleCatalog.AccountRoleKey(role) : OrgRoleCatalog.HostRoleKey(role, context);
        return roleKey is not null && Seeded.Value.TryGetValue((context, roleKey), out var permissions) ? permissions : [];
    }

    private static List<string> PoliciesOf(MethodInfo action) =>
        action.DeclaringType!.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .Select(a => a.Policy)
            .Where(p => p is not null && p.StartsWith(CasazenPolicies.ContextPolicyPrefix, StringComparison.Ordinal))
            .Select(p => p!)
            .ToList();

    /// <summary>The policies of an action are all required: a role passes when it holds the permission in one of the contexts of each.</summary>
    private static bool Allows(OrgRole role, MethodInfo action) => PoliciesOf(action).All(policy =>
    {
        var (contexts, permission) = CasazenPolicies.ParseContextPolicy(policy);
        return contexts.Any(context => PermissionsOf(role, context).Contains(permission));
    });

    public static TheoryData<Type, string, string> Matrix => new()
    {
        // What every role of the team can look at.
        { typeof(PropertiesController), nameof(PropertiesController.GetAll), Everyone },
        { typeof(PropertiesController), nameof(PropertiesController.GetCinCompliance), Everyone },
        { typeof(BookingsController), nameof(BookingsController.GetAll), Everyone },
        { typeof(BookingsController), nameof(BookingsController.Search), Everyone },
        { typeof(DashboardController), nameof(DashboardController.GetKpis), Everyone },
        { typeof(DashboardController), nameof(DashboardController.GetToday), Everyone },
        { typeof(DashboardController), nameof(DashboardController.GetIcalFeeds), Everyone },
        { typeof(ComplianceController), nameof(ComplianceController.GetSummary), Everyone },
        { typeof(AlloggiatiController), nameof(AlloggiatiController.GetSummary), Everyone },
        { typeof(SuppliersController), nameof(SuppliersController.GetSuppliers), Everyone },

        // Money: the collaborator never reads it.
        { typeof(PaymentsController), nameof(PaymentsController.GetAll), WithMoney },
        { typeof(FiscalController), nameof(FiscalController.AnnualReport), WithMoney },
        { typeof(FiscalController), nameof(FiscalController.WithholdingReport), WithMoney },
        { typeof(PaymentsController), nameof(PaymentsController.Create), Operators },
        { typeof(PaymentsController), nameof(PaymentsController.Refund), Operators },

        // Property core, bookings, channels: the owner, the administrators and the managers.
        { typeof(PropertiesController), nameof(PropertiesController.Update), Operators },
        { typeof(PropertiesController), nameof(PropertiesController.UpdateCin), Operators },
        { typeof(PropertiesController), nameof(PropertiesController.Delete), Operators },
        { typeof(PropertyResponsibleController), nameof(PropertyResponsibleController.Set), Operators },
        { typeof(BookingsController), nameof(BookingsController.Create), Operators },
        { typeof(BookingLifecycleController), nameof(BookingLifecycleController.CheckIn), Operators },
        { typeof(BookingCancellationController), nameof(BookingCancellationController.Cancel), Operators },
        { typeof(FiscalController), nameof(FiscalController.AssignRegime), Operators },
        { typeof(PricingAdapterController), nameof(PricingAdapterController.SaveConfig), Operators },
        { typeof(OtaController), nameof(OtaController.GetIntegrations), Operators },
        { typeof(OtaController), nameof(OtaController.SyncAll), Operators },
        { typeof(OtaIntegrationsController), nameof(OtaIntegrationsController.Create), Operators },

        // AM-03: what the collaborator can do without the rest.
        { typeof(ServiceRequestsController), nameof(ServiceRequestsController.Create), WithoutAccountant },
        { typeof(ServiceRequestsController), nameof(ServiceRequestsController.MatchSupplier), WithoutAccountant },
        { typeof(ServiceRequestsController), nameof(ServiceRequestsController.MarkPaid), WithoutAccountant },
        { typeof(GuestsController), nameof(GuestsController.GetAll), WithoutAccountant },
        { typeof(GuestsController), nameof(GuestsController.Create), WithoutAccountant },
        { typeof(GuestsController), nameof(GuestsController.Update), WithoutAccountant },
        { typeof(GdprController), nameof(GdprController.ExportGuestData), WithoutAccountant },
        { typeof(AlloggiatiController), nameof(AlloggiatiController.DownloadRecordFile), WithoutAccountant },

        // AM-03: and what it cannot, because it was carved out of guest.write and booking.write.
        { typeof(GuestsController), nameof(GuestsController.Delete), Operators },
        { typeof(GdprController), nameof(GdprController.DeleteGuestData), Operators },
        { typeof(GdprController), nameof(GdprController.AnonymizeGuestData), Operators },
        { typeof(GdprController), nameof(GdprController.UpdateConsent), Operators },
        { typeof(AlloggiatiController), nameof(AlloggiatiController.ReplaceStayGuests), Operators },
        { typeof(AlloggiatiController), nameof(AlloggiatiController.MarkSentManually), Operators },
        { typeof(AlloggiatiController), nameof(AlloggiatiController.SendManual), Operators },

        // Long-term rental: the accountant reads the leases, the collaborator sees none.
        { typeof(LeasesController), nameof(LeasesController.GetAll), WithMoney },
        { typeof(LeasesController), nameof(LeasesController.Create), Operators },
        { typeof(LeasesController), nameof(LeasesController.TriggerRegistration), Operators },

        // The people of the org, and the properties each of them reaches.
        { typeof(OrgMembersController), nameof(OrgMembersController.List), Team },
        { typeof(OrgMembersController), nameof(OrgMembersController.GetProperties), Team },
        { typeof(OrgMembersController), nameof(OrgMembersController.SetProperties), Team },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Action_IsAllowedToExactlyTheRolesOfTheTable(Type controller, string actionName, string expectedRoles)
    {
        var action = controller.GetMethod(actionName);
        Assert.NotNull(action);
        Assert.NotEmpty(PoliciesOf(action));

        var allowed = Enum.GetValues<OrgRole>().Where(role => Allows(role, action)).Select(role => role.ToString()).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(expectedRoles.Split(',').Order(StringComparer.Ordinal).ToList(), allowed);
    }
}
