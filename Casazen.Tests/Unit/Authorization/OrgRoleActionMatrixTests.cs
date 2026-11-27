using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
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
        { typeof(BookingsController), nameof(BookingsController.Counts), Everyone },
        { typeof(DashboardController), nameof(DashboardController.GetKpis), Everyone },
        { typeof(DashboardController), nameof(DashboardController.GetToday), Everyone },
        { typeof(DashboardController), nameof(DashboardController.GetOverview), Everyone },
        { typeof(DashboardController), nameof(DashboardController.GetIcalFeeds), Everyone },
        { typeof(ComplianceController), nameof(ComplianceController.GetSummary), Everyone },
        { typeof(AlloggiatiController), nameof(AlloggiatiController.GetSummary), Everyone },
        { typeof(SuppliersController), nameof(SuppliersController.GetSuppliers), Everyone },

        // Money: the collaborator never reads it.
        { typeof(PaymentsController), nameof(PaymentsController.GetAll), WithMoney },
        { typeof(FiscalController), nameof(FiscalController.AnnualReport), WithMoney },
        { typeof(FiscalController), nameof(FiscalController.WithholdingReport), WithMoney },
        { typeof(ReportsController), nameof(ReportsController.RevenueByProperty), WithMoney },
        { typeof(ReportsController), nameof(ReportsController.OccupancyByProperty), WithMoney },
        { typeof(ReportsController), nameof(ReportsController.BookingsByChannel), WithMoney },
        { typeof(ReportsController), nameof(ReportsController.Performance), WithMoney },
        { typeof(ReportsController), nameof(ReportsController.Trend), WithMoney },
        { typeof(CollectionsReportsController), nameof(CollectionsReportsController.Collections), WithMoney },
        { typeof(CollectionsReportsController), nameof(CollectionsReportsController.BalancesToCollect), WithMoney },
        { typeof(ChannelCostsReportsController), nameof(ChannelCostsReportsController.ChannelCosts), WithMoney },
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
        { typeof(PropertyPriceSuggestionController), nameof(PropertyPriceSuggestionController.Suggest), Operators },
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
        { typeof(LeaseMaintenanceRequestsController), nameof(LeaseMaintenanceRequestsController.Decide), Operators },

        // LR-09: the tenants are read like the leases; their personal data in clear (the copy) and their erasure are for who may create a lease.
        { typeof(LongRentTenantsController), nameof(LongRentTenantsController.GetAll), WithMoney },
        { typeof(LongRentTenantsController), nameof(LongRentTenantsController.GetById), WithMoney },
        { typeof(LongRentTenantsController), nameof(LongRentTenantsController.Export), Operators },
        { typeof(LongRentTenantsController), nameof(LongRentTenantsController.RequestErasure), Operators },

        // LR-12: the guided draft of a lease is written, and read (it carries the parties in clear), by who may create a lease; the
        // rules of the area, which are figures and nothing personal, by who may read the leases.
        { typeof(LeaseDraftsController), nameof(LeaseDraftsController.Create), Operators },
        { typeof(LeaseDraftsController), nameof(LeaseDraftsController.Get), Operators },
        { typeof(LeaseDraftsController), nameof(LeaseDraftsController.Save), Operators },
        { typeof(LeaseDraftsController), nameof(LeaseDraftsController.GetReview), Operators },
        { typeof(LeaseDraftsController), nameof(LeaseDraftsController.GetReading), Operators },
        { typeof(LeaseDraftsController), nameof(LeaseDraftsController.ConfirmReview), Operators },
        { typeof(LeaseDraftsController), nameof(LeaseDraftsController.MarkSent), Operators },
        { typeof(LeaseDraftsController), nameof(LeaseDraftsController.Delete), Operators },
        { typeof(LongRentLeaseSetupController), nameof(LongRentLeaseSetupController.GetRules), WithMoney },
        { typeof(LongRentLeaseSetupController), nameof(LongRentLeaseSetupController.GetLandlordDefaults), Operators },

        // CF-BE: the guided contract. What writes (the privacy acceptance, the gift of the plan, the signed link to the form) asks for
        // lease.create like the draft, so the accountant, who only reads, cannot spend the gift of the org; the state is read with lease.read.
        { typeof(ConcordatoFacileController), nameof(ConcordatoFacileController.AcceptPrivacy), Operators },
        { typeof(ConcordatoFacileController), nameof(ConcordatoFacileController.ConsumeGift), Operators },
        { typeof(ConcordatoFacileController), nameof(ConcordatoFacileController.GenerateLink), Operators },
        { typeof(ConcordatoFacileController), nameof(ConcordatoFacileController.GetAccess), WithMoney },
        { typeof(ConcordatoFacileController), nameof(ConcordatoFacileController.GetStatus), WithMoney },

        // ADR-026: every signed-in caller reads the To do list and its count (the policy Authenticated, which this table does not
        // list); closing an item is a change, so it asks for property.write of the short-rent context like the other edits of the
        // short-stay side. The accountant and the collaborator, who only read, are refused (403) instead of closing, for the whole
        // org, an item (a deadline of law too) that would then vanish for the owner as well.
        { typeof(ActivitiesController), nameof(ActivitiesController.Close), Operators },

        // E03/F04/US02/T01 (AC-NF24-3): the identity verification queue, its decisions and its figures are for who decides (verification.decision):
        // the owner, the administrators and the property managers; never the collaborator nor the accountant.
        { typeof(VerificationController), nameof(VerificationController.GetPending), Operators },
        { typeof(VerificationController), nameof(VerificationController.RequestNewPhoto), Operators },
        { typeof(VerificationController), nameof(VerificationController.RecordVerification), Operators },
        { typeof(VerificationController), nameof(VerificationController.RejectVerification), Operators },
        { typeof(VerificationController), nameof(VerificationController.GetMetrics), Operators },

        // The people of the org, and the properties each of them reaches.
        { typeof(OrgMembersController), nameof(OrgMembersController.List), Team },
        { typeof(OrgMembersController), nameof(OrgMembersController.GetProperties), Team },
        { typeof(OrgMembersController), nameof(OrgMembersController.SetProperties), Team },

        // E03/F02/US03/T01: the proof archive. Every role of the team reads it, but the receipts of a tourist tax declaration or payment are records
        // of money and are given, inside the action, only to who holds payment.read (ProofArchive_TaxReceipts_... below); the coverage report is a
        // state per obligation, with no file and no amount. Uploading is for who sends the Alloggiati communication, and a tax receipt asks
        // payment.write on top, inside the action.
        { typeof(ComplianceArchiveController), nameof(ComplianceArchiveController.List), Everyone },
        { typeof(ComplianceArchiveController), nameof(ComplianceArchiveController.GetBookingCompliance), Everyone },
        { typeof(ComplianceArchiveController), nameof(ComplianceArchiveController.Download), Everyone },
        { typeof(ComplianceArchiveController), nameof(ComplianceArchiveController.Export), Everyone },
        { typeof(ComplianceArchiveController), nameof(ComplianceArchiveController.GetReport), Everyone },
        { typeof(ComplianceArchiveController), nameof(ComplianceArchiveController.ExportReport), Everyone },
        { typeof(ComplianceArchiveController), nameof(ComplianceArchiveController.Upload), Operators },
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

    /// <summary>
    /// PROOF TEST: money and permission rule (AM-03, E03/F02/US03/T01). The receipts of a tourist tax declaration or payment are records of money, so
    /// the proof archive gives them (list, total, ZIP, download) only to who holds <c>payment.read</c>. The role gives the permission (the seeded
    /// roles), the permission gives the answer of the policy, and the controller tells the service: the owner, the administrators, the property
    /// managers and the accountant may read money; the collaborator, who reads the archive like the others, never does.
    /// </summary>
    [Theory]
    [InlineData(nameof(OrgRole.Owner), true)]
    [InlineData(nameof(OrgRole.Admin), true)]
    [InlineData(nameof(OrgRole.PropertyManager), true)]
    [InlineData(nameof(OrgRole.Accountant), true)]
    [InlineData(nameof(OrgRole.Collaborator), false)]
    public async Task ProofArchive_TaxReceipts_AreGivenOnlyToTheRolesThatHoldPaymentRead(string roleName, bool mayReadMoney)
    {
        var role = Enum.Parse<OrgRole>(roleName);
        var (contexts, permission) = CasazenPolicies.ParseContextPolicy(CasazenPolicies.PaymentRead);
        var holdsPaymentRead = contexts.Any(context => PermissionsOf(role, context).Contains(permission));
        Assert.Equal(mayReadMoney, holdsPaymentRead);

        // Every role of the team reads the archive (the policy of the class), which is what makes the cut inside the action necessary.
        Assert.True(Allows(role, typeof(ComplianceArchiveController).GetMethod(nameof(ComplianceArchiveController.List))!));

        var orgId = Guid.NewGuid();
        ComplianceArchiveQuery? listed = null;
        bool? downloaded = null;
        ComplianceArchiveExportQuery? exported = null;
        var archive = new Mock<IComplianceArchiveService>();
        archive
            .Setup(a => a.ListAsync(It.IsAny<HostScope>(), It.IsAny<ComplianceArchiveQuery>(), It.IsAny<CancellationToken>()))
            .Callback<HostScope, ComplianceArchiveQuery, CancellationToken>((_, query, _) => listed = query)
            .ReturnsAsync(new ComplianceArchivePage([], 1, 25, 0));
        archive
            .Setup(a => a.OpenDownloadAsync(It.IsAny<HostScope>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<HostScope, Guid, bool, CancellationToken>((_, _, money, _) => downloaded = money)
            .ReturnsAsync(new ComplianceArchiveDownload(new Uri("https://storage.example/private/prova"), null, "prova.pdf", "application/pdf"));
        archive
            .Setup(a => a.ExportAsync(It.IsAny<HostScope>(), It.IsAny<ComplianceArchiveExportQuery>(), It.IsAny<CancellationToken>()))
            .Callback<HostScope, ComplianceArchiveExportQuery, CancellationToken>((_, query, _) => exported = query)
            .ReturnsAsync(() => new ComplianceArchiveExportFile(new MemoryStream(), "prove.zip", 0));
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.PaymentRead))
            .ReturnsAsync(holdsPaymentRead ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes
            .Setup(s => s.ResolveAsync("auth0|persona", It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostScope(orgId));

        var sut = new ComplianceArchiveController(
            archive.Object, orgs.Object, scopes.Object, authorization.Object, Microsoft.Extensions.Options.Options.Create(new ComplianceOptions()))
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|persona")], "test")) },
            },
        };

        await sut.List(null, null, null, null, CancellationToken.None);
        await sut.Download(Guid.NewGuid(), CancellationToken.None);
        await sut.Export(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), null, CancellationToken.None);

        Assert.Equal(mayReadMoney, listed!.MoneyProofs);
        Assert.Equal(mayReadMoney, downloaded);
        Assert.Equal(mayReadMoney, exported!.MoneyProofs);
    }

    /// <summary>
    /// AM-02c (AM-D12): the property manager has a membership of the account context, so a policy of the account context could
    /// start to let it in. Every action of the API found by reflection that asks for a permission of the account context is
    /// checked: the property manager passes it only when the permission is the one of the trusted suppliers, whatever the
    /// controller (so a team, billing, settings or activity endpoint added later is covered without anyone listing it).
    /// </summary>
    [Fact]
    public void PropertyManager_InTheAccountContext_PassesNoAccountActionButTheTrustedSuppliers()
    {
        var accountPermissions = typeof(OrgMembersController).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t) && t is { IsAbstract: false, IsPublic: true })
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(PoliciesOf)
            .Select(CasazenPolicies.ParseContextPolicy)
            .Where(p => p.ContextKeys.Any(AccountContext.IsAccountContext))
            .Select(p => p.PermissionKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The team, the activity log and the other pages of the area are really found (the test is not vacuous).
        Assert.Contains(AccountContext.Permissions.MembersManage, accountPermissions);
        Assert.Contains(AccountContext.Permissions.ActivityRead, accountPermissions);

        var held = PermissionsOf(OrgRole.PropertyManager, AccountContext.Key);
        Assert.Equal([AccountContext.Permissions.SuppliersManage], held.ToArray());
        foreach (var permission in accountPermissions)
            Assert.Equal(permission == AccountContext.Permissions.SuppliersManage, held.Contains(permission));
    }
}
