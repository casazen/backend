using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The ISTAT region table (E21/F01/US01/T01, decision O6, NF-44, NF-52): only the platform staff changes it, with an audit line; a region the staff did
/// not verify is never shown as covered; the guest is asked a statistical field only where the region requires it; and the deadline of the cockpit is
/// the table's, overridden by the configuration. The table is the one the migration seeds (<c>EnsureCreated</c> applies it on the in-memory database).
/// </summary>
public class IstatRegionServiceTests : IAsyncLifetime
{
    private const string Staff = "auth0|staff-istat";
    private const string Source = "https://www.example.gov.it/flussi-turistici";
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly DbContextOptions<AppDbContext> _options =
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(Now);
    private readonly ComplianceOptions _compliance = new();
    private readonly Guid _org = Guid.NewGuid();
    private AppDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(_options);
        await _db.Database.EnsureCreatedAsync();
        _db.Comuni.AddRange(
            ComuneRow("013075", "Como", "CO", "03", "Lombardia"),
            ComuneRow("048017", "Firenze", "FI", "09", "Toscana"),
            ComuneRow("021008", "Bolzano/Bozen", "BZ", "04", "Trentino-Alto Adige/Südtirol"),
            ComuneRow("022205", "Trento", "TN", "04", "Trentino-Alto Adige/Südtirol"),
            ComuneRow("999001", "Comune di prova", "XX", "04", "Trentino-Alto Adige/Südtirol"));
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ─── The proofs of the brief ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (permission): only the platform staff changes the table. The controller that lists and saves it sits under <see cref="CasazenPolicies.AdminOnly"/>
    /// and nothing else, with two actions (GET and PUT); the only other controller that takes the service is the one of the property, which has one GET under
    /// the property read; the registered policy refuses every role but the administrator; and a save by a staff member reaches the service with its id and
    /// leaves a line of the audit trail, while a caller with no id writes nothing.
    /// </summary>
    [Fact]
    public async Task IstatRegions_AdminOnly_Test()
    {
        var admin = typeof(AdminIstatRegionsController);
        Assert.Equal([CasazenPolicies.AdminOnly], admin.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        Assert.Empty(admin.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Equal("api/admin/istat-regions", admin.GetCustomAttribute<RouteAttribute>()!.Template);
        var actions = RoutedActions(admin);
        Assert.Equal(["List", "Update"], actions.Select(a => a.Name).Order());
        Assert.Equal(["GET", "PUT"], actions.SelectMany(a => a.GetCustomAttributes<HttpMethodAttribute>()).SelectMany(h => h.HttpMethods).Order());
        Assert.All(actions, action =>
        {
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.DoesNotContain(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true), a => a.Policy != CasazenPolicies.AdminOnly);
        });

        // Nobody else writes the table: the other controller that takes the service is the property's, which only reads, for the caller who reads the property.
        var takers = admin.Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .Where(type => type.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IIstatRegionService))))
            .Select(type => type.Name)
            .Order();
        Assert.Equal([nameof(AdminIstatRegionsController), nameof(PropertyIstatCoverageController)], takers);
        var property = typeof(PropertyIstatCoverageController);
        Assert.Equal([CasazenPolicies.PropertyRead], property.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        Assert.Equal(["GET"], RoutedActions(property).SelectMany(a => a.GetCustomAttributes<HttpMethodAttribute>()).SelectMany(h => h.HttpMethods));

        // The registered policy refuses every role but the administrator (the positive control).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        await using var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(CasazenPolicies.AdminOnly);
        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());
        foreach (var role in new[] { "PropertyOwner", "PropertyManager", "Supplier", "Guest" })
        {
            var asHost = new AuthorizationHandlerContext(policy.Requirements, Principal(role), resource: null);
            await ((IAuthorizationHandler)requirement).HandleAsync(asHost);
            Assert.False(asHost.HasSucceeded, $"A caller with the role {role} passed the policy of the ISTAT region table.");
        }

        var staff = new AuthorizationHandlerContext(policy.Requirements, Principal("Admin"), resource: null);
        await ((IAuthorizationHandler)requirement).HandleAsync(staff);
        Assert.True(staff.HasSucceeded, "The platform administrator does not pass the policy of the ISTAT region table.");

        // A save reaches the service with the id of the staff member and leaves its audit line; a caller with no id is refused and writes nothing.
        var request = new UpdateIstatRegionRequest
        {
            RegionIstatCode = "03",
            System = "ROSS1000",
            DeadlineKind = IstatDeadlineKind.Monthly,
            DeadlineDay = 5,
            OctorateCoverage = IstatCoverage.Unknown,
            SourceUrl = Source,
        };
        var controller = new AdminIstatRegionsController(Service()) { ControllerContext = ContextOf(Staff) };
        var saved = Assert.IsType<IstatRegionRuleDto>(Assert.IsType<OkObjectResult>((await controller.Update(request, Ct)).Result).Value);
        Assert.Equal(("03", IstatCoverage.Unknown, Source), (saved.RegionIstatCode, saved.OctorateCoverage, saved.SourceUrl));
        var line = Assert.Single(await _db.RegulatoryDataAuditEntries.AsNoTracking().ToListAsync(Ct));
        Assert.Equal((IstatRegionErrors.AuditEntityType, saved.Id, Staff), (line.EntityType, line.EntityId, line.ChangedByUserId));

        var nobody = new AdminIstatRegionsController(Service()) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedResult>((await nobody.Update(request, Ct)).Result);
        Assert.Single(await _db.RegulatoryDataAuditEntries.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(21, await _db.IstatRegionRules.CountAsync(Ct));
    }

    /// <summary>
    /// Proof (data): a region that was not verified is never shown as covered. Every row the migration seeds is «da verificare» (coverage unknown, never
    /// verified, no statistical field required, an https source), so the coverage of a property in a seeded region, in a comune that is not in the list, in a
    /// territory with no row or in a house outside Italy never says covered and never sends the host to the panel; only the staff turns a row into covered,
    /// and then back. The one pure rule says the same: an unknown (or undefined) coverage is «ask support», never «connect in the panel».
    /// </summary>
    [Fact]
    public async Task IstatCoverage_UnknownNeverClaimedCovered_Test()
    {
        var service = Service();

        // The seed, as the migration writes it and as the database now holds it.
        Assert.All(IstatRegionRuleSeed.Build(), AssertSeededRowIsToVerify);
        var table = await service.ListAsync(Ct);
        Assert.Equal(21, table.Count);
        Assert.All(table, AssertSeededRowIsToVerify);

        // A property in a seeded region: never covered, the next step is to ask support with the context of the ISTAT panel.
        var como = await AddPropertyAsync("013075");
        var seeded = await service.GetCoverageAsync(como.Id, Ct);
        Assert.Equal((true, true, "03", "Lombardia"), (seeded.Applies, seeded.RegionKnown, seeded.RegionIstatCode, seeded.RegionName));
        Assert.Equal((IstatCoverage.Unknown, IstatNextStep.AskSupport, OnboardingAssistContexts.PanelIstat), (seeded.Coverage, seeded.NextStep, seeded.AssistContext));
        Assert.Null(seeded.VerifiedAt);

        // No comune, a comune the list does not have, and a comune of a territory with no row (the region 04 has only its two provinces): unknown as well,
        // never the row of a neighbour.
        foreach (var comune in new string?[] { null, "123456", "999001" })
        {
            var unknown = await service.GetCoverageAsync((await AddPropertyAsync(comune)).Id, Ct);
            Assert.True(unknown.Applies);
            Assert.False(unknown.RegionKnown);
            Assert.Equal((IstatCoverage.Unknown, IstatNextStep.AskSupport, IstatDeadlineKind.Unknown), (unknown.Coverage, unknown.NextStep, unknown.DeadlineKind));
            Assert.Empty(unknown.RequiredGuestFields);
        }

        // A house outside Italy has nothing to do with the regional flows: no next step, nothing covered.
        var abroad = await service.GetCoverageAsync((await AddPropertyAsync("013075", country: "CH")).Id, Ct);
        Assert.Equal((false, IstatCoverage.Unknown), (abroad.Applies, abroad.Coverage));
        Assert.Null(abroad.NextStep);
        Assert.Null(abroad.RegionIstatCode);

        // The autonomous provinces answer with their own row; a comune of the region 04 takes the row of its province.
        var bolzano = await service.GetCoverageAsync((await AddPropertyAsync("021008")).Id, Ct);
        var trento = await service.GetCoverageAsync((await AddPropertyAsync("022205")).Id, Ct);
        Assert.Equal(("04", "BZ", "Provincia autonoma di Bolzano"), (bolzano.RegionIstatCode, bolzano.ProvinceCode, bolzano.RegionName));
        Assert.Equal(("04", "TN", "Provincia autonoma di Trento"), (trento.RegionIstatCode, trento.ProvinceCode, trento.RegionName));

        // Only the staff turns a row into covered (and then the host connects it in the panel), or into not covered (the portal of the region); the other
        // regions stay as seeded, and a row put back to unknown asks support again.
        await service.UpdateAsync(Update("03", coverage: IstatCoverage.Covered), Staff, Ct);
        var covered = await service.GetCoverageAsync(como.Id, Ct);
        Assert.Equal(IstatCoverage.Covered, covered.Coverage);
        Assert.Equal(IstatNextStep.ConnectInPanel, covered.NextStep);
        Assert.Null(covered.AssistContext);
        Assert.NotNull(covered.VerifiedAt);
        var firenze = await service.GetCoverageAsync((await AddPropertyAsync("048017")).Id, Ct);
        Assert.Equal((IstatCoverage.Unknown, IstatNextStep.AskSupport), (firenze.Coverage, firenze.NextStep));

        await service.UpdateAsync(Update("03", coverage: IstatCoverage.NotCovered), Staff, Ct);
        Assert.Equal(IstatNextStep.ManualPortal, (await service.GetCoverageAsync(como.Id, Ct)).NextStep);
        await service.UpdateAsync(Update("03", coverage: IstatCoverage.Unknown), Staff, Ct);
        var again = await service.GetCoverageAsync(como.Id, Ct);
        Assert.Equal((IstatCoverage.Unknown, IstatNextStep.AskSupport), (again.Coverage, again.NextStep));

        // The pure rule: nothing but a verified «covered» connects in the panel.
        Assert.Equal(IstatNextStep.ConnectInPanel, IstatRegionResolver.NextStep(IstatCoverage.Covered));
        Assert.Equal(IstatNextStep.ManualPortal, IstatRegionResolver.NextStep(IstatCoverage.NotCovered));
        Assert.Equal(IstatNextStep.AskSupport, IstatRegionResolver.NextStep(IstatCoverage.Unknown));
        Assert.Equal(IstatNextStep.AskSupport, IstatRegionResolver.NextStep((IstatCoverage)99));
        Assert.Equal(IstatCoverage.Unknown, new IstatRegionRule().OctorateCoverage);
    }

    /// <summary>
    /// Proof (privacy, data minimization): the guest page asks a statistical field only where the region requires it. With the seeded table (no row requires
    /// one) nobody is asked any; once the staff says a region requires two of them, the guests of that region are asked exactly those, in the order of the
    /// lists, and the guests of another region, of a house with no comune and of a region whose row says «verified: none required» or is not verified
    /// are asked none; a field that is not one of the three is refused and changes nothing.
    /// </summary>
    [Fact]
    public async Task IstatRequiredFields_OnlyWhenRegionRequires_Test()
    {
        var service = Service();
        var lombardia = await AddPropertyAsync("013075");
        var toscana = await AddPropertyAsync("048017");
        var noComune = await AddPropertyAsync(null);

        // The seeded table: nothing is asked anywhere.
        foreach (var property in new[] { lombardia, toscana, noComune })
            Assert.Empty(await PublicFieldsAsync(property));

        // Lombardia requires two fields (named in any order, once repeated): exactly those are asked, and only of its own guests.
        await service.UpdateAsync(Update("03", fields: ["TravelWay", "TourismType", "tourismtype"]), Staff, Ct);
        Assert.Equal(["TourismType", "TravelWay"], await PublicFieldsAsync(lombardia));
        Assert.Empty(await PublicFieldsAsync(toscana));
        Assert.Empty(await PublicFieldsAsync(noComune));
        var coverage = await service.GetCoverageAsync(lombardia.Id, Ct);
        Assert.Equal(["TourismType", "TravelWay"], coverage.RequiredGuestFields);
        Assert.True(coverage.RequiredGuestFieldsKnown);

        // «Verified: none required» asks nothing, and so does a row that is not verified (null), which says it is not known.
        await service.UpdateAsync(Update("03", fields: []), Staff, Ct);
        Assert.Empty(await PublicFieldsAsync(lombardia));
        Assert.True((await service.GetCoverageAsync(lombardia.Id, Ct)).RequiredGuestFieldsKnown);
        await service.UpdateAsync(Update("03", fields: null), Staff, Ct);
        Assert.Empty(await PublicFieldsAsync(lombardia));
        Assert.False((await service.GetCoverageAsync(lombardia.Id, Ct)).RequiredGuestFieldsKnown);

        // A name that is not a statistical field of the lists is refused, and the row keeps what it had.
        await service.UpdateAsync(Update("03", fields: ["EducationLevel"]), Staff, Ct);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.UpdateAsync(Update("03", fields: ["EducationLevel", "Religion"]), Staff, Ct));
        Assert.Equal(IstatRegionErrors.FieldsInvalidCode, refused.Code);
        Assert.Equal(["EducationLevel"], await PublicFieldsAsync(lombardia));
        Assert.Equal("EducationLevel", (await _db.IstatRegionRules.AsNoTracking().SingleAsync(r => r.RegionIstatCode == "03", Ct)).RequiredGuestFields);

        // The pure rule: no row, no field.
        Assert.Empty(IstatRegionResolver.RequiredFields(null));
        Assert.Empty(IstatRegionResolver.RequiredFields(new IstatRegionRule()));
        Assert.Equal(["TravelWay"], IstatRegionResolver.RequiredFields(new IstatRegionRule { RequiredGuestFields = "Nope,TravelWay" }));
    }

    // ─── The property endpoint ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (permission): the coverage of a property is read only by who reaches the property. The row-level check of the property runs with the production
    /// handler: a member that does not reach the property is refused (403), another organization's property is not found (404, like one that does not
    /// exist), and in both cases the service is never called; the endpoint is behind <c>OctorateOnboarding</c>, under the property read, and private.
    /// </summary>
    [Fact]
    public async Task IstatCoverage_PropertyEndpoint_RowLevelAuthorization_Test()
    {
        var orgA = Guid.Parse("6d5c4b3a-2918-4f7e-8d6c-5b4a39281702");
        var propertyOfA = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c21");
        var propertyOfB = Guid.Parse("5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a822");
        var regions = new Mock<IIstatRegionService>();
        regions
            .Setup(r => r.GetCoverageAsync(propertyOfA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IstatCoverageView(
                propertyOfA, true, true, "03", null, "Lombardia", "ROSS1000", null, IstatDeadlineKind.Monthly, 5, [], false,
                IstatCoverage.Unknown, ComplianceSenderType.Manual, IstatNextStep.AskSupport, OnboardingAssistContexts.PanelIstat, Source, null));
        var lookup = new Mock<IHostResourceLookup>();
        lookup
            .Setup(l => l.ForPropertyAsync(propertyOfA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(HostResource.ForProperty(new Property { Id = propertyOfA, OrgId = orgA, OwnerId = "auth0|owner-a", Name = "Casa del Lago" }));
        lookup
            .Setup(l => l.ForPropertyAsync(propertyOfB, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostResource?)null);

        PropertyIstatCoverageController Controller(ClaimsPrincipal user) =>
            new(regions.Object, lookup.Object, HostAuthorizationTestHarness.Create(orgA), NullLogger<PropertyIstatCoverageController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = user,
                        RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                    },
                },
            };

        var outsider = Controller(HostAuthorizationTestHarness.User("auth0|staff-a", "Staff"));
        Assert.IsType<ForbidResult>((await outsider.Get(propertyOfA, Ct)).Result);

        var manager = Controller(HostAuthorizationTestHarness.User("auth0|manager-a", "PropertyManager"));
        var notFound = Assert.IsType<ObjectResult>((await manager.Get(propertyOfB, Ct)).Result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.Equal("property_not_found", Assert.IsAssignableFrom<ProblemDetails>(notFound.Value).Extensions["code"]);
        Assert.Empty(regions.Invocations);

        var owner = Controller(HostAuthorizationTestHarness.User("auth0|owner-a", "PropertyOwner"));
        var dto = Assert.IsType<IstatCoverageDto>(Assert.IsType<OkObjectResult>((await owner.Get(propertyOfA, Ct)).Result).Value);
        Assert.Equal((propertyOfA, IstatCoverage.Unknown, IstatNextStep.AskSupport, "panelIstat"), (dto.PropertyId, dto.Coverage, dto.NextStep, dto.AssistContext));
        regions.Verify(r => r.GetCoverageAsync(propertyOfA, It.IsAny<CancellationToken>()), Times.Once);

        var controller = typeof(PropertyIstatCoverageController);
        Assert.Equal([FeatureFlags.OctorateOnboarding], controller.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(g => g.Flag));
        Assert.NotNull(controller.GetCustomAttribute<PrivateAnswerAttribute>());
        Assert.Equal("api/properties/{propertyId:guid}/istat-coverage", controller.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    // ─── What the seed holds, what a save does ───────────────────────────────────────────────────────────

    /// <summary>
    /// The seed is the matrix of the runbook and nothing more: a row for each of the 19 regions with one portal and for the two autonomous provinces (none
    /// for the region 04), with a monthly deadline only where the sources name the day (Lombardia, Valle d'Aosta and Umbria the 5th, Basilicata the 10th), a
    /// portal address only where they name it, no region that the sources do not identify given a system (Molise), and no statistical field required.
    /// </summary>
    [Fact]
    public void IstatRegionSeed_IsTheRunbookMatrix_NothingIsGuessed()
    {
        var rows = IstatRegionRuleSeed.Build();
        Assert.Equal(21, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        Assert.Equal(rows.Count, rows.Select(r => (r.RegionIstatCode, r.ProvinceCode)).Distinct().Count());

        foreach (var region in ItalianRegions.All.Where(r => r.IstatCode != "04"))
        {
            var row = Assert.Single(rows, r => r.RegionIstatCode == region.IstatCode);
            Assert.Null(row.ProvinceCode);
            Assert.Equal(region.Name, row.RegionName);
        }

        Assert.DoesNotContain(rows, r => r.RegionIstatCode == "04" && r.ProvinceCode is null);
        Assert.Equal(["BZ", "TN"], rows.Where(r => r.RegionIstatCode == "04").Select(r => r.ProvinceCode!).Order());

        var monthly = rows.Where(r => r.DeadlineKind == IstatDeadlineKind.Monthly).ToDictionary(r => r.RegionIstatCode, r => r.DeadlineDay);
        Assert.Equal(
            new Dictionary<string, int?> { ["02"] = 5, ["03"] = 5, ["10"] = 5, ["17"] = 10 }.OrderBy(p => p.Key),
            monthly.OrderBy(p => p.Key));
        Assert.All(rows.Where(r => r.DeadlineKind != IstatDeadlineKind.Monthly), r => Assert.Null(r.DeadlineDay));
        Assert.Equal(["03", "06", "19"], rows.Where(r => r.PortalUrl is not null).Select(r => r.RegionIstatCode).Order());
        Assert.Null(Assert.Single(rows, r => r.RegionIstatCode == "14").System);
        Assert.All(rows.Where(r => r.RegionIstatCode != "14"), r => Assert.False(string.IsNullOrWhiteSpace(r.System)));
        Assert.All(rows, r => Assert.Null(r.RequiredGuestFields));
    }

    /// <summary>
    /// A save replaces what the row holds, stamps it (who, when, verified now) and leaves one line of the audit trail with what changed; saving the same
    /// values again is a verification and says so; and a save never creates a row.
    /// </summary>
    [Fact]
    public async Task IstatRegions_Update_StampsTheRow_AndLeavesOneAuditLinePerSave()
    {
        var service = Service();
        var update = Update(
            "03", coverage: IstatCoverage.NotCovered, kind: IstatDeadlineKind.Monthly, day: 7, fields: ["EducationLevel"],
            portal: "https://portale.example.it/guida", source: "https://www.example.org/nuova-fonte", notes: "  Letta il 15/11.  ");

        var saved = await service.UpdateAsync(update, Staff, Ct);

        Assert.Equal((Staff, Now.UtcDateTime, Now.UtcDateTime), (saved.UpdatedByUserId, saved.UpdatedAt, saved.VerifiedAt));
        Assert.Equal(
            (IstatCoverage.NotCovered, IstatDeadlineKind.Monthly, 7, "EducationLevel", "Letta il 15/11."),
            (saved.OctorateCoverage, saved.DeadlineKind, saved.DeadlineDay, saved.RequiredGuestFields, saved.Notes));
        var first = Assert.Single(await _db.RegulatoryDataAuditEntries.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(
            (IstatRegionErrors.AuditEntityType, saved.Id, RegulatoryAuditAction.Updated, Staff, Now.UtcDateTime),
            (first.EntityType, first.EntityId, first.Action, first.ChangedByUserId, first.OccurredAt));
        Assert.Contains("octorateCoverage: Unknown -> NotCovered", first.Changes, StringComparison.Ordinal);
        Assert.Contains("deadlineDay: 5 -> 7", first.Changes, StringComparison.Ordinal);
        Assert.Contains("requiredGuestFields: (none) -> EducationLevel", first.Changes, StringComparison.Ordinal);
        Assert.Contains("verifiedAt: (none) -> 2026-11-15", first.Changes, StringComparison.Ordinal);

        // The same values again, checked on another day: only the verification is recorded.
        _clock.Advance(TimeSpan.FromDays(2));
        var recheck = update with
        {
            VerifiedAt = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc),
        };
        var verified = await service.UpdateAsync(recheck, "auth0|staff-two", Ct);

        Assert.Equal(new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), verified.VerifiedAt);
        var lines = await _db.RegulatoryDataAuditEntries.AsNoTracking().OrderBy(l => l.OccurredAt).ToListAsync(Ct);
        Assert.Equal(2, lines.Count);
        Assert.Equal((RegulatoryAuditAction.MarkedVerified, "auth0|staff-two"), (lines[1].Action, lines[1].ChangedByUserId));
        Assert.DoesNotContain("octorateCoverage", lines[1].Changes, StringComparison.Ordinal);
        Assert.Contains("verifiedAt: 2026-11-15 -> 2026-11-16", lines[1].Changes, StringComparison.Ordinal);
        Assert.Equal(21, await _db.IstatRegionRules.CountAsync(Ct));
    }

    /// <summary>
    /// What cannot be verified is refused and writes nothing: a source or a portal that is not an https address, a monthly deadline without a day (or with
    /// one outside 1 to 31) and a day on a deadline that is not monthly, and a territory that is not in the table (a province that has no row, the region
    /// 04 that has none of its own).
    /// </summary>
    [Fact]
    public async Task IstatRegions_Update_RefusesWhatCannotBeVerified_AndWritesNothing()
    {
        var service = Service();
        var before = await SnapshotAsync();
        var refusals = new (IstatRegionRuleUpdate Update, string Code)[]
        {
            (Update("03", source: "http://www.example.gov.it/non-sicuro"), IstatRegionErrors.SourceInvalidCode),
            (Update("03", source: "www.example.gov.it"), IstatRegionErrors.SourceInvalidCode),
            (Update("03", source: "   "), IstatRegionErrors.SourceInvalidCode),
            (Update("03", portal: "ftp://portale.example.it"), IstatRegionErrors.PortalInvalidCode),
            (Update("03", kind: IstatDeadlineKind.Monthly), IstatRegionErrors.DeadlineInvalidCode),
            (Update("03", kind: IstatDeadlineKind.Monthly, day: 0), IstatRegionErrors.DeadlineInvalidCode),
            (Update("03", kind: IstatDeadlineKind.Monthly, day: 32), IstatRegionErrors.DeadlineInvalidCode),
            (Update("03", kind: IstatDeadlineKind.Daily, day: 3), IstatRegionErrors.DeadlineInvalidCode),
            (Update("03", day: 5), IstatRegionErrors.DeadlineInvalidCode),
            (Update("03", fields: ["TourismType", "Hobby"]), IstatRegionErrors.FieldsInvalidCode),
        };

        foreach (var (update, code) in refusals)
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.UpdateAsync(update, Staff, Ct));
            Assert.Equal(code, refused.Code);
        }

        foreach (var update in new[] { Update("99"), Update("04"), Update("03", province: "BZ"), Update("04", province: "AO") })
        {
            var missing = await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateAsync(update, Staff, Ct));
            Assert.Equal(IstatRegionErrors.NotFoundCode, missing.Code);
        }

        Assert.Equal(before, await SnapshotAsync());
        Assert.Empty(await _db.RegulatoryDataAuditEntries.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(21, await _db.IstatRegionRules.CountAsync(Ct));

        // The province rows are found by their plate code, in either case.
        var province = await service.UpdateAsync(Update("04", province: "tn", coverage: IstatCoverage.NotCovered), Staff, Ct);
        Assert.Equal(("04", "TN", IstatCoverage.NotCovered), (province.RegionIstatCode, province.ProvinceCode, province.OctorateCoverage));
    }

    // ─── The deadline of the cockpit ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The deadline of the ISTAT month is the monthly day of the row of the table (none for a daily or unknown one, none for a region with no row), and
    /// <c>Compliance:IstatDeadlineDays</c> overrides it, by region code in any case, for that region only; the answer for a property shows the same day.
    /// </summary>
    [Fact]
    public async Task IstatDeadline_ComesFromTheTable_AndTheConfigurationOverridesIt()
    {
        var monthly = new IstatRegionRule { DeadlineKind = IstatDeadlineKind.Monthly, DeadlineDay = 5 };
        var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["lom"] = 12 };

        Assert.Equal(5, IstatRegionResolver.DeadlineDay(monthly, "LOM", null));
        Assert.Null(IstatRegionResolver.DeadlineDay(new IstatRegionRule { DeadlineKind = IstatDeadlineKind.Daily }, "LOM", null));
        Assert.Null(IstatRegionResolver.DeadlineDay(new IstatRegionRule(), "LOM", null));
        Assert.Null(IstatRegionResolver.DeadlineDay(new IstatRegionRule { DeadlineKind = IstatDeadlineKind.Monthly, DeadlineDay = 40 }, "LOM", null));
        Assert.Null(IstatRegionResolver.DeadlineDay(null, null, null));
        Assert.Equal(12, IstatRegionResolver.DeadlineDay(monthly, "LOM", overrides));
        Assert.Equal(12, IstatRegionResolver.DeadlineDay(new IstatRegionRule(), "LOM", overrides));
        Assert.Equal(5, IstatRegionResolver.DeadlineDay(monthly, "TOS", overrides));

        // The answer for a property: the table's day, then the configured one for its region only.
        var service = Service();
        var como = await AddPropertyAsync("013075");
        var firenze = await AddPropertyAsync("048017");
        var seeded = await service.GetCoverageAsync(como.Id, Ct);
        Assert.Equal((IstatDeadlineKind.Monthly, 5), (seeded.DeadlineKind, seeded.DeadlineDay));
        var undated = await service.GetCoverageAsync(firenze.Id, Ct);
        Assert.Equal(IstatDeadlineKind.Unknown, undated.DeadlineKind);
        Assert.Null(undated.DeadlineDay);

        _compliance.IstatDeadlineDays["LOM"] = 12;
        var overridden = await service.GetCoverageAsync(como.Id, Ct);
        Assert.Equal((IstatDeadlineKind.Monthly, 12), (overridden.DeadlineKind, overridden.DeadlineDay));
        Assert.Equal(IstatDeadlineKind.Unknown, (await service.GetCoverageAsync(firenze.Id, Ct)).DeadlineKind);
    }

    /// <summary>
    /// The cockpit reads the deadline from the table: with no row, or a row with no monthly day, a region is «to define» and has no ISTAT item; with a monthly
    /// day it has one, due on that day of the following month (red once it is past); and the configuration overrides the table's day.
    /// </summary>
    [Fact]
    public async Task IstatPeriodItems_FollowTheTable_AndTheConfigurationOverridesIt()
    {
        var world = await ComplianceStatusServiceTests.World.SeedAsync();
        await world.AddComuneAsync("015146", regionIstatCode: "03");
        world.Granted.ComuneIstatCode = "015146";
        world.Granted.City = "Milano";
        await world.SaveAsync(world.Granted);
        await world.AddStayAsync(world.Granted, checkInOffset: -10, nights: 2, status: BookingStatus.CheckedOut, touristTax: 12m);

        async Task<ComplianceStatusItem?> IstatItemAsync()
        {
            await using var db = world.Db();
            var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, Ct);
            return status.Items.SingleOrDefault(i => i.Kind == ComplianceItemKind.Istat && i.Period == "2026-10");
        }

        async Task SetRowAsync(IstatDeadlineKind kind, int? day)
        {
            await using var db = world.Db();
            var row = await db.IstatRegionRules.FirstOrDefaultAsync(r => r.RegionIstatCode == "03", Ct);
            if (row is null)
            {
                row = new IstatRegionRule { RegionIstatCode = "03", RegionName = "Lombardia", SourceUrl = Source };
                db.IstatRegionRules.Add(row);
            }

            row.DeadlineKind = kind;
            row.DeadlineDay = day;
            await db.SaveChangesAsync(Ct);
        }

        // No row, then a row whose deadline is daily or unknown: «to define», no item (the clock is 6 November, 11:00 in Rome).
        Assert.Null(await IstatItemAsync());
        await SetRowAsync(IstatDeadlineKind.Daily, null);
        Assert.Null(await IstatItemAsync());

        // The monthly day 5: October's communication was due on 5 November, so it is red.
        await SetRowAsync(IstatDeadlineKind.Monthly, 5);
        var overdue = Assert.IsType<ComplianceStatusItem>(await IstatItemAsync());
        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.DeadlineOverdue), (overdue.Verdict.Light, overdue.Verdict.Reason));
        Assert.Equal(RomeCalendar.StartOfDayUtc(new DateOnly(2026, 11, 5)), overdue.DueAt);

        // The day 10 of the table: still open on the 6th.
        await SetRowAsync(IstatDeadlineKind.Monthly, 10);
        var open = Assert.IsType<ComplianceStatusItem>(await IstatItemAsync());
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.PeriodOpen), (open.Verdict.Light, open.Verdict.Reason));
        Assert.Equal(RomeCalendar.StartOfDayUtc(new DateOnly(2026, 11, 10)), open.DueAt);

        // The configuration wins over the table, and alone is enough for a region the table does not date.
        world.Options.IstatDeadlineDays["LOM"] = 20;
        Assert.Equal(RomeCalendar.StartOfDayUtc(new DateOnly(2026, 11, 20)), (await IstatItemAsync())!.DueAt);
        await SetRowAsync(IstatDeadlineKind.Unknown, null);
        Assert.Equal(RomeCalendar.StartOfDayUtc(new DateOnly(2026, 11, 20)), (await IstatItemAsync())!.DueAt);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertSeededRowIsToVerify(IstatRegionRule row)
    {
        Assert.Equal(IstatCoverage.Unknown, row.OctorateCoverage);
        Assert.Null(row.VerifiedAt);
        Assert.Null(row.RequiredGuestFields);
        Assert.Null(row.UpdatedByUserId);
        Assert.True(Uri.TryCreate(row.SourceUrl, UriKind.Absolute, out var source) && source.Scheme == Uri.UriSchemeHttps, $"{row.RegionName}: the source is not https");
        if (row.PortalUrl is not null)
            Assert.StartsWith("https://", row.PortalUrl, StringComparison.Ordinal);
    }

    private IstatRegionService Service()
    {
        var senders = new Mock<IPropertySenderStore>();
        senders
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new PropertySenderInfo(
                id, _org, ComplianceSenderType.Manual, ComplianceSenderType.Manual, SenderSource.DefaultNotConnected, true, null, null));
        return new IstatRegionService(
            _db,
            senders.Object,
            Microsoft.Extensions.Options.Options.Create(_compliance),
            NullLogger<IstatRegionService>.Instance,
            _clock);
    }

    private static IstatRegionRuleUpdate Update(
        string region,
        string? province = null,
        IstatCoverage coverage = IstatCoverage.Unknown,
        IstatDeadlineKind kind = IstatDeadlineKind.Unknown,
        int? day = null,
        IReadOnlyCollection<string>? fields = null,
        string? portal = null,
        string source = Source,
        string? notes = null)
    {
        return new IstatRegionRuleUpdate(region, province, "ROSS1000", portal, kind, day, fields, coverage, source, null, notes);
    }

    private async Task<Property> AddPropertyAsync(string? comuneIstatCode, string country = "IT")
    {
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = _org,
            OwnerId = "auth0|host",
            Name = "Casa di prova",
            Address = "Via Test 1",
            City = "Prova",
            PostalCode = "00100",
            NightlyRate = 100m,
            IsActive = true,
            ComuneIstatCode = comuneIstatCode,
            Country = country,
        };
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();
        return property;
    }

    /// <summary>The statistical fields the guest page of a stay of <paramref name="property"/> asks, read through the public link of a new session.</summary>
    private async Task<IReadOnlyList<string>> PublicFieldsAsync(Property property)
    {
        var guest = new Guest { Id = Guid.NewGuid(), FirstName = "Mario", LastName = "Rossi", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = property.Id,
            GuestId = guest.Id,
            OrgId = _org,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(3),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(6),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
        };
        _db.Guests.Add(guest);
        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();

        var checkIn = new GuestCheckInService(_db, NullLogger<GuestCheckInService>.Instance);
        var token = await checkIn.CreateSessionAsync(booking.Id, _org);
        var view = await checkIn.GetPublicViewAsync(token);
        Assert.NotNull(view);
        return view.RequiredGuestFields ?? [];
    }

    /// <summary>The rows of the table as a comparable text, to prove that a refused save left every one as it was.</summary>
    private async Task<List<string>> SnapshotAsync()
    {
        var rows = await _db.IstatRegionRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(Ct);
        return rows
            .Select(r => string.Join('|', r.Id, r.System, r.PortalUrl, r.DeadlineKind, r.DeadlineDay, r.RequiredGuestFields, r.OctorateCoverage, r.SourceUrl, r.VerifiedAt, r.Notes, r.UpdatedAt, r.UpdatedByUserId))
            .ToList();
    }

    private static Comune ComuneRow(string istatCode, string name, string province, string regionIstatCode, string regionName) => new()
    {
        IstatCode = istatCode,
        Name = name,
        DisplayName = name,
        NormalizedName = name.ToLowerInvariant(),
        SearchText = name.ToLowerInvariant(),
        ProvinceCode = province,
        RegionIstatCode = regionIstatCode,
        RegionName = regionName,
    };

    private static ClaimsPrincipal Principal(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(role => new Claim(ClaimTypes.Role, role)), "test"));

    private static ControllerContext ContextOf(string userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
            RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
        },
    };

    private static List<MethodInfo> RoutedActions(Type controller) =>
        [
            .. controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any()),
        ];
}
