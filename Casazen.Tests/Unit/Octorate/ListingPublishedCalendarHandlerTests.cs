using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E08/F05/US02/T02 (NF-29, NF-36): the first publication of a listing reaches the calendar. The consumer of <c>ListingPublished</c> records one change for the whole horizon of the
/// property, once for each publication however many times the event is delivered, so a new listing gets its prices, its availability and its restrictions without a second action of
/// the host.
/// </summary>
public class ListingPublishedCalendarHandlerTests
{
    private static readonly Guid OrgId = Guid.Parse("4e6a8c02-3d5f-4a7b-9c1d-2e4f6a8b0c01");

    // 9 October 2026, 10:00 UTC: the 9th in Rome.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 9);

    [Fact]
    public async Task Handle_RecordsOneChangeForTheWholeHorizon_OncePerPublication_HoweverManyTimesTheEventIsDelivered()
    {
        var h = new Handler();
        var published = h.Event();

        await h.Service().HandleAsync(published, CancellationToken.None);
        // The bus delivers an event again when another handler of it failed: a second delivery, in a context of its own, leaves.
        await h.Service().HandleAsync(published, CancellationToken.None);

        var db = h.NewDb();
        var process = await db.CalendarChangeProcesses.AsNoTracking().SingleAsync();
        Assert.Equal((ListingPublishedCalendarHandler.ProcessIdOf(published.Id), CalendarChangeKind.Publication, CalendarChangeState.Queued, 1), (process.Id, process.Kind, process.State, process.PropertyCount));
        Assert.Null(process.RequestedByUserId);
        var chunks = await db.CalendarChangeChunks.AsNoTracking().OrderBy(c => c.FromDate).ToListAsync();
        // 365 days from today in calls of 62: six chunks that follow one another from today to the last day of the horizon.
        Assert.Equal(6, chunks.Count);
        Assert.Equal((Today, Today.AddDays(364)), (chunks[0].FromDate, chunks[^1].ToDate));
        Assert.All(chunks, c => Assert.Equal((CalendarChunkState.Pending, h.PropertyId, process.Id), (c.State, c.PropertyId, c.ProcessId)));
        Assert.Equal(365, process.DayCount);
        h.VerifyRuns(Times.Once());

        // Another publication of the same property (the listing was unpublished and published again) is another change.
        await h.Service().HandleAsync(h.Event(), CancellationToken.None);
        Assert.Equal(2, await h.NewDb().CalendarChangeProcesses.CountAsync());
        h.VerifyRuns(Times.Exactly(2));
    }

    [Fact]
    public async Task Handle_TheHorizonIsTheOneOfTheConfiguration_ANotMirroredPropertyRecordsAnAppliedChange_AndAConnectorThatIsOffRecordsNothing()
    {
        var h = new Handler();
        h.Options.HorizonDays = 90;

        await h.Service().HandleAsync(h.Event(), CancellationToken.None);

        var process = await h.NewDb().CalendarChangeProcesses.AsNoTracking().SingleAsync();
        Assert.Equal(90, process.DayCount);
        Assert.Equal(2, await h.NewDb().CalendarChangeChunks.CountAsync());

        // Not on the channel manager: the change stays in CasaZen, visible, with nothing to send and no run.
        var notMirrored = new Handler();
        notMirrored.Targets.Setup(t => t.LookupAsync(notMirrored.PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(OctorateTargetLookup.NotMirrored);
        await notMirrored.Service().HandleAsync(notMirrored.Event(), CancellationToken.None);
        var applied = await notMirrored.NewDb().CalendarChangeProcesses.AsNoTracking().SingleAsync();
        Assert.Equal((CalendarChangeState.Applied, 0), (applied.State, await notMirrored.NewDb().CalendarChangeChunks.CountAsync()));
        notMirrored.VerifyRuns(Times.Never());

        // The connector off: nothing at all.
        var off = new Handler(connector: false);
        await off.Service().HandleAsync(off.Event(), CancellationToken.None);
        Assert.Equal(0, await off.NewDb().CalendarChangeProcesses.CountAsync());
    }

    [Fact]
    public async Task Handle_APropertyThatIsGone_OrAnEventWithoutOne_RecordsNothing_AndIsNotAnError()
    {
        var h = new Handler();

        await h.Service().HandleAsync(h.Event(propertyId: Guid.NewGuid()), CancellationToken.None);
        await h.Service().HandleAsync(h.Event(propertyId: Guid.Empty), CancellationToken.None);

        Assert.Equal(0, await h.NewDb().CalendarChangeProcesses.CountAsync());
        h.VerifyRuns(Times.Never());
    }

    private sealed class Handler
    {
        private readonly string _database = $"calendar-publication-{Guid.NewGuid():N}";
        private readonly bool _connector;

        public Handler(bool connector = true)
        {
            _connector = connector;
            var property = HostScopeScenario.NewProperty(OrgId, "auth0|owner", "Casa del Lago");
            using var seed = NewDb();
            seed.Properties.Add(property);
            seed.SaveChanges();
            PropertyId = property.Id;
            Targets
                .Setup(t => t.LookupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(OctorateTargetLookup.Of(new OctorateBookingTarget("ACC1", "956528")));
        }

        public Guid PropertyId { get; }

        public OctorateCalendarOptions Options { get; } = new();

        public Mock<IOctorateBookingTargets> Targets { get; } = new();

        public Mock<IBackgroundJobClient> Jobs { get; } = new();

        public AppDbContext NewDb() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

        /// <summary>The event of a publication, as the outbox delivers it: a row of its own for each publication.</summary>
        public ListingPublishedEvent Event(Guid? propertyId = null)
        {
            var property = propertyId ?? PropertyId;
            var payload = JsonSerializer.Serialize(new ListingPublishedPayload(property, "ACC1"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new ListingPublishedEvent(Guid.NewGuid(), OrgId, DomainEventType.ListingPublished, property, Now.UtcDateTime, payload);
        }

        /// <summary>The handler in a context of its own, as every delivery of the bus gets one.</summary>
        public ListingPublishedCalendarHandler Service()
        {
            var flags = new Mock<IFeatureFlags>();
            flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(_connector);
            var db = NewDb();
            var options = Microsoft.Extensions.Options.Options.Create(Options);
            var clock = new FixedTimeProvider(Now);
            var recorder = new CalendarChangeRecorder(db, Targets.Object, flags.Object, options, clock, NullLogger<CalendarChangeRecorder>.Instance);
            return new ListingPublishedCalendarHandler(
                db, recorder, flags.Object, options, clock, NullLogger<ListingPublishedCalendarHandler>.Instance, Jobs.Object);
        }

        public void VerifyRuns(Times times) =>
            Jobs.Verify(
                j => j.Create(
                    It.Is<Job>(job => job.Type == typeof(CalendarChangeProcessJob) && job.Method.Name == nameof(CalendarChangeProcessJob.RunAsync)),
                    It.IsAny<IState>()),
                times);
    }
}
