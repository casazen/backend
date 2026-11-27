using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-96: the report by property owner of an agency is the Home's performance split by what each owner holds of each property. October
/// 2026, worked out by hand: Villa (shared 50/50 by Anna and Bruno) has a 400 stay of 4 nights, Casa Sola (Anna alone) a 200 one of 2,
/// Casa Libera (nobody's) a 300 one of 3; 31 nights a house.
/// </summary>
public class OwnerReportTests
{
    private const string Sub = "auth0|owner-report-test";

    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateOnly OctoberEnd = new(2026, 10, 31);

    // What must never leave the endpoint: the personal data of an owner but its name.
    private const string FiscalCode = "RSSNNA80A41H501Z";
    private const string Iban = "IT60X0542811101000000123456";
    private const string Email = "anna.rossi.privata@example.com";
    private const string Phone = "+39 333 1234567";

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static Booking Stay(Property house, Guid guestId, DateTime checkIn, DateTime checkOut, decimal basePrice) => new()
    {
        OrgId = house.OrgId,
        PropertyId = house.Id,
        GuestId = guestId,
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Direct,
        CheckInDate = checkIn,
        CheckOutDate = checkOut,
        NumberOfGuests = 2,
        BasePrice = basePrice,
        TotalPrice = basePrice,
    };

    private static Property NewHouse(Guid orgId, string name)
    {
        var house = HostScopeScenario.NewProperty(orgId, "auth0|agenzia-nf96", name);
        house.CreatedAt = Day(2024, 1, 1);
        return house;
    }

    private static PropertyOwner NewOwner(Guid orgId, string first, string last, bool anonymized = false) => new()
    {
        OrgId = orgId,
        FirstName = first,
        LastName = last,
        Email = anonymized ? $"{Guid.NewGuid():N}@example.com" : Email,
        Phone = Phone,
        FiscalCode = FiscalCode,
        Iban = Iban,
        CreatedAt = Day(2024, 1, 1),
        AnonymizedAt = anonymized ? Day(2026, 5, 1) : null,
    };

    private static PropertyOwnership Owns(PropertyOwner owner, Property house, decimal share, DateTime start, DateTime? end = null) => new()
    {
        OrgId = house.OrgId,
        PropertyId = house.Id,
        PropertyOwnerId = owner.Id,
        SharePercent = share,
        OwnershipStart = start,
        OwnershipEnd = end,
    };

    private sealed record World(
        HostDashboardService Dashboard,
        OwnerReportService Service,
        HostScope Scope,
        Guid OrgId,
        PropertyOwner Anna,
        PropertyOwner Bruno,
        Property Villa,
        Property Sola,
        Property Libera);

    private static async Task<World> SeedAsync(Action<AppDbContext, World>? more = null)
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var villa = NewHouse(orgId, "Villa");
        var sola = NewHouse(orgId, "Casa Sola");
        var libera = NewHouse(orgId, "Casa Libera");
        var anna = NewOwner(orgId, "Anna", "Rossi");
        var bruno = NewOwner(orgId, "Bruno", "Verdi");
        var guest = new Guest { OrgId = orgId, FirstName = "Ospite", LastName = "Prova", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Properties.AddRange(villa, sola, libera);
        db.PropertyOwners.AddRange(anna, bruno);
        db.Guests.Add(guest);
        db.Bookings.AddRange(
            Stay(villa, guest.Id, Day(2026, 10, 10), Day(2026, 10, 14), 400m),
            Stay(sola, guest.Id, Day(2026, 10, 20), Day(2026, 10, 22), 200m),
            Stay(libera, guest.Id, Day(2026, 10, 1), Day(2026, 10, 4), 300m));
        db.PropertyOwnerships.AddRange(
            Owns(anna, villa, 50m, Day(2024, 1, 1)),
            Owns(bruno, villa, 50m, Day(2024, 1, 1)),
            Owns(anna, sola, 100m, Day(2024, 1, 1)));

        var dashboard = new HostDashboardService(db, new ConfigurationBuilder().Build(), Clock);
        var world = new World(dashboard, new OwnerReportService(db, dashboard), new HostScope(orgId), orgId, anna, bruno, villa, sola, libera);
        more?.Invoke(db, world);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return world;
    }

    // ---------------------------------------------------------------- proof: the shares add up to the Home

    [Fact]
    public async Task OwnerReport_SharesAddUpToTheHomeTotal_Test()
    {
        var world = await SeedAsync();
        var home = await world.Dashboard.GetKpisAsync(world.Scope, new HostDashboardQuery(HostDashboardPeriodKind.Month, October));

        var report = await world.Service.GetByOwnerAsync(world.Scope, new HostPerformanceQuery(October, OctoberEnd));

        // The Home: 400 + 200 + 300 = 900; 4 + 2 + 3 = 9 nights taken of 3 houses x 31 nights = 93.
        Assert.Equal(900m, home.Revenue);
        Assert.Equal(new HostDashboardOccupancy(9, 93, 0), home.Occupancy);
        Assert.Equal(home.Revenue, report.Total.Revenue);
        Assert.Equal(home.Occupancy.OccupiedNights, report.Total.OccupiedNights);
        Assert.Equal(home.Occupancy.AvailableNights, report.Total.AvailableNights);

        // Anna: half of the villa and all of Casa Sola; Bruno: the other half of the villa; the owners' rows are by name.
        Assert.Equal(new[] { "Anna Rossi", "Bruno Verdi" }, report.Rows.Select(r => r.DisplayName).ToArray());
        var anna = report.Rows[0];
        var bruno = report.Rows[1];
        Assert.Equal(2, anna.PropertyCount);
        Assert.Equal(400m, anna.Figures.Revenue);   // 200 + 200
        Assert.Equal(4m, anna.Figures.OccupiedNights);   // 2 + 2
        Assert.Equal(46.5m, anna.Figures.AvailableNights);   // 15.5 + 31
        Assert.Equal(100m, anna.Figures.Adr);   // 400 over 4 nights
        Assert.Equal(8.6m, anna.Figures.RevPar);   // 400 over 46.5 nights, 8.602...
        Assert.Equal(1, bruno.PropertyCount);
        Assert.Equal(200m, bruno.Figures.Revenue);
        Assert.Equal(2m, bruno.Figures.OccupiedNights);

        // The house nobody owns is the unassigned row; the owners and the unassigned row add up to the Home, a co-owned house once.
        Assert.Equal(1, report.Unassigned.PropertyCount);
        Assert.Equal(300m, report.Unassigned.Figures.Revenue);
        Assert.Null(report.Unassigned.OwnerId);
        var parts = report.Rows.Select(r => r.Figures).Append(report.Unassigned.Figures).ToList();
        Assert.Equal(home.Revenue, parts.Sum(p => p.Revenue));
        Assert.Equal(home.Occupancy.OccupiedNights, parts.Sum(p => p.OccupiedNights));
        Assert.Equal(home.Occupancy.AvailableNights, parts.Sum(p => p.AvailableNights));
    }

    [Fact]
    public async Task OwnerReport_OwnershipHistory_OnlyThePeriodsThatOverlapTheDaysCount_AndANewOwnerDoesNotCountWhatItDidNotOwn()
    {
        // Casa Libera: Carlo owned it until 15 October, Dario since the 16th (15 and 16 days of 31); Edoardo sold it in 2025 and has a part
        // of Casa Sola only from November: neither held anything in the days of the report.
        Guid carlo = default, dario = default, edoardo = default;
        var world = await SeedAsync((db, w) =>
        {
            var c = NewOwner(w.OrgId, "Carlo", "Bianchi");
            var d = NewOwner(w.OrgId, "Dario", "Neri");
            var e = NewOwner(w.OrgId, "Edoardo", "Gialli");
            carlo = c.Id;
            dario = d.Id;
            edoardo = e.Id;
            db.PropertyOwners.AddRange(c, d, e);
            db.PropertyOwnerships.AddRange(
                Owns(c, w.Libera, 100m, Day(2024, 1, 1), Day(2026, 10, 15)),
                Owns(d, w.Libera, 100m, Day(2026, 10, 16)),
                Owns(e, w.Libera, 100m, Day(2020, 1, 1), Day(2025, 12, 31)),
                Owns(e, w.Sola, 50m, Day(2026, 11, 1)));
        });

        var report = await world.Service.GetByOwnerAsync(world.Scope, new HostPerformanceQuery(October, OctoberEnd));

        // Edoardo held nothing in the days of the report: no row. Carlo and Dario share Casa Libera by the days each had it, and nobody
        // is left with a part of it, so the unassigned row is empty.
        Assert.DoesNotContain(report.Rows, r => r.OwnerId == edoardo);
        var carloRow = report.Rows.Single(r => r.OwnerId == carlo);
        var darioRow = report.Rows.Single(r => r.OwnerId == dario);
        Assert.Equal(Math.Round(300m * 15m / 31m, 2), Math.Round(carloRow.Figures.Revenue, 2));
        Assert.Equal(Math.Round(300m * 16m / 31m, 2), Math.Round(darioRow.Figures.Revenue, 2));
        Assert.Equal(300m, Math.Round(carloRow.Figures.Revenue + darioRow.Figures.Revenue, 2));
        Assert.Equal(0, report.Unassigned.PropertyCount);
        Assert.Equal(0m, Math.Round(report.Unassigned.Figures.Revenue, 2));

        // And everything together is still the Home's revenue.
        Assert.Equal(900m, Math.Round(report.Rows.Sum(r => r.Figures.Revenue) + report.Unassigned.Figures.Revenue, 2));
    }

    // ---------------------------------------------------------------- proof: only the owner and the administrators of an agency, with payment.read

    [Fact]
    public async Task OwnerReport_RequiresAgencyStatementViewAndPaymentRead_Test()
    {
        // Both policies, as two attributes: ASP.NET Core requires all of them.
        var policies = typeof(OwnerReportsController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Select(a => a.Policy)
            .ToList();
        Assert.Equal(2, policies.Count);
        Assert.Contains(CasazenPolicies.AgencyStatementView, policies);
        Assert.Contains(CasazenPolicies.PaymentRead, policies);
        Assert.Null(typeof(OwnerReportsController).GetCustomAttribute<AllowAnonymousAttribute>());

        // The statements' policy asks for the agency plan and for agency.statement.view.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        var provider = services.BuildServiceProvider().GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await provider.GetPolicyAsync(CasazenPolicies.AgencyStatementView);
        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is AgencyTierRequirement);
        Assert.Equal(AgencyPermissions.StatementView, Assert.Single(policy.Requirements.OfType<AgencyPermissionRequirement>()).PermissionKey);
        Assert.NotNull(await provider.GetPolicyAsync(CasazenPolicies.PaymentRead));

        // The owner and the administrators hold it; the property manager (agency.owner.view only), the collaborator and the accountant do not.
        Assert.True(await HoldsStatementViewAsync(OrgRole.Owner));
        Assert.True(await HoldsStatementViewAsync(OrgRole.Admin));
        Assert.False(await HoldsStatementViewAsync(OrgRole.PropertyManager));
        Assert.False(await HoldsStatementViewAsync(OrgRole.Collaborator));
        Assert.False(await HoldsStatementViewAsync(OrgRole.Accountant));
        Assert.Contains(AgencyPermissions.OwnerView, OrgRoleCatalog.AgencyPermissionsOf(OrgRole.PropertyManager));
        Assert.DoesNotContain(AgencyPermissions.StatementView, OrgRoleCatalog.AgencyPermissionsOf(OrgRole.PropertyManager));

        // An org that is not on the agency plan is refused (403 feature_requires_agency_tier), whoever the caller is.
        foreach (var tier in new[] { PlanTier.Starter, PlanTier.Pro, PlanTier.Scale })
        {
            var context = await HandleTierAsync(tier);
            Assert.False(context.HasSucceeded);
            Assert.Contains(context.FailureReasons, r => r.Message == "feature_requires_agency_tier");
        }

        Assert.True((await HandleTierAsync(PlanTier.Agency)).HasSucceeded);
    }

    private static async Task<bool> HoldsStatementViewAsync(OrgRole role)
    {
        var orgId = Guid.NewGuid();
        var snapshot = new UserAuthorizationSnapshot(
            Exists: true,
            IsActive: true,
            Role: UserRole.None,
            SupplierOrgId: null,
            Memberships: [],
            OrgId: orgId,
            OrgMember: new OrgMemberSnapshot(orgId, role, OrgMemberStatus.Active));
        var snapshots = new Mock<IUserAuthorizationSnapshotStore>();
        snapshots.Setup(s => s.GetAsync(Sub, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var onboarding = new Mock<IHostOnboardingGate>();
        onboarding.Setup(g => g.GetStatusAsync(Sub, It.IsAny<CancellationToken>())).ReturnsAsync(new HostOnboardingStatus(true, true));

        var handler = new AgencyPermissionAuthorizationHandler(
            snapshots.Object, orgs.Object, onboarding.Object, NullLogger<AgencyPermissionAuthorizationHandler>.Instance);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Sub)], "TestAuthType"));
        var context = new AuthorizationHandlerContext([new AgencyPermissionRequirement(AgencyPermissions.StatementView)], user, null);

        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static async Task<AuthorizationHandlerContext> HandleTierAsync(PlanTier tier)
    {
        var org = new OrgEntity { Id = Guid.NewGuid(), PlanTier = tier, IsAgencyTierDowngraded = false };
        var orgContextResolver = new Mock<IOrgContextResolver>();
        orgContextResolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(org.Id);
        var orgService = new Mock<IOrgService>();
        orgService.Setup(s => s.GetByIdAsync(org.Id, It.IsAny<CancellationToken>())).ReturnsAsync(org);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(httpContext);

        var handler = new AgencyTierAuthorizationHandler(orgContextResolver.Object, orgService.Object, accessor.Object);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Sub), new Claim(ClaimTypes.NameIdentifier, Sub)], "TestAuthType"));
        var context = new AuthorizationHandlerContext([new AgencyTierRequirement()], user, null);

        await handler.HandleAsync(context);
        return context;
    }

    // ---------------------------------------------------------------- proof: no personal data of an owner leaves the endpoint

    [Fact]
    public async Task OwnerReport_ShowsNoOwnerPersonalData_Test()
    {
        var world = await SeedAsync((db, w) =>
        {
            var gone = NewOwner(w.OrgId, "Gianni", "Cancellato", anonymized: true);
            db.PropertyOwners.Add(gone);
            db.PropertyOwnerships.Add(Owns(gone, w.Libera, 100m, Day(2024, 1, 1)));
        });

        var report = await world.Service.GetByOwnerAsync(world.Scope, new HostPerformanceQuery(October, OctoberEnd));
        const string anonymizedLabel = "Proprietario anonimizzato";
        var json = JsonSerializer.Serialize(OwnerReportDto.From(report, OwnerReportDocuments.Title, anonymizedLabel));
        var csv = Encoding.UTF8.GetString(OwnerReportDocuments.Csv(report));

        // What it says of an owner is its name (the label of the anonymized one) and the numbers; nothing else of the person.
        foreach (var secret in new[] { FiscalCode, Iban, Email, Phone, "Gianni", "Cancellato" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, csv, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Anna Rossi", json, StringComparison.Ordinal);
        Assert.Contains(anonymizedLabel, json, StringComparison.Ordinal);
        Assert.Contains(OwnerReportDocuments.AnonymizedOwnerLabel, csv, StringComparison.Ordinal);
        Assert.Equal(anonymizedLabel, OwnerReportDocuments.AnonymizedOwnerLabel);
        Assert.Contains(report.Rows, r => r.IsAnonymized && r.DisplayName is null);

        // And the shape cannot carry it: no member of the service result or of the answer is a fiscal code, an IBAN, an e-mail or a phone.
        foreach (var type in new[]
                 {
                     typeof(OwnerPerformanceReport), typeof(OwnerPerformanceRow), typeof(OwnerPerformanceFigures),
                     typeof(OwnerReportDto), typeof(OwnerReportRowDto), typeof(WeightedFiguresDto),
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("Fiscal", StringComparison.OrdinalIgnoreCase)
                                              || n.Contains("Iban", StringComparison.OrdinalIgnoreCase)
                                              || n.Contains("Email", StringComparison.OrdinalIgnoreCase)
                                              || n.Contains("Phone", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task OwnerReport_OnlyShowsThePropertiesTheScopeReaches()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var owner = NewOwner(world.OrgId, "Anna", "Rossi");
        db.PropertyOwners.Add(owner);
        db.PropertyOwnerships.AddRange(
            Owns(owner, world.Granted, 100m, Day(2024, 1, 1)),
            Owns(owner, world.Hidden, 100m, Day(2024, 1, 1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var dashboard = new HostDashboardService(db, new ConfigurationBuilder().Build(), Clock);
        var service = new OwnerReportService(db, dashboard);

        var everything = await service.GetByOwnerAsync(world.OrgWide, new HostPerformanceQuery(October, OctoberEnd));
        var restricted = await service.GetByOwnerAsync(world.Restricted, new HostPerformanceQuery(October, OctoberEnd));

        // The collaborator «Solo alcuni» sees the owner for the one house it was given, with that house's money and nothing of the other.
        Assert.Equal(2, Assert.Single(everything.Rows).PropertyCount);
        Assert.Equal(1, Assert.Single(restricted.Rows).PropertyCount);
        Assert.True(restricted.Rows[0].Figures.Revenue < everything.Rows[0].Figures.Revenue);
        Assert.Equal(restricted.Total.Revenue, restricted.Rows[0].Figures.Revenue);
    }
}
