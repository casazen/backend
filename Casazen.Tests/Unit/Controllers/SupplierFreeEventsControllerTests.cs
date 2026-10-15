using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ICal;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Hangfire;
using Hangfire.States;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// <c>api/supplier/calendar/free-events</c>: the supplier lists and answers about the "Available" events of its calendar. The
/// caller's supplier org comes from the resolver only, never from the request, so the id of another supplier's event is a 404.
/// </summary>
public class SupplierFreeEventsControllerTests
{
    private static readonly Guid Mine = Guid.Parse("a1111111-1111-4111-8111-111111111111");
    private static readonly Guid Theirs = Guid.Parse("b2222222-2222-4222-8222-222222222222");

    [Fact]
    public void Controller_RequiresTheSupplierPolicyAndTheNewActionsAreNotAnonymous()
    {
        var controller = typeof(SupplierProfileController);
        var authorize = controller.GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal("RequireSupplier", authorize?.Policy);
        foreach (var name in new[] { nameof(SupplierProfileController.GetFreeCalendarEvents), nameof(SupplierProfileController.DecideFreeCalendarEvent) })
            Assert.Null(controller.GetMethod(name)!.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public async Task GetFreeCalendarEvents_Supplier_ReturnsOnlyItsPendingEventsAndTheCount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mine = await fixture.SeedAsync(Mine, "Ferie", new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
        await fixture.SeedAsync(Mine, "Fatto", new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc), SupplierFreeEventDecision.Available);
        await fixture.SeedAsync(Theirs, "Altrui", new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc));

        var result = await fixture.Controller(Mine).GetFreeCalendarEvents(fixture.Service, CancellationToken.None);

        var body = Assert.IsType<SupplierFreeEventsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var item = Assert.Single(body.Items);
        Assert.Equal(mine, item.Id);
        Assert.Equal("Ferie", item.Summary);
        Assert.Equal(1, body.PendingCount);
    }

    [Fact]
    public async Task GetFreeCalendarEvents_NoSupplier_Returns404()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Controller(null).GetFreeCalendarEvents(fixture.Service, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task DecideFreeCalendarEvent_Available_Returns204AndQueuesTheSyncToFreeTheDays()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.SeedAsync(Mine, "Ferie", DateTime.UtcNow);

        var result = await fixture.Controller(Mine).DecideFreeCalendarEvent(
            id, new SupplierFreeEventDecisionRequest { Decision = "Available" }, fixture.Service, fixture.Jobs.Object, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        fixture.Jobs.Verify(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<IState>()), Times.Once);
        Assert.Equal(SupplierFreeEventDecision.Available, (await fixture.Db.SupplierFreeCalendarEvents.AsNoTracking().SingleAsync()).Decision);
    }

    [Fact]
    public async Task DecideFreeCalendarEvent_Busy_Returns204AndQueuesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.SeedAsync(Mine, "Ferie", DateTime.UtcNow);

        var result = await fixture.Controller(Mine).DecideFreeCalendarEvent(
            id, new SupplierFreeEventDecisionRequest { Decision = "Busy" }, fixture.Service, fixture.Jobs.Object, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        fixture.Jobs.Verify(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task DecideFreeCalendarEvent_EventOfAnotherSupplier_Returns404AndChangesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var theirs = await fixture.SeedAsync(Theirs, "Altrui", DateTime.UtcNow);

        var result = await fixture.Controller(Mine).DecideFreeCalendarEvent(
            theirs, new SupplierFreeEventDecisionRequest { Decision = "Available" }, fixture.Service, fixture.Jobs.Object, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(SupplierFreeEventDecision.Pending, (await fixture.Db.SupplierFreeCalendarEvents.AsNoTracking().SingleAsync()).Decision);
        fixture.Jobs.Verify(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task DecideFreeCalendarEvent_InvalidAnswer_ThrowsTheRuleCodeForTheMiddleware()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.SeedAsync(Mine, "Ferie", DateTime.UtcNow);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => fixture.Controller(Mine).DecideFreeCalendarEvent(
            id, new SupplierFreeEventDecisionRequest { Decision = "Pending" }, fixture.Service, fixture.Jobs.Object, CancellationToken.None));

        Assert.Equal("free_event_decision_invalid", ex.Code);
    }

    [Fact]
    public async Task DecideFreeCalendarEvent_AlreadyAnswered_ThrowsTheConflictCode()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.SeedAsync(Mine, "Ferie", DateTime.UtcNow, SupplierFreeEventDecision.Busy);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => fixture.Controller(Mine).DecideFreeCalendarEvent(
            id, new SupplierFreeEventDecisionRequest { Decision = "Available" }, fixture.Service, fixture.Jobs.Object, CancellationToken.None));

        Assert.Equal("free_event_already_decided", ex.Code);
    }

    [Fact]
    public async Task GetCalendarStatus_PendingEvents_ExposesTheCountForTheDashboard()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(Mine, "Ferie", DateTime.UtcNow);
        await fixture.SeedAsync(Mine, "Altro", DateTime.UtcNow);
        await fixture.SeedAsync(Theirs, "Altrui", DateTime.UtcNow);

        var result = await fixture.Controller(Mine).GetCalendarStatus(fixture.Service, fixture.Localizer.Object, CancellationToken.None);

        var dto = Assert.IsType<CalendarSyncStatusDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, dto.PendingFreeEventsCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required AppDbContext Db { get; init; }

        public SupplierFreeEventService Service => new(Db, NullLogger<SupplierFreeEventService>.Instance);

        public Mock<IBackgroundJobClient> Jobs { get; } = new();

        public Mock<IStringLocalizer<SharedResources>> Localizer { get; } = new();

        public static async Task<Fixture> CreateAsync()
        {
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            foreach (var org in new[] { Mine, Theirs })
            {
                db.SupplierProfiles.Add(new SupplierProfile
                {
                    OrgId = org,
                    LegalName = "Fornitore Test Srl",
                    Phone = "+39 06 000000",
                    Email = $"{org:N}@example.com",
                    IcalFeedUrl = "https://feeds.example.com/cal.ics",
                    CalendarSyncType = CalendarSyncType.ICalFeed,
                    CalendarSyncStatus = SupplierCalendarSyncStatus.Success,
                });
            }

            await db.SaveChangesAsync();
            var fixture = new Fixture { Db = db };
            fixture.Localizer.Setup(l => l[It.IsAny<string>()]).Returns((string n) => new LocalizedString(n, n, resourceNotFound: true));
            fixture.Localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()])
                .Returns((string n, object[] _) => new LocalizedString(n, n, resourceNotFound: true));
            return fixture;
        }

        public async Task<Guid> SeedAsync(
            Guid orgId, string summary, DateTime next, SupplierFreeEventDecision decision = SupplierFreeEventDecision.Pending)
        {
            var row = new SupplierFreeCalendarEvent
            {
                OrgId = orgId,
                DecisionKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                UidHash = new string('a', 64),
                Summary = summary,
                IsAllDay = true,
                NextStartUtc = next,
                NextEndUtc = next.AddDays(1),
                Decision = decision,
            };
            Db.SupplierFreeCalendarEvents.Add(row);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return row.Id;
        }

        public SupplierProfileController Controller(Guid? supplierOrg)
        {
            var resolver = new Mock<ISupplierOrgContextResolver>();
            resolver.Setup(r => r.GetOrProvisionSupplierOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(supplierOrg);
            resolver.Setup(r => r.GetLinkedSupplierOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(supplierOrg);

            var sync = new CalendarSyncService(
                Db,
                Mock.Of<Casazen.Infrastructure.Http.ISafeExternalHttpClient>(),
                ICalTestServices.ImportService(),
                Mock.Of<IServiceScopeFactory>(),
                NullLogger<CalendarSyncService>.Instance);

            var supplierService = new Mock<ISupplierService>();
            supplierService
                .Setup(s => s.GetProfileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns((Guid org, CancellationToken _) => Db.SupplierProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.OrgId == org));

            var services = new ServiceCollection().AddSingleton(Localizer.Object).BuildServiceProvider();
            return new SupplierProfileController(
                supplierService.Object,
                resolver.Object,
                Mock.Of<IComuneDirectory>(),
                sync,
                Mock.Of<IImageStorageService>(),
                NullLogger<SupplierProfileController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } },
            };
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
