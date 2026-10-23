using Casazen.Core.Services;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>UI-12a real time: the job tells the open sessions of each user it wrote, and of nobody else.</summary>
public class InAppNotificationJobPublishTests : IDisposable
{
    private readonly InAppNotificationTestKit _kit = new();
    private readonly Mock<IInAppNotificationPublisher> _publisher = new();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task CreateAsync_WritesTheRows_PublishesOncePerWrittenUser_WithTypeAndEntity()
    {
        var world = await _kit.SeedWorldAsync();
        var job = _kit.Job(_publisher.Object);

        await job.CreateAsync("booking-message:1", Queued(world.BookingId), CancellationToken.None);

        _publisher.Verify(p => p.PublishAsync(InAppNotificationTestKit.World.ManagerId, PushTypes.GuestMessage, world.BookingId, It.IsAny<CancellationToken>()), Times.Once);
        _publisher.Verify(p => p.PublishAsync(InAppNotificationTestKit.World.OwnerId, PushTypes.GuestMessage, world.BookingId, It.IsAny<CancellationToken>()), Times.Once);
        _publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateAsync_UsersToldAlready_AreNotPublishedAgain()
    {
        var world = await _kit.SeedWorldAsync();
        var job = _kit.Job(_publisher.Object);
        await job.CreateAsync("booking-message:1", Queued(world.BookingId), CancellationToken.None);
        _publisher.Invocations.Clear();

        await job.CreateAsync("booking-message:1", Queued(world.BookingId), CancellationToken.None);

        _publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateAsync_NobodyToTell_PublishesNothing()
    {
        var job = _kit.Job(_publisher.Object);

        await job.CreateAsync("booking-message:2", Queued(Guid.NewGuid()), CancellationToken.None);

        _publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateAsync_ThePublisherFails_TheRowsStayAndTheJobSucceeds()
    {
        var world = await _kit.SeedWorldAsync();
        _publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        await _kit.Job(_publisher.Object).CreateAsync("booking-message:3", Queued(world.BookingId), CancellationToken.None);

        Assert.Equal(2, (await _kit.RowsAsync()).Count);
    }

    private static Casazen.Infrastructure.Push.QueuedInAppNotification Queued(Guid bookingId) => new()
    {
        AudienceKind = PushAudienceKind.BookingHosts,
        AudienceId = bookingId,
        Type = PushTypes.GuestMessage,
        EntityId = bookingId,
        OccurredAt = InAppNotificationTestKit.Now,
    };
}
