using System.Globalization;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US02/T01 (NF-66, NF-67, PO-08, PO-09): an incoming message of a guest becomes a draft a person approves. The three proofs of the brief (no name, contact or code reaches the
/// provider; with the default gate nothing is sent; the simulator writes and sends nothing) and the steps of the pipeline: the decision log, the escalation with its «To do», the urgent
/// alert, the messages that never reach the model, once per message, the reach of the person who asks for a draft, the draft that supersedes the waiting one, and the «To do» closed by the
/// answer of a person.
/// </summary>
public sealed class AssistantDraftPipelineTests : IDisposable
{
    private const string GuestQuestion =
        "Ciao, sono Giulia Verdi (prenotazione K7M2Q-9X4BN, codice Airbnb HMXYZ987). Scrivetemi a giulia.verdi@example.com o al 333 1234567, "
        + "codice fiscale RSSMRA80A01H501U, pratica 12345678. Dove posso parcheggiare?";

    private const string UrgentClassification =
        """{"situation":"malfunction","situationMatch":0.8,"urgency":"urgent","sensitiveCategories":["damage"],"language":"it"}""";

    private readonly AssistantDraftRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ─── The proofs ───

    [Fact]
    public async Task AssistantPrompt_NamesContactsCodes_ReplacedByPlaceholders_Test()
    {
        await _rig.SeedAsync();
        await _rig.HostWritesAsync("Benvenuta Giulia! Sono Marco, per qualsiasi cosa scrivimi a marco.bianchi@example.com", 60);
        var message = await _rig.GuestWritesAsync(GuestQuestion);

        var outcome = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        // Both calls were made (the classification and the draft), and what they carried holds none of the guest's or the host's data.
        Assert.Equal(AssistantDecision.Drafted, outcome.Decision);
        Assert.Equal([AiModelTier.Economy, AiModelTier.Standard], _rig.Ai.Calls.Select(call => call.Tier));
        var sent = _rig.EverythingSentToTheProvider();
        foreach (var personal in new[]
                 {
                     "Giulia", "Verdi", "Marco", "Bianchi", AssistantDraftRig.GuestEmail, "marco.bianchi@example.com", "333 1234567", "347 9876543",
                     "RSSMRA80A01H501U", "K7M2Q-9X4BN", AssistantDraftRig.BookingCode, AssistantDraftRig.ChannelReference, "12345678", AssistantDraftRig.Signature,
                 })
        {
            Assert.DoesNotContain(personal, sent, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var placeholder in new[] { "[guest]", "[host]", "[booking]", "[code]", "[email]", "[tel]", "[cf]" })
            Assert.Contains(placeholder, sent, StringComparison.Ordinal);

        // The draft greets the guest by name: the server filled the variable in, the model never saw the name.
        var item = await _rig.Db.ApprovalQueueItems.SingleAsync();
        Assert.StartsWith("Ciao Giulia,", item.ContentText, StringComparison.Ordinal);
        Assert.EndsWith(AssistantDraftRig.Signature, item.ContentText, StringComparison.Ordinal);

        // The log keeps ids, codes and scores: no word of the guest, of a fact or of the draft.
        var row = await _rig.Db.AssistantDecisionLogs.SingleAsync();
        var logged = string.Join("|", row.GateFailures) + row.SourceRefs + row.Model + row.ModelVersion + row.ThreadKey;
        Assert.DoesNotContain("Giulia", logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parcheggio", logged, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssistantPrompt_NamesWrittenWithoutTheAccentOrWithATypographicApostrophe_AreTakenOutToo_Test()
    {
        // The booking knows the guest as «José D'Angelo»; the guest writes «Jose» and the apostrophe of a phone.
        await _rig.SeedAsync();
        var guest = await _rig.Db.Guests.SingleAsync(g => g.Id == _rig.Guest.Id);
        guest.FirstName = "José";
        guest.LastName = "D'Angelo";
        await _rig.Db.SaveChangesAsync();
        _rig.Db.ChangeTracker.Clear();
        var message = await _rig.GuestWritesAsync("Buongiorno, sono Jose D’Angelo (la prenotazione e di JOSÉ D'ANGELO). Dove posso parcheggiare?");

        var outcome = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        Assert.Equal(AssistantDecision.Drafted, outcome.Decision);
        var sent = _rig.EverythingSentToTheProvider();
        foreach (var personal in new[] { "Jose", "José", "Angelo", "D’Angelo", "D'Angelo" })
            Assert.DoesNotContain(personal, sent, StringComparison.OrdinalIgnoreCase);

        // Whatever the form of the accent (a letter with it, or a letter and a mark), the name is not in the text without the accents either.
        var withoutAccents = new string(sent.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        Assert.DoesNotContain("Jose", withoutAccents, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[guest]", sent, StringComparison.Ordinal);

        // The server fills the name in: the draft greets the guest by the name the booking has.
        Assert.StartsWith("Ciao José,", (await _rig.Db.ApprovalQueueItems.SingleAsync()).ContentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssistantPipeline_DefaultGate_NeverSendsToGuest_Test()
    {
        await _rig.SeedAsync();
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?");

        var outcome = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        // The draft is in the approval queue, waiting, with everything the queue and the apps need; nothing went to the guest.
        Assert.Equal(AssistantDecision.Drafted, outcome.Decision);
        var item = await _rig.Db.ApprovalQueueItems.SingleAsync();
        Assert.Equal(outcome.ApprovalItemId, item.Id);
        Assert.Equal(ApprovalStatus.Pending, item.Status);
        Assert.Equal(ApprovalItemTypes.MessageDraft, item.ItemType);
        Assert.Equal(message.Id, item.ItemId);
        Assert.Equal(_rig.Key.ToString(), item.ThreadKey);
        Assert.Equal(_rig.World.Granted.Id, item.PropertyId);
        Assert.Equal(ApprovalOriginActions.SendGuestMessage, item.OriginAction);
        Assert.Equal("parking", item.SituationCode);
        Assert.Equal(0.92m, item.ConfidenceScore);
        Assert.True(item.IsAiGenerated);
        _rig.Replies.Verify(
            r => r.ReplyAsync(
                It.IsAny<HostScope>(), It.IsAny<MessageThreadKey>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<MessageOrigin>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(0, await _rig.Db.OtaMessages.CountAsync(m => m.Direction == OtaMessageDirection.Outbound));

        // The gate said why: no real gate is registered.
        var row = await _rig.Db.AssistantDecisionLogs.SingleAsync();
        Assert.Equal(AssistantDecision.Drafted, row.Decision);
        Assert.Equal([AutoSendVerdict.NotEnabledCode], row.GateFailures);
        Assert.Equal(item.Id, row.ApprovalItemId);
        Assert.Null(row.OutboundMessageId);

        // The «Draft ready» alert: the people who may decide were looked for, and the «To do» of the item is raised.
        Assert.Contains(_rig.Asked, context => context.EventType == NotificationEventType.AiDraftToApprove && context.PropertyId == _rig.World.Granted.Id);
        Assert.True(await _rig.Db.InternalActivities.AnyAsync(a =>
            a.ActivityType == nameof(ActivityType.MessageToApprove) && a.EntityId == item.Id && a.Action == ActivityAction.ApproveAiDraft));
    }

    [Fact]
    public async Task AssistantSimulator_WritesAndSendsNothing_Test()
    {
        // The switch of the organization is still off: the host tries the assistant before turning it on.
        await _rig.SeedAsync(enabled: false);
        var before = await CountsAsync();

        var simulation = await _rig.Service().SimulateAsync(_rig.OrgId, _rig.World.Granted.Id, GuestQuestion, null);

        // The answer: what the pipeline would decide.
        Assert.Equal(AssistantDecision.Drafted, simulation.Decision);
        Assert.Equal(AssistantSituation.Parking, simulation.Situation);
        Assert.Equal(AssistantUrgency.Normal, simulation.Urgency);
        Assert.NotNull(simulation.Confidence);
        Assert.Equal(0.92m, simulation.Confidence.Score);
        Assert.StartsWith("Ciao Maria,", simulation.Draft, StringComparison.Ordinal);
        Assert.Contains(simulation.FactsUsed, fact => fact.Topic == KnowledgeTopic.Parking);
        Assert.False(simulation.WouldAutoSend);
        Assert.Equal([AutoSendVerdict.NotEnabledCode], simulation.GateFailures);

        // Nothing was written, nothing was sent, nobody was told; the example went to the provider minimized like a real message.
        Assert.Equal(before, await CountsAsync());
        _rig.Replies.VerifyNoOtherCalls();
        Assert.Empty(_rig.Asked);
        Assert.Empty(_rig.Push.Queued);
        Assert.DoesNotContain("giulia.verdi@example.com", _rig.EverythingSentToTheProvider(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12345678", _rig.EverythingSentToTheProvider(), StringComparison.Ordinal);
    }

    // ─── The decision log (AC-NF67-1) ───

    [Fact]
    public async Task EveryMessage_IsClassifiedGivenFactsADraftAndAScore_AndTheLogSaysWithWhat()
    {
        await _rig.SeedAsync();
        var entryId = await _rig.Db.PropertyKnowledgeEntries.Select(e => e.Id).SingleAsync();
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?");

        await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        var row = await _rig.Db.AssistantDecisionLogs.SingleAsync();
        var rules = await AssistantTestKit.Rules(_rig.Db).GetAsync(_rig.OrgId);
        Assert.Equal(message.Id, row.InboundMessageId);
        Assert.Equal(_rig.World.Granted.Id, row.PropertyId);
        Assert.Equal(AssistantSituation.Parking, row.Situation);
        Assert.Equal(AssistantUrgency.Normal, row.Urgency);
        Assert.Equal(0.92m, row.ConfidenceScore);
        Assert.Equal(0.92m, row.ScoreSituation);
        Assert.Equal(1m, row.ScoreFacts);
        Assert.Equal(1m, row.ScoreRules);
        Assert.Equal("deepseek-v4-flash", row.Model);
        Assert.Equal("DeepSeek/Standard", row.ModelVersion);
        Assert.Equal(rules.InstructionVersion, row.InstructionVersion);
        Assert.Contains($"entry:{entryId}", row.SourceRefs, StringComparison.Ordinal);
    }

    // ─── Escalation and urgency (AC-NF67-1, the exceptions of NF-67) ───

    [Fact]
    public async Task ASituationInEscalate_RaisesTheTodoOfTheConversation_WithItsDeadline_AndNoDraft()
    {
        await _rig.SeedAsync();
        await AssistantTestKit.Situations(_rig.Db).SaveAsync(_rig.OrgId, null, AssistantSituation.Parking, AssistantMode.Escalate, null, null, _rig.World.OwnerUserId);
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?");

        var outcome = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        Assert.Equal((AssistantDecision.Escalated, AssistantDecisionCodes.ModeEscalate), (outcome.Decision, outcome.Reason));
        var todo = await _rig.Db.InternalActivities.SingleAsync(a => a.Id == outcome.ActivityId);
        Assert.Equal(nameof(ActivityType.MessageToReply), todo.ActivityType);
        Assert.Equal(ActivityAction.ReplyToMessage, todo.Action);
        Assert.Equal(AssistantDraftService.ThreadEntityType, todo.EntityType);
        Assert.Equal(_rig.Thread.Id, todo.EntityId);
        Assert.Equal(ActivityUrgency.Medium, todo.Urgency);
        Assert.Equal(AssistantDraftRig.Now.UtcDateTime.AddHours(24), todo.Deadline);
        Assert.Equal(_rig.World.Granted.Id, todo.PropertyId);

        // No draft: one call (the classification), no item in the queue.
        Assert.Equal([AiModelTier.Economy], _rig.Ai.Calls.Select(call => call.Tier));
        Assert.False(await _rig.Db.ApprovalQueueItems.AnyAsync());
        Assert.Equal(todo.Id, (await _rig.Db.AssistantDecisionLogs.SingleAsync()).ActivityId);
    }

    [Fact]
    public async Task AForbiddenTopic_IsEscalatedToo()
    {
        await _rig.SeedAsync(forbidden: ["Parking"]);
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?");

        var outcome = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        Assert.Equal((AssistantDecision.Escalated, AssistantDecisionCodes.ForbiddenTopic), (outcome.Decision, outcome.Reason));
        Assert.NotNull(outcome.ActivityId);
        Assert.False(await _rig.Db.ApprovalQueueItems.AnyAsync());
    }

    [Fact]
    public async Task AnUrgentMessage_RaisesTheTodoWithItsDeadline_AndAlertsThePeopleOfTheProperty_AndStillGetsADraft()
    {
        await _rig.SeedAsync();
        _rig.Ai.Classification = """{"situation":"malfunction","situationMatch":0.8,"urgency":"urgent","sensitiveCategories":["damage"],"language":"it"}""";
        var message = await _rig.GuestWritesAsync("Esce acqua dal soffitto del bagno!");

        var outcome = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        Assert.Equal(AssistantDecision.Drafted, outcome.Decision);
        var todo = await _rig.Db.InternalActivities.SingleAsync(a => a.Id == outcome.ActivityId);
        Assert.Equal(ActivityUrgency.High, todo.Urgency);
        Assert.Equal(AssistantDraftRig.Now.UtcDateTime.AddHours(1), todo.Deadline);
        Assert.Contains(_rig.Asked, context => context.EventType == NotificationEventType.UrgentGuestMessage && context.PropertyId == _rig.World.Granted.Id);
        Assert.Equal(SensitiveCategory.Damage, (await _rig.Db.AssistantDecisionLogs.SingleAsync()).SensitiveCategories);
    }

    // ─── Never the model for history, once per message (AC 6) ───

    [Fact]
    public async Task AnOldMessage_ALaterOne_OrOneTheHostAnswered_NeverReachesTheModel()
    {
        await _rig.SeedAsync();
        var old = await _rig.GuestWritesAsync("Messaggio di ieri", minutesAgo: 7 * 60);
        var first = await _rig.GuestWritesAsync("Prima domanda", minutesAgo: 30);
        var answered = await _rig.GuestWritesAsync("Seconda domanda", minutesAgo: 20);
        await _rig.HostWritesAsync("Risposta del padrone di casa", minutesAgo: 10);
        var service = _rig.Service();

        Assert.Equal(AssistantDecisionCodes.MessageTooOld, (await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, old.Id, AssistantProcessOptions.Automatic)).Reason);
        Assert.Equal(AssistantDecisionCodes.Superseded, (await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, first.Id, AssistantProcessOptions.Automatic)).Reason);
        Assert.Equal(AssistantDecisionCodes.HostAlreadyReplied, (await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, answered.Id, AssistantProcessOptions.Automatic)).Reason);

        Assert.Empty(_rig.Ai.Calls);
        Assert.All(await _rig.Db.AssistantDecisionLogs.ToListAsync(), row => Assert.Equal(AssistantDecision.NotHandled, row.Decision));
    }

    [Fact]
    public async Task ProcessingAMessageTwice_DoesTheWorkOnce()
    {
        await _rig.SeedAsync();
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?");
        var service = _rig.Service();

        var first = await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);
        var second = await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        Assert.False(first.AlreadyProcessed);
        Assert.True(second.AlreadyProcessed);
        Assert.Equal(first.ApprovalItemId, second.ApprovalItemId);
        Assert.Equal(2, _rig.Ai.Calls.Count);
        Assert.Equal(1, await _rig.Db.AssistantDecisionLogs.CountAsync());
        Assert.Equal(1, await _rig.Db.ApprovalQueueItems.CountAsync());
    }

    [Fact]
    public async Task ARunThatStoppedAfterQueueingTheDraft_FindsItsItemAndQueuesNoSecondOne()
    {
        await _rig.SeedAsync();
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?");
        await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        // The row of the log is lost (the run stopped before it was written); Hangfire runs the job again.
        _rig.Db.AssistantDecisionLogs.RemoveRange(_rig.Db.AssistantDecisionLogs);
        await _rig.Db.SaveChangesAsync();
        _rig.Db.ChangeTracker.Clear();
        var again = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        var item = await _rig.Db.ApprovalQueueItems.SingleAsync();
        Assert.Equal(item.Id, again.ApprovalItemId);
        Assert.Equal(item.Id, (await _rig.Db.AssistantDecisionLogs.SingleAsync()).ApprovalItemId);
    }

    // ─── What is not handled: a code, and no call ───

    [Fact]
    public async Task AConversationWithNoPropertyOrAListingNobodyDecided_IsNotHandled_WithNoCall()
    {
        await _rig.SeedAsync();
        var unassigned = _rig.NewThread(null, null, "Luca V.");
        _rig.Db.OtaMessageThreads.Add(unassigned);
        await _rig.Db.SaveChangesAsync();
        var onUnassigned = await _rig.GuestWritesAsync("Domanda", thread: unassigned);
        var onMapped = await _rig.GuestWritesAsync("Domanda");
        var service = _rig.Service();

        var noProperty = await service.ProcessInboundAsync(_rig.OrgId, MessageThreadKey.ForOta(unassigned.Id), onUnassigned.Id, AssistantProcessOptions.Automatic);
        _rig.Mapping.Setup(m => m.IsWriteBlockedAsync(_rig.World.Granted.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var undecided = await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, onMapped.Id, AssistantProcessOptions.Automatic);

        Assert.Equal(AssistantDecisionCodes.PropertyUnassigned, noProperty.Reason);
        Assert.Equal(AssistantDecisionCodes.MappingNotConfirmed, undecided.Reason);
        Assert.Empty(_rig.Ai.Calls);
    }

    [Fact]
    public async Task TheAssistantOffForTheOrg_OrNoProviderInEffect_IsNotHandled_WithNoCall()
    {
        await _rig.SeedAsync(enabled: false);
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?");

        var off = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);
        Assert.Equal((AssistantDecision.NotHandled, AssistantDecisionCodes.AssistantDisabled), (off.Decision, off.Reason));

        await AssistantTestKit.Rules(_rig.Db).SaveAsync(_rig.OrgId, AssistantTestKit.RulesInput(enabled: true, languages: ["it"]), _rig.World.OwnerUserId);
        var second = await _rig.GuestWritesAsync("E il wifi?", minutesAgo: 1);
        _rig.AiSettings.Setup(s => s.GetEffectiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiOptions());
        var stub = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, second.Id, AssistantProcessOptions.Automatic);
        Assert.Equal(AssistantDecisionCodes.AiProviderNotConfigured, stub.Reason);

        Assert.Empty(_rig.Ai.Calls);
    }

    [Fact]
    public async Task ABudgetUsedUpOrAProviderThatFails_IsADecisionNotHandled_NeverAFailedEvent()
    {
        await _rig.SeedAsync();
        var first = await _rig.GuestWritesAsync("Dove posso parcheggiare?", minutesAgo: 2);
        _rig.Ai.Throw = new AiBudgetExceededException();

        var budget = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, first.Id, AssistantProcessOptions.Automatic);

        var second = await _rig.GuestWritesAsync("Ancora?", minutesAgo: 1);
        _rig.Ai.Throw = new HttpRequestException("the provider did not answer");
        var failure = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, second.Id, AssistantProcessOptions.Automatic);

        Assert.Equal((AssistantDecision.NotHandled, AssistantDecisionCodes.AiBudgetExceeded), (budget.Decision, budget.Reason));
        Assert.Equal((AssistantDecision.NotHandled, AssistantDecisionCodes.AiProviderError), (failure.Decision, failure.Reason));
        Assert.False(await _rig.Db.ApprovalQueueItems.AnyAsync());
    }

    // ─── A draft asked for by a person ───

    [Fact]
    public async Task DraftNow_ACollaboratorSoloAlcuni_ReachesOnlyTheConversationsOfItsProperties()
    {
        await _rig.SeedAsync();
        await _rig.GuestWritesAsync("Domanda sulla casa nascosta", thread: _rig.HiddenThread);
        await _rig.GuestWritesAsync("Dove posso parcheggiare?");
        var service = _rig.Service();

        var hidden = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.DraftNowAsync(_rig.World.Restricted, MessageThreadKey.ForOta(_rig.HiddenThread.Id), _rig.World.CollaboratorId));
        Assert.Equal(MessageHubService.ThreadNotFoundCode, hidden.Code);
        Assert.Empty(_rig.Ai.Calls);
        Assert.False(await _rig.Db.AssistantDecisionLogs.AnyAsync());

        var granted = await service.DraftNowAsync(_rig.World.Restricted, _rig.Key, _rig.World.CollaboratorId);
        Assert.Equal(AssistantDecision.Drafted, granted.Decision);
    }

    [Fact]
    public async Task DraftNow_IgnoresTheAgeAndTheAnswer_SupersedesTheWaitingDraft_AndNeverSends()
    {
        await _rig.SeedAsync();
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?", minutesAgo: 10);
        var first = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);
        await _rig.HostWritesAsync("Ti rispondo subito", minutesAgo: 5);

        _rig.Ai.Draft = """{"draft":"Ciao {{guest_name}}, puoi parcheggiare nel cortile, è gratuito.","language":"it","missingFacts":false,"topics":["Parking"]}""";
        var regenerated = await _rig.Service().DraftNowAsync(_rig.World.OrgWide, _rig.Key, _rig.World.OwnerUserId);

        Assert.Equal(AssistantDecision.Drafted, regenerated.Decision);
        Assert.NotEqual(first.ApprovalItemId, regenerated.ApprovalItemId);
        var old = await _rig.Db.ApprovalQueueItems.SingleAsync(i => i.Id == first.ApprovalItemId);
        Assert.Equal((ApprovalStatus.Rejected, AssistantDecisionCodes.RegeneratedReason), (old.Status, old.RejectionReason));
        Assert.Equal(ApprovalStatus.Pending, (await _rig.Db.ApprovalQueueItems.SingleAsync(i => i.Id == regenerated.ApprovalItemId)).Status);

        // The row of the message is the new decision, and nothing went to the guest.
        var row = await _rig.Db.AssistantDecisionLogs.SingleAsync();
        Assert.Equal(regenerated.ApprovalItemId, row.ApprovalItemId);
        _rig.Replies.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DraftNow_InAConversationWhereTheGuestWroteNothing_IsAConflict()
    {
        await _rig.SeedAsync();
        await _rig.HostWritesAsync("Benvenuta!", minutesAgo: 5);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => _rig.Service().DraftNowAsync(_rig.World.OrgWide, _rig.Key, _rig.World.OwnerUserId));

        Assert.Equal(AssistantDecisionCodes.NoGuestMessageCode, conflict.Code);
    }

    // ─── A gate that allows (the contract of E05/F05/US03) ───

    [Fact]
    public async Task AGateThatAllows_SendsAsTheAssistant_WithTheKeyOfTheMessage_AndQueuesNothing()
    {
        await _rig.SeedAsync();
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?");
        var outbound = Guid.NewGuid();
        _rig.Replies
            .Setup(r => r.ReplyAsync(
                It.IsAny<HostScope>(), It.IsAny<MessageThreadKey>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<MessageOrigin>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessageReplyResult(
                MessageContactChannel.Ota,
                _rig.Key,
                new MessageHubMessage(outbound, OtaMessageDirection.Outbound, MessageOrigin.Assistant, "parking", null, "text", AssistantDraftRig.Now.UtcDateTime, null, OtaMessageDeliveryState.Sent, null),
                OtaMessageDeliveryState.Sent,
                false,
                null));

        var outcome = await _rig.Service(new AllowingGate()).ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        Assert.Equal(AssistantDecision.AutoSent, outcome.Decision);
        _rig.Replies.Verify(
            r => r.ReplyAsync(
                It.Is<HostScope>(scope => scope.OrgId == _rig.OrgId),
                _rig.Key,
                It.Is<string?>(text => text!.StartsWith("Ciao Giulia,", StringComparison.Ordinal)),
                $"assistant-{message.Id:N}",
                null,
                MessageOrigin.Assistant,
                "parking",
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.False(await _rig.Db.ApprovalQueueItems.AnyAsync());
        Assert.Equal(outbound, (await _rig.Db.AssistantDecisionLogs.SingleAsync()).OutboundMessageId);
    }

    // ─── The «To do» never outlives the answer ───

    [Fact]
    public async Task AnAnswerOfAPerson_ClosesTheTodoOfTheEscalatedMessagesOfThatConversation_Only()
    {
        await _rig.SeedAsync();
        await AssistantTestKit.Situations(_rig.Db).SaveAsync(_rig.OrgId, null, AssistantSituation.Parking, AssistantMode.Escalate, null, null, _rig.World.OwnerUserId);
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?");
        var escalated = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);
        var other = await _rig.Activities().CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.MessageToReply,
                Action = ActivityAction.ReplyToMessage,
                EntityType = AssistantDraftService.ThreadEntityType,
                EntityId = _rig.HiddenThread.Id,
                Urgency = ActivityUrgency.Medium,
                PropertyId = _rig.World.Hidden.Id,
            },
            CancellationToken.None);

        await new AssistantEscalationCloser(_rig.Db, _rig.Activities(), NullLogger<AssistantEscalationCloser>.Instance, _rig.Clock)
            .OnHostReplyAsync(_rig.OrgId, _rig.Key, CancellationToken.None);

        _rig.Db.ChangeTracker.Clear();
        Assert.Equal(InternalActivity.StatusCompleted, (await _rig.Db.InternalActivities.SingleAsync(a => a.Id == escalated.ActivityId)).Status);
        Assert.Equal(AssistantDraftRig.Now.UtcDateTime, (await _rig.Db.AssistantDecisionLogs.SingleAsync()).ActivityClosedAt);
        Assert.Equal(InternalActivity.StatusPending, (await _rig.Db.InternalActivities.SingleAsync(a => a.Id == other)).Status);
    }

    // ─── The conversation of a direct booking ───

    [Fact]
    public async Task AMessageOfTheGuestOfADirectBooking_GetsItsDraftToo()
    {
        await _rig.SeedAsync();
        var booking = new Booking
        {
            OrgId = _rig.OrgId,
            PropertyId = _rig.World.Granted.Id,
            GuestId = _rig.Guest.Id,
            CheckInDate = HostScopeScenario.Day(2026, 10, 11),
            CheckOutDate = HostScopeScenario.Day(2026, 10, 13),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 200m,
            TotalPrice = 200m,
        };
        var thread = new GuestThread { OrgId = _rig.OrgId, PropertyId = _rig.World.Granted.Id, BookingId = booking.Id, CreatedAt = AssistantDraftRig.Now.UtcDateTime, LastMessageAt = AssistantDraftRig.Now.UtcDateTime };
        var message = new GuestMessage
        {
            OrgId = _rig.OrgId,
            ThreadId = thread.Id,
            Sender = GuestMessageSender.Guest,
            Origin = MessageOrigin.Guest,
            Text = "Dove posso parcheggiare?",
            CreatedAt = AssistantDraftRig.Now.UtcDateTime.AddMinutes(-3),
        };
        _rig.Db.Bookings.Add(booking);
        _rig.Db.GuestThreads.Add(thread);
        _rig.Db.GuestMessages.Add(message);
        await _rig.Db.SaveChangesAsync();
        _rig.Db.ChangeTracker.Clear();

        var outcome = await _rig.Service().ProcessInboundAsync(_rig.OrgId, MessageThreadKey.ForDirect(booking.Id), message.Id, AssistantProcessOptions.Automatic);

        Assert.Equal(AssistantDecision.Drafted, outcome.Decision);
        Assert.Equal(MessageThreadKey.ForDirect(booking.Id).ToString(), (await _rig.Db.ApprovalQueueItems.SingleAsync()).ThreadKey);
    }

    // ─── The «To do» of a conversation is as urgent as its most urgent message (AC 3) ───

    [Fact]
    public async Task AnUrgentMessageAfterAnEscalatedOne_RaisesTheOpenTodoOfTheConversation_AndALessUrgentOneNeverLowersIt()
    {
        await _rig.SeedAsync();
        await AssistantTestKit.Situations(_rig.Db).SaveAsync(_rig.OrgId, null, AssistantSituation.Parking, AssistantMode.Escalate, null, null, _rig.World.OwnerUserId);
        var service = _rig.Service();
        var now = AssistantDraftRig.Now.UtcDateTime;

        // A question the host answers himself: a «To do» of normal urgency, due the day after.
        var first = await _rig.GuestWritesAsync("Dove posso parcheggiare?", minutesAgo: 30);
        var escalated = await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, first.Id, AssistantProcessOptions.Automatic);
        var opened = await TodoAsync(escalated.ActivityId);
        Assert.Equal((ActivityUrgency.Medium, now.AddHours(24), "parking"), (opened.Urgency, opened.Deadline, opened.Reason));

        // An urgent message follows in the same conversation while that «To do» is open: the same item, raised to High and due in an hour, among the urgent ones of the list.
        _rig.Ai.Classification = UrgentClassification;
        var second = await _rig.GuestWritesAsync("Esce acqua dal soffitto del bagno!", minutesAgo: 5);
        var urgent = await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, second.Id, AssistantProcessOptions.Automatic);
        Assert.Equal(escalated.ActivityId, urgent.ActivityId);
        var raised = await TodoAsync(urgent.ActivityId);
        Assert.Equal((ActivityUrgency.High, now.AddHours(1), "malfunction"), (raised.Urgency, raised.Deadline, raised.Reason));
        Assert.Equal(1, await _rig.Db.InternalActivities.CountAsync(a => a.ActivityType == nameof(ActivityType.MessageToReply)));
        var pending = await _rig.Activities().GetPendingAsync(_rig.World.OrgWide, CancellationToken.None);
        Assert.Equal(
            [escalated.ActivityId!.Value],
            pending.Where(a => a.ActivityType == nameof(ActivityType.MessageToReply) && a.Urgency == ActivityUrgency.High).Select(a => a.Id));

        // A less urgent one afterwards leaves it as it is: the urgency does not go down and the deadline does not move later.
        _rig.Ai.Classification = ScriptedAiProvider.ParkingClassification;
        var third = await _rig.GuestWritesAsync("E per i bagagli?", minutesAgo: 1);
        var lessUrgent = await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, third.Id, AssistantProcessOptions.Automatic);
        Assert.Equal(escalated.ActivityId, lessUrgent.ActivityId);
        var kept = await TodoAsync(lessUrgent.ActivityId);
        Assert.Equal((ActivityUrgency.High, now.AddHours(1), "malfunction"), (kept.Urgency, kept.Deadline, kept.Reason));
    }

    [Fact]
    public async Task AnEscalatedMessageAfterAnUrgentOne_LeavesTheTodoHighAndDueInAnHour()
    {
        await _rig.SeedAsync();
        await AssistantTestKit.Situations(_rig.Db).SaveAsync(_rig.OrgId, null, AssistantSituation.Parking, AssistantMode.Escalate, null, null, _rig.World.OwnerUserId);
        var service = _rig.Service();
        var now = AssistantDraftRig.Now.UtcDateTime;

        _rig.Ai.Classification = UrgentClassification;
        var first = await _rig.GuestWritesAsync("Esce acqua dal soffitto del bagno!", minutesAgo: 30);
        var urgent = await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, first.Id, AssistantProcessOptions.Automatic);
        _rig.Ai.Classification = ScriptedAiProvider.ParkingClassification;
        var second = await _rig.GuestWritesAsync("Dove posso parcheggiare?", minutesAgo: 5);
        var escalated = await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, second.Id, AssistantProcessOptions.Automatic);

        Assert.Equal(AssistantDecision.Escalated, escalated.Decision);
        Assert.Equal(urgent.ActivityId, escalated.ActivityId);
        var todo = await TodoAsync(escalated.ActivityId);
        Assert.Equal((ActivityUrgency.High, now.AddHours(1), "malfunction"), (todo.Urgency, todo.Deadline, todo.Reason));
        Assert.Equal(1, await _rig.Db.InternalActivities.CountAsync(a => a.ActivityType == nameof(ActivityType.MessageToReply)));
    }

    // ─── The alert of an urgent message is never lost by a job that stops (no event consumed with no effect) ───

    [Fact]
    public async Task TheAlertOfAnUrgentMessage_IsDoneBeforeTheRowOfTheLogIsWritten_SoAJobThatStopsAfterwardsLosesNothing()
    {
        await _rig.SeedAsync();
        _rig.Ai.Classification = UrgentClassification;
        var message = await _rig.GuestWritesAsync("Esce acqua dal soffitto del bagno!");

        // The run stops after the alert and before its row is written (the gate, asked for the draft, fails): the people were alerted, and there is no row to make Hangfire return at once.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _rig.Service(new FailingGate()).ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic));
        Assert.Single(_rig.Asked, context => context.EventType == NotificationEventType.UrgentGuestMessage);
        Assert.False(await _rig.Db.AssistantDecisionLogs.AnyAsync());

        // Hangfire repeats the job: it does the work, and the alert again (the keys of the alert make that tell nobody twice).
        var repeated = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);
        Assert.Equal(AssistantDecision.Drafted, repeated.Decision);
        Assert.Equal(2, _rig.Asked.Count(context => context.EventType == NotificationEventType.UrgentGuestMessage));

        // The row exists now: a third run returns at once and alerts nobody.
        var third = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);
        Assert.True(third.AlreadyProcessed);
        Assert.Equal(2, _rig.Asked.Count(context => context.EventType == NotificationEventType.UrgentGuestMessage));
    }

    [Fact]
    public async Task AnUrgentAlertThatFails_IsHandedToTheJobOfTheAlerts_AndOneThatCannotEvenBeHandedOverFailsTheRun_SoItIsRepeated()
    {
        await _rig.SeedAsync();
        _rig.Ai.Classification = UrgentClassification;
        var first = await _rig.GuestWritesAsync("Esce acqua dal soffitto del bagno!", minutesAgo: 2);
        _rig.Preferences
            .Setup(p => p.ResolveAsync(It.Is<NotificationEventContext>(context => context.EventType == NotificationEventType.UrgentGuestMessage), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The preferences cannot be read."));

        // The alert fails and the job of the alerts takes it over: the run goes on and writes its row.
        var handedOver = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, first.Id, AssistantProcessOptions.Automatic);
        Assert.Equal(AssistantDecision.Drafted, handedOver.Decision);
        _rig.Jobs.Verify(j => j.Create(It.Is<Job>(job => job.Type == typeof(HostMessageAlertDueJob)), It.IsAny<IState>()), Times.Once);

        // The job of the alerts cannot be queued either: the failure is not swallowed, the run fails before it writes its row, and Hangfire repeats it.
        var second = await _rig.GuestWritesAsync("Ancora acqua, sempre piu forte!", minutesAgo: 1);
        _rig.Jobs
            .Setup(j => j.Create(It.Is<Job>(job => job.Type == typeof(HostMessageAlertDueJob)), It.IsAny<IState>()))
            .Throws(new InvalidOperationException("The storage of the jobs is down."));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, second.Id, AssistantProcessOptions.Automatic));
        Assert.False(await _rig.Db.AssistantDecisionLogs.AnyAsync(row => row.InboundMessageId == second.Id));

        _rig.Jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-2");
        var repeated = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, second.Id, AssistantProcessOptions.Automatic);
        Assert.Equal(AssistantDecision.Drafted, repeated.Decision);
    }

    // ─── Another organization's conversation or message is never read (tenant) ───

    [Fact]
    public async Task AConversationOrAMessageOfAnotherOrg_IsNeverRead_WithNoCallToTheModel_AndNothingOfThatOrgChanges()
    {
        await _rig.SeedAsync();
        var otherOrg = _rig.World.OtherOrgId;

        // The conversation of another org (on a house of its own) with two messages of its guest, and its guest area: a direct booking with a message.
        var foreignThread = _rig.NewThread(_rig.World.OtherOrgProperty.Id, null, "Anna Altrove");
        foreignThread.OrgId = otherOrg;
        _rig.Db.OtaMessageThreads.Add(foreignThread);
        var foreignBooking = new Booking
        {
            OrgId = otherOrg,
            PropertyId = _rig.World.OtherOrgProperty.Id,
            GuestId = _rig.Guest.Id,
            CheckInDate = HostScopeScenario.Day(2026, 10, 11),
            CheckOutDate = HostScopeScenario.Day(2026, 10, 13),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 200m,
            TotalPrice = 200m,
        };
        var foreignGuestThread = new GuestThread
        {
            OrgId = otherOrg,
            PropertyId = _rig.World.OtherOrgProperty.Id,
            BookingId = foreignBooking.Id,
            CreatedAt = AssistantDraftRig.Now.UtcDateTime,
            LastMessageAt = AssistantDraftRig.Now.UtcDateTime,
        };
        var foreignGuestMessage = new GuestMessage
        {
            OrgId = otherOrg,
            ThreadId = foreignGuestThread.Id,
            Sender = GuestMessageSender.Guest,
            Origin = MessageOrigin.Guest,
            Text = "Dove posso parcheggiare?",
            CreatedAt = AssistantDraftRig.Now.UtcDateTime.AddMinutes(-3),
        };
        _rig.Db.Bookings.Add(foreignBooking);
        _rig.Db.GuestThreads.Add(foreignGuestThread);
        _rig.Db.GuestMessages.Add(foreignGuestMessage);
        await _rig.Db.SaveChangesAsync();
        var firstOfOther = await _rig.GuestWritesAsync("Dove posso parcheggiare?", thread: foreignThread);
        var secondOfOther = await _rig.GuestWritesAsync("E il wifi?", minutesAgo: 4, thread: foreignThread);
        await _rig.GuestWritesAsync("Domanda di questa org");
        var service = _rig.Service();
        var before = await CountsAsync();

        // The job of this org is told a conversation of the other one (of a channel, of a direct booking): it is not there, for this org.
        var ota = await service.ProcessInboundAsync(_rig.OrgId, MessageThreadKey.ForOta(foreignThread.Id), firstOfOther.Id, AssistantProcessOptions.Automatic);
        var direct = await service.ProcessInboundAsync(_rig.OrgId, MessageThreadKey.ForDirect(foreignBooking.Id), foreignGuestMessage.Id, AssistantProcessOptions.Automatic);

        // ...or a message of the other one in a conversation of its own: that message is not there.
        var message = await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, secondOfOther.Id, AssistantProcessOptions.Automatic);

        // A person of this org cannot ask for a draft of either conversation: the 404 of a conversation that does not exist.
        var deniedOta = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.DraftNowAsync(_rig.World.OrgWide, MessageThreadKey.ForOta(foreignThread.Id), _rig.World.OwnerUserId));
        var deniedDirect = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.DraftNowAsync(_rig.World.OrgWide, MessageThreadKey.ForDirect(foreignBooking.Id), _rig.World.OwnerUserId));

        Assert.Equal(
            [AssistantDecisionCodes.ThreadNotFound, AssistantDecisionCodes.ThreadNotFound, AssistantDecisionCodes.MessageNotFound],
            new[] { ota, direct, message }.Select(outcome => outcome.Reason));
        Assert.All(new[] { ota, direct, message }, outcome => Assert.Equal(AssistantDecision.NotHandled, outcome.Decision));
        Assert.Equal(MessageHubService.ThreadNotFoundCode, deniedOta.Code);
        Assert.Equal(MessageHubService.ThreadNotFoundCode, deniedDirect.Code);

        // No call to the model, nothing queued, raised or sent; and nothing was written for the other org.
        Assert.Empty(_rig.Ai.Calls);
        var after = await CountsAsync();
        Assert.Equal((before.Items, before.Todos, before.OtaMessages, before.GuestMessages, before.Events), (after.Items, after.Todos, after.OtaMessages, after.GuestMessages, after.Events));
        Assert.Empty(await _rig.Db.ApprovalQueueItems.Where(i => i.OrgId == otherOrg).ToListAsync());
        Assert.Empty(await _rig.Db.InternalActivities.Where(a => a.OrgId == otherOrg).ToListAsync());
        Assert.All(await _rig.Db.AssistantDecisionLogs.ToListAsync(), row => Assert.Equal(_rig.OrgId, row.OrgId));
        _rig.Replies.VerifyNoOtherCalls();
    }

    // ─── The codes of a message that is not handled, beyond the brief: no call to the model, nothing queued, the code in the log ───

    [Fact]
    public async Task AnAnonymizedConversation_OrAClosedDirectOne_IsClosed_SoAnErasedGuestNeverReachesTheModel()
    {
        await _rig.SeedAsync();
        var erased = await _rig.Db.OtaMessageThreads.SingleAsync(t => t.Id == _rig.Thread.Id);
        erased.AnonymizedAt = AssistantDraftRig.Now.UtcDateTime.AddDays(-1);
        var booking = new Booking
        {
            OrgId = _rig.OrgId,
            PropertyId = _rig.World.Granted.Id,
            GuestId = _rig.Guest.Id,
            CheckInDate = HostScopeScenario.Day(2026, 9, 17),
            CheckOutDate = HostScopeScenario.Day(2026, 9, 20),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Direct,
            BasePrice = 200m,
            TotalPrice = 200m,
        };
        var guestThread = new GuestThread { OrgId = _rig.OrgId, PropertyId = _rig.World.Granted.Id, BookingId = booking.Id, CreatedAt = AssistantDraftRig.Now.UtcDateTime, LastMessageAt = AssistantDraftRig.Now.UtcDateTime };
        var guestMessage = new GuestMessage
        {
            OrgId = _rig.OrgId,
            ThreadId = guestThread.Id,
            Sender = GuestMessageSender.Guest,
            Origin = MessageOrigin.Guest,
            Text = "Posso lasciare le valigie?",
            CreatedAt = AssistantDraftRig.Now.UtcDateTime.AddMinutes(-3),
        };
        _rig.Db.Bookings.Add(booking);
        _rig.Db.GuestThreads.Add(guestThread);
        _rig.Db.GuestMessages.Add(guestMessage);
        await _rig.Db.SaveChangesAsync();
        _rig.Db.ChangeTracker.Clear();
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?", minutesAgo: 2);

        var ota = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);
        var direct = await _rig.Service().ProcessInboundAsync(_rig.OrgId, MessageThreadKey.ForDirect(booking.Id), guestMessage.Id, AssistantProcessOptions.Automatic);

        await AssertNotHandledAsync(ota, AssistantDecisionCodes.ThreadClosed);
        await AssertNotHandledAsync(direct, AssistantDecisionCodes.ThreadClosed);
    }

    [Fact]
    public async Task ASituationOutsideTheAllowedTopics_IsEscalatedWithItsTodo_AndGetsNoDraft()
    {
        await _rig.SeedAsync();
        await AssistantTestKit.Rules(_rig.Db).SaveAsync(
            _rig.OrgId,
            AssistantTestKit.RulesInput(enabled: true, languages: ["it", "en"], signature: AssistantDraftRig.Signature, threshold: 0.9m, allowed: ["Pets"]),
            _rig.World.OwnerUserId);
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?");

        var outcome = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        Assert.Equal((AssistantDecision.Escalated, AssistantDecisionCodes.TopicNotAllowed), (outcome.Decision, outcome.Reason));
        Assert.NotNull(outcome.ActivityId);
        Assert.Equal([AiModelTier.Economy], _rig.Ai.Calls.Select(call => call.Tier));
        Assert.False(await _rig.Db.ApprovalQueueItems.AnyAsync());
        var row = await _rig.Db.AssistantDecisionLogs.SingleAsync();
        Assert.Equal(AssistantDecision.Escalated, row.Decision);
        Assert.Equal([AssistantDecisionCodes.TopicNotAllowed], row.GateFailures);
    }

    [Fact]
    public async Task AnAnswerOfTheDraftThatCannotBeUsed_IsNotHandled_AndNeverAnEmptyDraftInTheQueue()
    {
        await _rig.SeedAsync();

        // Not an object with a draft, and a draft that is nothing once the unknown variables are taken out: the model was asked twice for each message, and nothing is queued.
        _rig.Ai.Draft = "Non riesco a scrivere la bozza.";
        var first = await _rig.GuestWritesAsync("Dove posso parcheggiare?", minutesAgo: 2);
        var notAnObject = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, first.Id, AssistantProcessOptions.Automatic);
        await AssertNotHandledAsync(notAnObject, AssistantDecisionCodes.AiInvalidAnswer, modelCalls: 2);

        _rig.Ai.Draft = """{"draft":"{{nonsense}}","language":"it","missingFacts":false,"topics":[]}""";
        var second = await _rig.GuestWritesAsync("Ancora il parcheggio?", minutesAgo: 1);
        var nothingLeft = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, second.Id, AssistantProcessOptions.Automatic);
        await AssertNotHandledAsync(nothingLeft, AssistantDecisionCodes.AiInvalidAnswer, modelCalls: 4);
    }

    [Fact]
    public async Task AChannelWhileTheConnectorIsOff_IsNotHandled_WithNoCallToTheModel()
    {
        await _rig.SeedAsync();
        _rig.Flags.Set(FeatureFlags.OctorateConnector, false);
        var message = await _rig.GuestWritesAsync("Dove posso parcheggiare?");

        var outcome = await _rig.Service().ProcessInboundAsync(_rig.OrgId, _rig.Key, message.Id, AssistantProcessOptions.Automatic);

        await AssertNotHandledAsync(outcome, AssistantDecisionCodes.OctorateNotConnected);
    }

    [Fact]
    public async Task AMessageOrAConversationThatIsNotThere_IsNotHandled_WithNoCallToTheModel()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();

        var noMessage = await service.ProcessInboundAsync(_rig.OrgId, _rig.Key, Guid.NewGuid(), AssistantProcessOptions.Automatic);
        var noConversation = await service.ProcessInboundAsync(_rig.OrgId, MessageThreadKey.ForOta(Guid.NewGuid()), Guid.NewGuid(), AssistantProcessOptions.Automatic);

        await AssertNotHandledAsync(noMessage, AssistantDecisionCodes.MessageNotFound);
        await AssertNotHandledAsync(noConversation, AssistantDecisionCodes.ThreadNotFound);
    }

    // ─── Helpers ───

    private async Task<(int Logs, int Items, int Todos, int OtaMessages, int GuestMessages, int Events)> CountsAsync() =>
        (await _rig.Db.AssistantDecisionLogs.CountAsync(),
            await _rig.Db.ApprovalQueueItems.CountAsync(),
            await _rig.Db.InternalActivities.CountAsync(),
            await _rig.Db.OtaMessages.CountAsync(),
            await _rig.Db.GuestMessages.CountAsync(),
            await _rig.Db.OutboxEvents.CountAsync());

    /// <summary>The «To do» as the database has it now (the tracker is cleared, so a change made through another context of the services is read).</summary>
    private async Task<InternalActivity> TodoAsync(Guid? id)
    {
        _rig.Db.ChangeTracker.Clear();
        return await _rig.Db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    /// <summary>A message not handled: the decision and its code, as many calls to the model as expected (none by default), nothing in the queue, and the code in the row of the log.</summary>
    private async Task AssertNotHandledAsync(AssistantDraftOutcome outcome, string code, int modelCalls = 0)
    {
        Assert.Equal((AssistantDecision.NotHandled, code), (outcome.Decision, outcome.Reason));
        Assert.Equal(modelCalls, _rig.Ai.Calls.Count);
        Assert.False(await _rig.Db.ApprovalQueueItems.AnyAsync());
        var row = await _rig.Db.AssistantDecisionLogs.SingleAsync(l => l.InboundMessageId == outcome.InboundMessageId);
        Assert.Equal(AssistantDecision.NotHandled, row.Decision);
        Assert.Equal([code], row.GateFailures);
        Assert.Null(row.ApprovalItemId);
    }

    private sealed class FailingGate : IAutoSendGate
    {
        public Task<AutoSendVerdict> EvaluateAsync(AutoSendContext context, CancellationToken ct) => throw new InvalidOperationException("The run stopped before its row was written.");
    }

    private sealed class AllowingGate : IAutoSendGate
    {
        public Task<AutoSendVerdict> EvaluateAsync(AutoSendContext context, CancellationToken ct) => Task.FromResult(new AutoSendVerdict(true, []));
    }
}
