using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F05/US02/T01: after an answer to a conversation of a channel that went out, written or approved by a person, the reply service tells the observers of the replies (the assistant
/// closes the «To do» of its escalated messages); an answer the channel refused, and one with no person behind it, tell nobody, and an observer that fails never fails the answer.
/// </summary>
public sealed class MessageReplyObserversTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private MessageHubWorld _world = null!;

    public async Task InitializeAsync() => _world = await MessageHubTestKit.SeedAsync(_db);

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task AnAnswerOfAPersonThatWentOut_TellsTheObservers_ARefusedOneOrOneWithNoUserDoesNot_AndAFailingObserverNeverFailsIt()
    {
        var rig = new MessageReplyRig(_db);
        rig.ChannelManagerAccepts("90001");
        var observer = new Mock<IThreadReplyObserver>();
        var failing = new Mock<IThreadReplyObserver>();
        failing
            .Setup(o => o.OnHostReplyAsync(It.IsAny<Guid>(), It.IsAny<MessageThreadKey>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The observer failed."));
        var service = Service(rig, failing.Object, observer.Object);
        var key = MessageThreadKey.ForOta(_world.GrantedReservation.Id);

        var sent = await service.ReplyAsync(_world.Scope.OrgWide, key, "Benvenuti", "key-person-0001", "auth0|anna", MessageOrigin.Person, null);
        var automatic = await service.ReplyAsync(_world.Scope.OrgWide, key, "Il parcheggio è gratuito", "key-assistant-0001", null, MessageOrigin.Assistant, "parking");

        rig.Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRequestRejectedException(400));
        var refused = await service.ReplyAsync(_world.Scope.OrgWide, key, "Rifiutato", "key-person-0002", "auth0|anna", MessageOrigin.Person, null);

        Assert.Equal(OtaMessageDeliveryState.Sent, sent.State);
        Assert.Equal(OtaMessageDeliveryState.Sent, automatic.State);
        Assert.Equal(OtaMessageDeliveryState.Error, refused.State);
        observer.Verify(o => o.OnHostReplyAsync(_world.Scope.OrgId, key, It.IsAny<CancellationToken>()), Times.Once);
    }

    private MessageReplyService Service(MessageReplyRig rig, params IThreadReplyObserver[] observers) =>
        new(
            _db,
            rig.Direct.Object,
            rig.Mapping.Object,
            rig.Client.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            rig.Targets.Object,
            rig.Flags,
            rig.Emails,
            new ActivityLog(_db),
            rig.Cache,
            rig.Log,
            new FixedTimeProvider(MessageHubTestKit.Now),
            observers);
}
