using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E24/F01/US01/T02 (NF-120, the review of slice S015) on a real PostgreSQL database: the money rule of the cleaning at check-out, one cleaning for each stay, holds when the triggers of a
/// stay arrive together. The routine of the check-out time and the one of the departure the host registered fire at the same moment, each on a context of its own: the advisory lock of
/// the stay and the unique index on the org and the booking serialize them, so one makes the request and its key and the others find them. EF InMemory proves the check that comes after the
/// lock, never the lock: it takes none and keeps no unique index.
/// </summary>
public class CleaningAutomationPostgresTests : IAsyncLifetime
{
    private const int Together = 4;

    private PostgresTestDatabase? _database;
    private HostScopeWorld _world = null!;
    private Guid _bookingId;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateMigratedAsync();
        await using var db = _database.CreateContext();
        _world = await HostScopeScenario.SeedAsync(db);

        // The stay is the one already checked out of the first property (it has no cleaning request); the supplier org of the world covers its comune and has an agreed price for it.
        _bookingId = (await db.Bookings.AsNoTracking().SingleAsync(b => b.PropertyId == _world.Granted.Id && b.Status == BookingStatus.CheckedOut)).Id;
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = _world.SupplierOrgId,
            Email = "fornitore@example.com",
            LegalName = "Fornitore Srl",
            Phone = "+39 06 123456",
            Status = SupplierStatus.Active,
            ComuniJson = $"[\"{_world.Granted.City}\"]",
            CategoriesJson = "[\"cleaning\"]",
        });
        db.PropertyCleaningSetups.Add(new PropertyCleaningSetup
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Granted.Id,
            CleanerKind = PropertyCleanerKind.Supplier,
            SupplierOrgId = _world.SupplierOrgId,
            PriceCents = 7500,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    /// <summary>
    /// Money rule: the triggers of one stay at the same moment make one cleaning. Of four calls together one creates the request and the key, the others find the key (the lock of the stay
    /// makes them wait for the commit of the first); the stay has one request of category cleaning, one <c>StayCleaning</c> row that points at it, and the supplier is told once.
    /// </summary>
    [PostgresFact]
    public async Task CleaningAutomation_TriggersAtTheSameMoment_MakeOneRequestAndOneKey_Test()
    {
        var emails = new RecordingEmailQueue();

        var results = await CreateTogetherAsync(emails);

        Assert.Equal(1, results.Count(r => r.Outcome == CleaningAutomationOutcome.Created));
        Assert.All(results.Where(r => r.Outcome != CleaningAutomationOutcome.Created), r => Assert.Equal(CleaningAutomationOutcome.AlreadyCreated, r.Outcome));
        await using var db = _database!.CreateContext();
        var request = Assert.Single(await db.ServiceRequests.AsNoTracking().Where(r => r.BookingId == _bookingId && r.Category == ServiceCategories.Cleaning).ToListAsync());
        var key = Assert.Single(await db.StayCleanings.AsNoTracking().Where(c => c.BookingId == _bookingId).ToListAsync());
        Assert.Equal((_world.OrgId, request.Id), (key.OrgId, key.ServiceRequestId));
        Assert.All(results, r => Assert.Equal(request.Id, r.ServiceRequestId));
        Assert.Single(emails.Snapshot(), e => e.Template == EmailTemplates.Names.ServiceRequestCreated);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The same call from <see cref="Together"/> triggers, each with a context and a service graph of its own, released together.</summary>
    private async Task<CleaningAutomationResult[]> CreateTogetherAsync(RecordingEmailQueue emails)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, Together)
            .Select(async _ =>
            {
                await using var db = _database!.CreateContext();
                var clock = new FakeTimeProvider(ServiceRequestScenario.Instant);
                using var kit = new ServiceRequestTestKit(db, emails, clock: clock);
                kit.Flags.Set(FeatureFlags.Routines, true);
                var automation = new CleaningAutomationService(
                    db,
                    kit.Service,
                    new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, clock),
                    Mock.Of<IAlertRouter>(),
                    kit.Flags,
                    clock,
                    NullLogger<CleaningAutomationService>.Instance);
                await start.Task;
                return await automation.CreateForStayAsync(_world.OrgId, _bookingId);
            })
            .ToList();

        start.SetResult();
        return await Task.WhenAll(calls);
    }
}
