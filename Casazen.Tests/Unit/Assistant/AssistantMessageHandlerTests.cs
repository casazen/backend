using System.Reflection;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Tests.Unit.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US02/T01 (NF-67): a message of a guest, of a channel or of a direct booking, sends the assistant to work in a job of its own, only behind the flag and the switch of the
/// organization, once; and the job runs only behind the flag, under the org of the event.
/// </summary>
public sealed class AssistantMessageHandlerTests : IDisposable
{
    private readonly Casazen.Infrastructure.Data.AppDbContext _db = AssistantTestKit.NewDb();
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly List<Job> _created = [];
    private readonly TestFeatureFlags _flags = new(FeatureFlags.MessageAssistant);
    private readonly Guid _orgId = Guid.NewGuid();

    public AssistantMessageHandlerTests() =>
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job, IState>((job, _) => _created.Add(job)).Returns("job-1");

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AMessageOfAChannelOrOfADirectBooking_QueuesTheJobOfTheAssistant_WithTheOrgTheConversationAndTheMessage()
    {
        await SwitchOnAsync();
        var ota = Received(GuestMessageReceivedPayload.OtaSource, MessageThreadKey.ForOta(Guid.NewGuid()));
        var direct = Received(GuestMessageReceivedPayload.DirectSource, MessageThreadKey.ForDirect(Guid.NewGuid()));

        await Handler().HandleAsync(ota.Event, CancellationToken.None);
        await Handler().HandleAsync(direct.Event, CancellationToken.None);

        Assert.Equal(2, _created.Count);
        Assert.All(_created, job => Assert.Equal((typeof(AssistantDraftJob), nameof(AssistantDraftJob.RunAsync)), (job.Type, job.Method.Name)));
        Assert.Equal([_orgId, ota.Key.ToString(), ota.Event.EntityId], _created[0].Args.Take(3));
        Assert.Equal([_orgId, direct.Key.ToString(), direct.Event.EntityId], _created[1].Args.Take(3));
    }

    [Fact]
    public async Task TheFlagOff_TheSwitchOfTheOrgOff_AMessageAlreadyProcessed_OrAnotherEvent_QueueNothing()
    {
        var message = Received(GuestMessageReceivedPayload.OtaSource, MessageThreadKey.ForOta(Guid.NewGuid()));

        // The organization never switched its assistant on: its messages are not looked at, and no row is written for them.
        await Handler().HandleAsync(message.Event, CancellationToken.None);

        await SwitchOnAsync();
        _flags.Set(FeatureFlags.MessageAssistant, false);
        await Handler().HandleAsync(message.Event, CancellationToken.None);
        _flags.Set(FeatureFlags.MessageAssistant, true);

        _db.AssistantDecisionLogs.Add(new AssistantDecisionLog
        {
            OrgId = _orgId,
            ThreadKey = message.Key.ToString(),
            InboundMessageId = message.Event.EntityId,
            Decision = AssistantDecision.Drafted,
        });
        await _db.SaveChangesAsync();
        await Handler().HandleAsync(message.Event, CancellationToken.None);
        await Handler().HandleAsync(message.Event with { Type = DomainEventType.BookingConfirmed, EntityId = Guid.NewGuid() }, CancellationToken.None);

        Assert.Empty(_created);
    }

    [Fact]
    public async Task APayloadThatIsNotAConversationOfItsSource_QueuesNothing()
    {
        await SwitchOnAsync();
        var otaKeyAsDirect = Received(GuestMessageReceivedPayload.DirectSource, MessageThreadKey.ForOta(Guid.NewGuid()));

        await Handler().HandleAsync(otaKeyAsDirect.Event, CancellationToken.None);
        await Handler().HandleAsync(otaKeyAsDirect.Event with { PayloadJson = "not json" }, CancellationToken.None);
        await Handler().HandleAsync(otaKeyAsDirect.Event with { PayloadJson = "{\"threadKey\":\"ota:nope\",\"source\":\"Ota\"}" }, CancellationToken.None);

        Assert.Empty(_created);
    }

    [Fact]
    public async Task TheJob_RunsOnlyBehindTheFlag_UnderTheOrgOfTheEvent_AndIsOneRunPerMessage()
    {
        var drafts = new Mock<IAssistantDraftService>();
        var tenant = new Mock<IBackgroundTenantScope>();
        var key = MessageThreadKey.ForOta(Guid.NewGuid());
        var messageId = Guid.NewGuid();
        drafts
            .Setup(d => d.ProcessInboundAsync(_orgId, key, messageId, AssistantProcessOptions.Automatic, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssistantDraftOutcome(messageId, AssistantDecision.Drafted, null, null, null, null, null, null, false));
        var job = new AssistantDraftJob(drafts.Object, _flags, tenant.Object, NullLogger<AssistantDraftJob>.Instance);

        await job.RunAsync(_orgId, key.ToString(), messageId, CancellationToken.None);
        _flags.Set(FeatureFlags.MessageAssistant, false);
        await job.RunAsync(_orgId, key.ToString(), messageId, CancellationToken.None);

        tenant.Verify(t => t.EnterOrg(_orgId), Times.Once);
        drafts.Verify(d => d.ProcessInboundAsync(_orgId, key, messageId, AssistantProcessOptions.Automatic, It.IsAny<CancellationToken>()), Times.Once);

        var run = typeof(AssistantDraftJob).GetMethod(nameof(AssistantDraftJob.RunAsync))!;
        Assert.Contains("{2}", run.GetCustomAttribute<DisableConcurrentExecutionAttribute>()!.Resource, StringComparison.Ordinal);
        Assert.Equal(AssistantDraftJob.Attempts, run.GetCustomAttribute<AutomaticRetryAttribute>()!.Attempts);
    }

    private AssistantMessageHandler Handler() => new(_db, _flags, _jobs.Object, NullLogger<AssistantMessageHandler>.Instance);

    private async Task SwitchOnAsync()
    {
        _db.OrgAssistantRules.Add(new OrgAssistantRules { OrgId = _orgId, Enabled = true });
        await _db.SaveChangesAsync();
    }

    private (DomainEvent Event, MessageThreadKey Key) Received(string source, MessageThreadKey key) =>
        (new DomainEvent(
                Guid.NewGuid(),
                _orgId,
                DomainEventType.GuestMessageReceived,
                Guid.NewGuid(),
                DateTime.UtcNow,
                JsonSerializer.Serialize(new GuestMessageReceivedPayload(key.ToString(), source, Guid.NewGuid(), null, "Airbnb"))),
            key);
}
