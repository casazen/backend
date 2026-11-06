using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E08/F01/US02/T01 (NF-74, AC-NF74-2): what the availability the host reads before entering a booking says of the channel manager. On EF
/// InMemory, on the world of <see cref="HostScopeScenario"/> (a confirmed stay Oct 10-13 on each property; the clock is Oct 9 in Rome): Octorate's
/// "not available" makes the stay occupied with a conflict of its own, an Octorate that cannot be asked is only said, and nothing is asked for
/// dates that cannot be a stay.
/// </summary>
public class HostAvailabilityChannelManagerTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private readonly Mock<IChannelManagerAvailability> _channelManager = new();
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task CheckAsync_FreeDatesThatOctorateRefuses_AreOccupiedWithAConflictOfTheChannelManager()
    {
        Answers(new ChannelManagerAvailability(ChannelManagerAnswer.NotAvailable, "Not sellable"));

        var result = await Service().CheckAsync(_world.Granted, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 22), cancellationToken: Ct);

        Assert.Equal(HostAvailabilityState.Occupied, result.State);
        Assert.True(result.ChannelManagerChecked);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(HostAvailabilityConflictType.ChannelManager, conflict.Type);
        Assert.Equal(Guid.Empty, conflict.Id);
        Assert.Null(conflict.Title);
        Assert.Null(conflict.Channel);
        Assert.Equal(new DateOnly(2026, 10, 20), conflict.From);
        Assert.Equal(new DateOnly(2026, 10, 22), conflict.To);
        // CasaZen's calendar was free: there is no window to suggest, as the free dates are the ones Octorate refuses.
        Assert.Null(result.NearestFreeWindow);
        Assert.Equal(2, result.Nights);
        _channelManager.Verify(
            c => c.CheckAsync(_world.Granted.Id, _world.OrgId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 22), 1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckAsync_DatesCasaZenHasTakenAndOctorateRefuses_ListBothInOrderOfArrival_AndKeepTheWindowOfCasaZen()
    {
        Answers(new ChannelManagerAvailability(ChannelManagerAnswer.NotAvailable));

        var result = await Service().CheckAsync(_world.Granted, new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 14), cancellationToken: Ct);

        Assert.Equal(HostAvailabilityState.Occupied, result.State);
        Assert.Equal(
            [HostAvailabilityConflictType.Booking, HostAvailabilityConflictType.ChannelManager],
            result.Conflicts.Select(c => c.Type));
        Assert.Equal(new DateOnly(2026, 10, 10), result.Conflicts[0].From);
        Assert.Equal(new DateOnly(2026, 10, 11), result.Conflicts[1].From);
        Assert.NotNull(result.NearestFreeWindow);
        Assert.True(result.ChannelManagerChecked);
    }

    [Theory]
    [InlineData(ChannelManagerAnswer.Available, true)]
    [InlineData(ChannelManagerAnswer.Unanswered, false)]
    [InlineData(ChannelManagerAnswer.NotMirrored, null)]
    public async Task CheckAsync_OctorateThatDoesNotRefuse_ChangesNothingButTheWordAboutIt(ChannelManagerAnswer answer, bool? expected)
    {
        Answers(new ChannelManagerAvailability(answer));

        var result = await Service().CheckAsync(_world.Granted, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 22), cancellationToken: Ct);

        Assert.Equal(HostAvailabilityState.Ok, result.State);
        Assert.Empty(result.Conflicts);
        Assert.Null(result.NearestFreeWindow);
        Assert.Equal(expected, result.ChannelManagerChecked);
    }

    [Fact]
    public async Task CheckAsync_DatesThatCannotBeAStay_AreNeverAskedToOctorate()
    {
        Answers(new ChannelManagerAvailability(ChannelManagerAnswer.NotAvailable));

        // The arrival is before today (Oct 9), and the departure is not after the arrival.
        var past = await Service().CheckAsync(_world.Granted, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 3), cancellationToken: Ct);
        var backwards = await Service().CheckAsync(_world.Granted, new DateOnly(2026, 10, 22), new DateOnly(2026, 10, 20), cancellationToken: Ct);

        Assert.Equal(HostAvailabilityState.Invalid, past.State);
        Assert.Equal(HostAvailabilityState.Invalid, backwards.State);
        Assert.Null(past.ChannelManagerChecked);
        Assert.Null(backwards.ChannelManagerChecked);
        _channelManager.Verify(
            c => c.CheckAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckAsync_TheReservationOfAPortalTheHostIsAboutToComplete_IsNotAskedToOctorate()
    {
        Answers(new ChannelManagerAvailability(ChannelManagerAnswer.NotAvailable));
        _db.ChangeTracker.Clear();
        var feed = await _db.PropertyICalFeeds.AsNoTracking().FirstAsync(f => f.PropertyId == _world.Granted.Id);
        _db.CalendarBlocks.Add(new CalendarBlock
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Granted.Id,
            Source = CalendarBlockSource.ICalImport,
            FeedId = feed.Id,
            StartUtc = HostScopeScenario.Day(2026, 10, 20),
            EndUtc = HostScopeScenario.Day(2026, 10, 22),
            Summary = "Reserved",
        });
        await _db.SaveChangesAsync();

        var result = await Service().CheckAsync(_world.Granted, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 22), cancellationToken: Ct);

        // Exactly the dates of the block: the host completes it with the guest's name, and that reservation is what Octorate would answer with.
        Assert.Equal(HostAvailabilityState.LinkableBlock, result.State);
        Assert.Null(result.ChannelManagerChecked);
        Assert.Equal(HostAvailabilityConflictType.Ical, Assert.Single(result.Conflicts).Type);
        _channelManager.Verify(
            c => c.CheckAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckAsync_WithoutAChannelManager_IsWhatItAlwaysWas()
    {
        var service = new HostAvailabilityService(_db, new ConfigurationBuilder().Build(), _clock);

        var result = await service.CheckAsync(_world.Granted, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 22), cancellationToken: Ct);

        Assert.Equal(HostAvailabilityState.Ok, result.State);
        Assert.Null(result.ChannelManagerChecked);
    }

    private HostAvailabilityService Service() => new(_db, new ConfigurationBuilder().Build(), _clock, _channelManager.Object);

    private void Answers(ChannelManagerAvailability answer) =>
        _channelManager
            .Setup(c => c.CheckAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(answer);
}
