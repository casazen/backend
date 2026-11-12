using System.Globalization;
using System.Text.Json;
using Casazen.Core.Assistant;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAssistantDraftService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Two calls, both through the platform.</b> The classification on the Economy tier and the draft on the Standard tier of <see cref="IAiProvider"/>, which goes through the monthly
/// budget when an external provider is active; their prompts are <see cref="AssistantPromptBuilder"/>'s, built from texts minimized by <see cref="AssistantPromptBuilder.Minimize"/> with
/// <see cref="PiiScrubber.Scrub"/>. A budget used up (<see cref="AiBudgetExceededException"/>) or a provider that fails is a decision <c>NotHandled</c> with its code, never a failed event.</item>
/// <item><b>The order of the writes.</b> A decision with no draft is written at once. A draft is put in the approval queue first (its item names the message: <c>ItemId</c> is the id of the
/// inbound message), then its row of the log is written with the id of the item, so a row never points to nothing; a run that stopped in between is run again by Hangfire, finds the item of
/// the message in the queue and does not queue a second one.</item>
/// <item><b>The «To do».</b> A message whose situation is set to escalate, that touches a forbidden topic (or one the rules do not allow), or that is urgent raises one «To do» per
/// conversation (<see cref="ActivityType.MessageToReply"/>, one while it is open; a later message that is more urgent raises it, never lowers it) with the urgency and a deadline of
/// <c>Assistant:Escalation</c>; an urgent message also alerts the people who reach the property (<see cref="NotificationEventType.UrgentGuestMessage"/>, through
/// <see cref="HostMessageAlertDelivery"/>), before the row of the log is written. The first two get no draft; an urgent message does.</item>
/// <item><b>Logs</b> carry ids, codes, the decision and the score: never a word of the guest, of a fact or of a draft.</item>
/// </list>
/// </remarks>
public sealed class AssistantDraftService(
    AppDbContext db,
    IAssistantRulesService rules,
    IAssistantKnowledgeService knowledge,
    IAssistantSituationService situations,
    IAiProvider ai,
    IAiRuntimeSettings aiSettings,
    IChannelMappingService channelMapping,
    IFeatureFlags featureFlags,
    IApprovalQueueService approvals,
    IMessageReplyService replies,
    IInternalActivityService activities,
    IAutoSendGate gate,
    HostMessageAlertDelivery hostAlerts,
    IOptions<AssistantOptions> options,
    ILogger<AssistantDraftService> logger,
    TimeProvider? timeProvider = null) : IAssistantDraftService
{
    /// <summary>The type of the entity of the «To do» of a conversation of a channel (the «To do» of a direct booking is about the <c>Booking</c>).</summary>
    public const string ThreadEntityType = nameof(OtaMessageThread);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly AssistantOptions _options = options.Value;

    // ─── The pipeline ───

    public async Task<AssistantDraftOutcome> ProcessInboundAsync(
        Guid orgId,
        MessageThreadKey threadKey,
        Guid inboundMessageId,
        AssistantProcessOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // One row per message: a message processed before is found again, and only a person who asks for a new draft writes the row again.
        var existing = await FindLogAsync(orgId, inboundMessageId, ct);
        if (existing is not null && !options.Requested)
            return OutcomeOf(existing, alreadyProcessed: true);

        var run = new Run(orgId, threadKey, inboundMessageId, options, existing);

        // (a) The conversation, read for the whole org (a job has no caller): its property, its channel, the message.
        var thread = await ThreadOfAsync(orgId, threadKey, ct);
        if (thread is null)
            return await NotHandledAsync(run, null, AssistantDecisionCodes.ThreadNotFound, ct);

        var message = await InboundOfAsync(orgId, thread, inboundMessageId, ct);
        if (message is null)
            return await NotHandledAsync(run, thread.PropertyId, AssistantDecisionCodes.MessageNotFound, ct);

        var conversation = await MessageConversationLookup.FindAsync(db, new HostScope(orgId), threadKey, ct);

        // An unassigned conversation (or one whose property is gone) has no knowledge base to answer from.
        if (thread.PropertyId is not { } propertyId || conversation.PropertyId != propertyId)
            return await NotHandledAsync(run, null, AssistantDecisionCodes.PropertyUnassigned, ct);

        if (thread.Closed)
            return await NotHandledAsync(run, propertyId, AssistantDecisionCodes.ThreadClosed, ct);

        // OCT-04: an answer could never be sent to a channel with the connector off or a listing nobody decided.
        if (thread.Source == HostMessageSource.Ota)
        {
            if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
                return await NotHandledAsync(run, propertyId, AssistantDecisionCodes.OctorateNotConnected, ct);

            if (await channelMapping.IsWriteBlockedAsync(propertyId, ct))
                return await NotHandledAsync(run, propertyId, AssistantDecisionCodes.MappingNotConfirmed, ct);
        }

        var effective = await rules.ResolveEffectiveAsync(orgId, propertyId, ct);
        if (!effective.Enabled)
            return await NotHandledAsync(run, propertyId, AssistantDecisionCodes.AssistantDisabled, effective.InstructionVersion, ct);

        var aiOptions = await aiSettings.GetEffectiveAsync(ct);
        if (!aiOptions.IsExternalProviderActive())
            return await NotHandledAsync(run, propertyId, AssistantDecisionCodes.AiProviderNotConfigured, effective.InstructionVersion, ct);

        // (b) History never wakes the assistant: a message too old, one that is not the last of the guest, one the host already answered.
        if (!options.Requested)
        {
            if (message.At < Now() - _options.MaxMessageAge())
                return await NotHandledAsync(run, propertyId, AssistantDecisionCodes.MessageTooOld, effective.InstructionVersion, ct);

            if (await HasLaterGuestMessageAsync(orgId, thread, message, ct))
                return await NotHandledAsync(run, propertyId, AssistantDecisionCodes.Superseded, effective.InstructionVersion, ct);

            if (await HostAnsweredSinceAsync(orgId, thread, message, ct))
                return await NotHandledAsync(run, propertyId, AssistantDecisionCodes.HostAlreadyReplied, effective.InstructionVersion, ct);
        }

        // (c), (d), (e): the classification, the setting of the situation, the facts, the draft, the confidence.
        var names = await NamesOfAsync(orgId, conversation, ct);
        var transcript = await TranscriptAsync(orgId, thread, names, ct);
        var composed = await ComposeAsync(
            new Composing(orgId, propertyId, transcript, names, effective, ValuesOf(conversation), text => RefusalOf(thread, text), null, inboundMessageId),
            ct);

        var model = Bounded(aiOptions.Model);
        var version = composed.LastTier is { } tier ? Bounded($"{aiOptions.Provider}/{tier}") : null;
        if (composed.Classification is not { } classification)
            return await NotHandledAsync(run, propertyId, composed.Failure!, effective.InstructionVersion, ct);

        // An escalated message, or an urgent one, raises the «To do» of the conversation (one while it is open).
        var activityId = run.Existing is { ActivityId: { } kept, ActivityClosedAt: null } ? kept : (Guid?)null;
        if (composed.Escalation is not null || classification.Urgency == AssistantUrgency.Urgent)
            activityId = await RaiseTodoAsync(orgId, propertyId, thread, classification.Urgency, composed.Info!.Code, ct);

        // (h) An urgent message is alerted when it arrives (a person who asks for a draft is looking at it already), and BEFORE the row of the log is written: the row is what makes a run that
        // is repeated return at once, so an alert that came after it would be lost for ever by a job that stops in between. The alert is idempotent (its keys are the message and the person),
        // so a run repeated before the row exists tells nobody twice; one that cannot even be handed to the job of the alerts fails the run, which Hangfire repeats.
        if (classification.Urgency == AssistantUrgency.Urgent && !options.Requested)
            await AlertUrgentAsync(orgId, thread, message, ct);

        var row = NewRow(run, propertyId, effective.InstructionVersion, model, version, activityId);
        row.Situation = classification.Situation;
        row.Urgency = classification.Urgency;
        row.SensitiveCategories = composed.Sensitive;
        row.ScoreSituation = classification.Situation == AssistantSituation.Other ? 0m : classification.SituationMatch;

        AssistantDraftOutcome outcome;
        if (composed.Escalation is { } escalation)
        {
            row.Decision = AssistantDecision.Escalated;
            row.GateFailures = [escalation];
            outcome = await SaveAsync(run, row, ct);
        }
        else if (composed.Failure is { } failure)
        {
            // The draft could not be written (the budget, the provider, an answer that cannot be used): the classification is kept, the reason is the code.
            row.Decision = AssistantDecision.NotHandled;
            row.GateFailures = [failure];
            outcome = await SaveDecisionAsync(run, row, ct);
        }
        else
        {
            outcome = await DecideAsync(run, row, thread, composed, effective, ct);
        }

        logger.LogInformation(
            "Assistant on message {MessageId} of conversation {ThreadKey} (org {OrgId}): {Decision} {Reason}, situation {Situation}, score {Score}",
            inboundMessageId,
            threadKey.ToString(),
            orgId,
            outcome.Decision,
            outcome.Reason ?? "-",
            classification.Situation,
            outcome.ConfidenceScore?.ToString(CultureInfo.InvariantCulture) ?? "-");
        return outcome;
    }

    public async Task<AssistantDraftOutcome> DraftNowAsync(HostScope scope, MessageThreadKey threadKey, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // The row check: the conversation in the org and the reach of the caller, else the 404 of a conversation that does not exist.
        await MessageConversationLookup.FindAsync(db, scope, threadKey, ct);

        var thread = await ThreadOfAsync(scope.OrgId, threadKey, ct);
        var last = thread is null ? null : await LastGuestMessageAsync(scope.OrgId, thread, ct);
        if (last is not { } messageId)
            throw new DomainConflictException(AssistantDecisionCodes.NoGuestMessageCode, "AssistantNoGuestMessage");

        return await ProcessInboundAsync(
            scope.OrgId,
            threadKey,
            messageId,
            new AssistantProcessOptions { Requested = true, RequestedBy = scope, RequestedByUserId = userId },
            ct);
    }

    public async Task<AssistantSimulation> SimulateAsync(Guid orgId, Guid propertyId, string? text, string? language, CancellationToken ct = default)
    {
        var example = GuestMessageRules.NormalizeText(text);
        if (example is null || example.Length > GuestMessageRules.TextMaxLength)
            throw SimulationInvalid();

        string? guestLanguage = null;
        if (!string.IsNullOrWhiteSpace(language))
        {
            guestLanguage = language.Trim().ToLowerInvariant();
            if (guestLanguage.Length != 2 || !guestLanguage.All(c => c is >= 'a' and <= 'z'))
                throw SimulationInvalid();
        }

        var hostName = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Id == propertyId)
            .Select(p => new { p.Org.HostName })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("The property was not found") { Code = "property_not_found", MessageKey = "PropertyNotFound" };

        // The current settings of the property: the switch of the organization is not looked at (a host tries the assistant before turning it on).
        var effective = await rules.ResolveEffectiveAsync(orgId, propertyId, ct);
        if (!(await aiSettings.GetEffectiveAsync(ct)).IsExternalProviderActive())
            return NotSimulated(AssistantDecisionCodes.AiProviderNotConfigured);

        var names = new AssistantPromptNames([], [hostName.HostName], []);
        var transcript = new List<AssistantPromptMessage> { new(true, AssistantPromptBuilder.Minimize(example, names, PiiScrubber.Scrub)) };
        var composed = await ComposeAsync(
            new Composing(orgId, propertyId, transcript, names, effective, AssistantSituationService.SampleValues, DirectRefusalOf, guestLanguage, Guid.Empty),
            ct);

        if (composed.Classification is not { } classification)
            return NotSimulated(composed.Failure!);

        if (composed.Escalation is { } escalation)
        {
            return new AssistantSimulation(
                AssistantDecision.Escalated, escalation, classification.Situation, classification.Urgency, composed.Sensitive, null, null, [], false, [escalation]);
        }

        if (composed.Failure is { } failure)
        {
            return new AssistantSimulation(
                AssistantDecision.NotHandled, failure, classification.Situation, classification.Urgency, composed.Sensitive, null, null, composed.Facts, false, [failure]);
        }

        var verdict = await gate.EvaluateAsync(ContextOf(orgId, propertyId, null, composed, effective, simulation: true), ct);
        return new AssistantSimulation(
            AssistantDecision.Drafted,
            null,
            classification.Situation,
            classification.Urgency,
            composed.Sensitive,
            composed.Confidence,
            composed.FinalText,
            composed.Facts,
            verdict.Allowed,
            verdict.FailureCodes);
    }

    // ─── (c), (d), (e) ───

    /// <summary>What <see cref="ComposeAsync"/> works on: the conversation (minimized), the names it knows, the rules, the values of the variables and the rule of the channel.</summary>
    private sealed record Composing(
        Guid OrgId,
        Guid PropertyId,
        IReadOnlyList<AssistantPromptMessage> Transcript,
        AssistantPromptNames Names,
        EffectiveAssistantRules Rules,
        MessageTemplateValues Values,
        Func<string, string?> RefusalOf,
        string? GuestLanguage,
        Guid MessageId);

    /// <summary>
    /// What the classification and the draft gave. <see cref="Classification"/> is <c>null</c> when the classification itself failed (<see cref="Failure"/> says why); with a classification,
    /// <see cref="Escalation"/> says why a person answers, or <see cref="Failure"/> why the draft could not be written, or the draft is there with its confidence.
    /// </summary>
    private sealed record Composition(
        AssistantClassification? Classification,
        AssistantSituationInfo? Info,
        ResolvedSituation? Resolved,
        SensitiveCategory Sensitive,
        string? Escalation,
        IReadOnlyList<RetrievedFact> Facts,
        string? FinalText,
        bool FromTemplate,
        AssistantConfidenceScore? Confidence,
        AiModelTier? LastTier,
        string? Failure)
    {
        public static Composition Failed(string code, AiModelTier? tier = null) =>
            new(null, null, null, SensitiveCategory.None, null, [], null, false, null, tier, code);
    }

    private async Task<Composition> ComposeAsync(Composing input, CancellationToken ct)
    {
        // (c) The classification (Economy tier) and the setting of its situation.
        var classified = await CallAsync(
            AssistantPromptBuilder.BuildClassification(input.Transcript),
            AiModelTier.Economy,
            AssistantPromptBuilder.CallLabel("classify", input.MessageId),
            ct);
        if (classified.Failure is { } classificationFailure)
            return Composition.Failed(classificationFailure, classified.Tier);

        var classification = AssistantClassification.Parse(classified.Content);
        if (input.GuestLanguage is { } given)
            classification = classification with { Language = given };

        var info = AssistantSituationCatalog.Of(classification.Situation);
        var resolved = await situations.ResolveAsync(input.OrgId, input.PropertyId, classification.Situation, ct);
        var sensitive = classification.SensitiveCategories | info.SensitiveCategories;
        var sourceTopic = resolved.Source ?? info.SourceTopic;
        var topics = TopicsOf(info, sourceTopic);

        var escalation = EscalationOf(resolved.Mode, classification.Situation, topics, input.Rules);
        if (escalation is not null)
            return new Composition(classification, info, resolved, sensitive, escalation, [], null, false, null, classified.Tier, null);

        // (d) The facts and the draft: the template of the situation when every variable resolves, else the model from the facts.
        var language = DraftLanguageOf(classification.Language, input.Rules);
        IReadOnlyList<RetrievedFact> facts = topics.Count == 0 ? [] : await knowledge.RetrieveAsync(input.OrgId, input.PropertyId, topics, language, ct);
        var culture = CultureOf(language);

        string body;
        string? draftLanguage = null;
        var missingFacts = false;
        IReadOnlyList<string> draftTopics = [];
        var fromTemplate = false;
        var lastTier = classified.Tier;
        if (resolved.Template is { } template && RenderTemplate(template, input.Values, culture) is { } rendered)
        {
            body = rendered;
            fromTemplate = true;
        }
        else
        {
            var promptFacts = facts
                .Select(fact => new AssistantPromptFact(fact.Topic, AssistantPromptBuilder.Minimize(fact.Text, input.Names, PiiScrubber.Scrub)))
                .ToList();
            var drafted = await CallAsync(
                AssistantPromptBuilder.BuildDraft(input.Transcript, promptFacts, input.Rules, info.Code, language, greetByName: input.Values.GuestName is not null),
                AiModelTier.Standard,
                AssistantPromptBuilder.CallLabel("draft", input.MessageId),
                ct);
            lastTier = drafted.Tier ?? lastTier;
            if (drafted.Failure is { } draftFailure)
                return new Composition(classification, info, resolved, sensitive, null, facts, null, false, null, lastTier, draftFailure);

            var answer = AssistantPromptBuilder.ParseDraft(drafted.Content);
            var filled = answer is null
                ? string.Empty
                : MessageTemplateRenderer.FillKnown(AssistantPromptBuilder.ToVariables(answer.Draft), input.Values, culture, out _);
            if (answer is null || filled.Length == 0)
            {
                logger.LogWarning("The answer of the AI to a draft of the assistant (org {OrgId}) could not be used ({Length} characters)", input.OrgId, drafted.Content?.Length ?? 0);
                return new Composition(classification, info, resolved, sensitive, null, facts, null, false, null, lastTier, AssistantDecisionCodes.AiInvalidAnswer);
            }

            body = filled;
            draftLanguage = answer.Language ?? language;
            missingFacts = answer.MissingFacts;
            draftTopics = answer.Topics;
        }

        // (e) The confidence of the text the guest would read.
        var finalText = AssistantPromptBuilder.WithSignature(body, input.Rules.Signature);
        var confidence = AssistantConfidence.Compute(new AssistantConfidenceInput(
            classification.Situation,
            classification.SituationMatch,
            facts.Select(fact => fact.Topic).ToHashSet(),
            missingFacts,
            body,
            finalText,
            facts.Select(fact => fact.Text).ToList(),
            draftTopics,
            draftLanguage,
            input.Rules,
            input.RefusalOf(finalText)));

        return new Composition(classification, info, resolved, sensitive, null, facts, finalText, fromTemplate, confidence, lastTier, null);
    }

    /// <summary>
    /// Why a person answers instead of the assistant: the situation is set to escalate, it (or a topic it needs) is forbidden, or the rules allow a list of topics and it is not in it
    /// (an empty list allows everything); <c>null</c> when the assistant may write the draft.
    /// </summary>
    private static string? EscalationOf(AssistantMode mode, AssistantSituation situation, IReadOnlyCollection<KnowledgeTopic> topics, EffectiveAssistantRules rules)
    {
        if (mode == AssistantMode.Escalate)
            return AssistantDecisionCodes.ModeEscalate;

        if (AssistantTopicNames.IsListed(rules.ForbiddenTopics, situation, null) || topics.Any(topic => AssistantTopicNames.IsListed(rules.ForbiddenTopics, null, topic)))
            return AssistantDecisionCodes.ForbiddenTopic;

        if (rules.AllowedTopics.Count > 0
            && !AssistantTopicNames.IsListed(rules.AllowedTopics, situation, null)
            && !topics.Any(topic => AssistantTopicNames.IsListed(rules.AllowedTopics, null, topic)))
        {
            return AssistantDecisionCodes.TopicNotAllowed;
        }

        return null;
    }

    /// <summary>The topics the facts of a situation are looked for in: the ones it requires and the one its answer comes from. None for a situation that needs none («no topics is no facts»).</summary>
    private static List<KnowledgeTopic> TopicsOf(AssistantSituationInfo info, KnowledgeTopic? source)
    {
        var topics = info.RequiredTopics.ToList();
        if (source is { } topic && !topics.Contains(topic))
            topics.Add(topic);

        return topics;
    }

    /// <summary>The language of the draft: the guest's when the rules allow it, else the first language of the rules.</summary>
    private static string DraftLanguageOf(string? guestLanguage, EffectiveAssistantRules effective) =>
        guestLanguage is not null && effective.Languages.Contains(guestLanguage, StringComparer.OrdinalIgnoreCase)
            ? guestLanguage
            : effective.Languages.FirstOrDefault() ?? "it";

    private static CultureInfo CultureOf(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>The template filled in, or <c>null</c> when a variable has no value in this conversation (the model then writes the draft).</summary>
    private static string? RenderTemplate(string template, MessageTemplateValues values, CultureInfo culture)
    {
        try
        {
            return MessageTemplateRenderer.Render(template, values, culture);
        }
        catch (DomainRuleException)
        {
            return null;
        }
    }

    // ─── The calls to the AI ───

    private sealed record CallResult(string? Content, AiModelTier? Tier, string? Failure);

    /// <summary>One call to the provider. The budget used up and any failure of the provider are codes, never exceptions; a provider that did not answer is «not configured».</summary>
    private async Task<CallResult> CallAsync(AiRequest request, AiModelTier tier, string label, CancellationToken ct)
    {
        try
        {
            var result = await ai.GenerateAsync(request, tier, label, ct);
            return result.ProviderConfigured
                ? new CallResult(result.Content, result.TierUsed, null)
                : new CallResult(null, null, AssistantDecisionCodes.AiProviderNotConfigured);
        }
        catch (AiBudgetExceededException)
        {
            logger.LogWarning("The AI budget of the platform is used up: the assistant did not call the provider");
            return new CallResult(null, null, AssistantDecisionCodes.AiBudgetExceeded);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A timeout of the provider is a TaskCanceledException too: it is the provider that did not answer, not the caller who left. The type only: never the prompt.
            logger.LogWarning("The AI provider failed on a call of the assistant ({ErrorType})", ex.GetType().Name);
            return new CallResult(null, null, AssistantDecisionCodes.AiProviderError);
        }
    }

    // ─── (f), (g): the gate, the send or the queue, the log ───

    private async Task<AssistantDraftOutcome> DecideAsync(
        Run run,
        AssistantDecisionLog row,
        ThreadState thread,
        Composition composed,
        EffectiveAssistantRules effective,
        CancellationToken ct)
    {
        var confidence = composed.Confidence!;
        row.ConfidenceScore = confidence.Score;
        row.ScoreSituation = confidence.SituationMatch;
        row.ScoreFacts = confidence.FactCoverage;
        row.ScoreRules = confidence.RuleCompliance;
        row.SourceRefs = SourceRefsOf(composed);

        var text = composed.FinalText!;
        var failures = new List<string>();
        if (!run.Options.Requested)
        {
            var verdict = await gate.EvaluateAsync(ContextOf(run.OrgId, row.PropertyId!.Value, run.Key.ToString(), composed, effective, simulation: false), ct);
            if (verdict.Allowed)
            {
                var sent = await SendAsync(run, text, composed.Info!.Code, ct);
                if (sent.Failure is null)
                {
                    row.Decision = AssistantDecision.AutoSent;
                    row.OutboundMessageId = sent.MessageId;
                    return await SaveAsync(run, row, ct);
                }

                failures.Add(AssistantDecisionCodes.AutoSendFailed);
                failures.Add(sent.Failure);
            }
            else
            {
                failures.AddRange(verdict.FailureCodes);
            }
        }

        row.Decision = AssistantDecision.Drafted;
        row.GateFailures = failures;
        row.ApprovalItemId = await QueueAsync(run, row.PropertyId!.Value, text, composed, effective.InstructionVersion, ct);
        return await SaveAsync(run, row, ct);
    }

    private AutoSendContext ContextOf(Guid orgId, Guid propertyId, string? threadKey, Composition composed, EffectiveAssistantRules effective, bool simulation)
    {
        var classification = composed.Classification!;
        return new AutoSendContext(
            orgId,
            propertyId,
            threadKey,
            classification.Situation,
            composed.Resolved!.Mode,
            composed.Resolved.ModeSince,
            classification.Urgency,
            composed.Sensitive,
            classification.IntentFlags,
            composed.Confidence!,
            effective,
            composed.FromTemplate,
            simulation,
            Now());
    }

    private sealed record SendResult(Guid? MessageId, string? Failure);

    /// <summary>
    /// The send of a draft the gate allowed, as the assistant (origin <see cref="MessageOrigin.Assistant"/>, the situation as the rule code), with the key of the message so a run that is
    /// repeated sends nothing twice. A send whose outcome is not known is the same message and counts as sent; a refusal of the channel is its code, and the draft then waits for a person.
    /// </summary>
    private async Task<SendResult> SendAsync(Run run, string text, string situationCode, CancellationToken ct)
    {
        try
        {
            var result = await replies.ReplyAsync(
                new HostScope(run.OrgId), run.Key, text, $"assistant-{run.MessageId:N}", null, MessageOrigin.Assistant, situationCode, ct);
            return result.State == OtaMessageDeliveryState.Error
                ? new SendResult(null, result.ErrorCode ?? MessageReplyService.ChannelReportedErrorCode)
                : new SendResult(result.Message?.Id, null);
        }
        catch (DomainException ex)
        {
            return new SendResult(null, ex.Code);
        }
        catch (NotFoundException ex)
        {
            return new SendResult(null, ex.Code ?? AssistantDecisionCodes.ThreadNotFound);
        }
        catch (OctorateException ex)
        {
            logger.LogWarning("The send of a draft of the assistant (org {OrgId}) was refused by the channel manager ({Code})", run.OrgId, ex.Code);
            return new SendResult(null, ex.Code);
        }
    }

    /// <summary>
    /// The draft in the approval queue, which raises the «Draft ready» alert: an item of type <see cref="ApprovalItemTypes.MessageDraft"/> about the message (<c>ItemId</c> is the inbound
    /// message), with the property, the action that sends it, the score, the situation and the conversation. A run repeated after it was queued finds that item; a draft a person asked
    /// for supersedes the drafts of the conversation that still wait (rejected with <see cref="AssistantDecisionCodes.RegeneratedReason"/>).
    /// </summary>
    private async Task<Guid> QueueAsync(Run run, Guid propertyId, string text, Composition composed, int instructionVersion, CancellationToken ct)
    {
        if (run.Options.Requested)
        {
            await SupersedeAsync(run, ct);
        }
        else if (await QueuedDraftOfAsync(run.OrgId, run.MessageId, ct) is { } queued)
        {
            return queued;
        }

        var item = await approvals.SubmitForApprovalAsync(
            run.OrgId,
            ApprovalItemTypes.MessageDraft,
            run.MessageId,
            text,
            isAiGenerated: !composed.FromTemplate,
            contentVersion: string.Create(CultureInfo.InvariantCulture, $"instructions-v{instructionVersion}"),
            propertyId: propertyId,
            originAction: ApprovalOriginActions.SendGuestMessage,
            confidenceScore: composed.Confidence!.Score,
            situationCode: composed.Info!.Code,
            threadKey: run.Key.ToString(),
            cancellationToken: ct);
        return item.Id;
    }

    /// <summary>The draft of this message already in the queue (waiting or approved), if a run stopped after queueing it.</summary>
    private Task<Guid?> QueuedDraftOfAsync(Guid orgId, Guid messageId, CancellationToken ct) =>
        db.ApprovalQueueItems
            .AsNoTracking()
            .Where(i => i.OrgId == orgId
                        && i.ItemType == ApprovalItemTypes.MessageDraft
                        && i.ItemId == messageId
                        && (i.Status == ApprovalStatus.Pending || i.Status == ApprovalStatus.Approved))
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>The drafts of the conversation that still wait are rejected with <see cref="AssistantDecisionCodes.RegeneratedReason"/>, in the reach of the person who asked for a new one.</summary>
    private async Task SupersedeAsync(Run run, CancellationToken ct)
    {
        // Only a person supersedes, in its own reach: a request with nobody behind it supersedes nothing.
        if (run.Options.RequestedBy is not { } requestedBy || run.Options.RequestedByUserId is not { Length: > 0 } requestedByUserId)
            return;

        var key = run.Key.ToString();
        var waiting = await db.ApprovalQueueItems
            .AsNoTracking()
            .Where(i => i.OrgId == run.OrgId && i.ItemType == ApprovalItemTypes.MessageDraft && i.ThreadKey == key && i.Status == ApprovalStatus.Pending)
            .Select(i => i.Id)
            .ToListAsync(ct);

        foreach (var itemId in waiting)
        {
            try
            {
                await approvals.RejectAsync(requestedBy, itemId, requestedByUserId, AssistantDecisionCodes.RegeneratedReason, ct);
            }
            catch (Exception ex) when (ex is NotFoundException or DomainConflictException)
            {
                // Decided meanwhile, or out of the reach of the person: it is not this request's to supersede.
                logger.LogInformation("The waiting draft {ItemId} of org {OrgId} was not superseded ({ErrorType})", itemId, run.OrgId, ex.GetType().Name);
            }
        }
    }

    // ─── (h) and the «To do» ───

    /// <summary>
    /// The «To do» of the conversation. There is one while it is open (<see cref="IInternalActivityService.CreateAsync"/> returns the open one and changes nothing), so a message that
    /// is more urgent than the one that opened it, or due sooner, raises it (<see cref="IInternalActivityService.RaiseUrgencyAsync"/>): an urgent message that follows an escalated
    /// one is High and due in an hour, never left as Medium and due the day after. A message that is less urgent never lowers it.
    /// </summary>
    private async Task<Guid> RaiseTodoAsync(Guid orgId, Guid propertyId, ThreadState thread, AssistantUrgency urgency, string situationCode, CancellationToken ct)
    {
        var request = new CreateTodoRequest
        {
            Type = ActivityType.MessageToReply,
            Action = ActivityAction.ReplyToMessage,
            EntityType = thread.Source == HostMessageSource.Ota ? ThreadEntityType : InternalActivity.BookingEntityType,
            EntityId = thread.Key.Id,
            Urgency = urgency is AssistantUrgency.Urgent or AssistantUrgency.High ? ActivityUrgency.High : ActivityUrgency.Medium,
            PropertyId = propertyId,
            OrgId = orgId,
            Deadline = Now() + _options.Escalation.DeadlineAfter(urgency),
            Reason = situationCode,
        };

        var activityId = await activities.CreateAsync(request, ct);
        await activities.RaiseUrgencyAsync(activityId, request.Urgency, request.Deadline, request.Reason, ct);
        return activityId;
    }

    /// <summary>
    /// The alert of an urgent message to the people who reach the property, through their preferences (<see cref="NotificationEventType.UrgentGuestMessage"/>): no text, the route of the
    /// conversation. A person who could not be alerted is handed to the job of the alerts (retried by Hangfire); a failure that cannot even be handed to it is <b>not swallowed</b>: it fails
    /// the run, which the job repeats (the row of the log is not written yet, and the alert is idempotent).
    /// </summary>
    private Task AlertUrgentAsync(Guid orgId, ThreadState thread, InboundMessage message, CancellationToken ct) =>
        hostAlerts.DeliverOrRetryAsync(
            new HostMessageAlert(orgId, thread.Source, NotificationEventType.UrgentGuestMessage, thread.RowId, message.Id, message.At),
            ct);

    // ─── The log ───

    private sealed record Run(Guid OrgId, MessageThreadKey Key, Guid MessageId, AssistantProcessOptions Options, AssistantDecisionLog? Existing);

    private Task<AssistantDecisionLog?> FindLogAsync(Guid orgId, Guid messageId, CancellationToken ct) =>
        db.AssistantDecisionLogs.FirstOrDefaultAsync(l => l.OrgId == orgId && l.InboundMessageId == messageId, ct);

    private AssistantDecisionLog NewRow(Run run, Guid? propertyId, int instructionVersion, string? model, string? modelVersion, Guid? activityId) => new()
    {
        OrgId = run.OrgId,
        PropertyId = propertyId,
        ThreadKey = run.Key.ToString(),
        InboundMessageId = run.MessageId,
        InstructionVersion = instructionVersion,
        Model = model,
        ModelVersion = modelVersion,
        ActivityId = activityId,
        CreatedAt = Now(),
    };

    /// <summary>A message not handled: the row with the code and nothing else.</summary>
    private Task<AssistantDraftOutcome> NotHandledAsync(Run run, Guid? propertyId, string code, CancellationToken ct) =>
        NotHandledAsync(run, propertyId, code, 0, ct);

    private async Task<AssistantDraftOutcome> NotHandledAsync(Run run, Guid? propertyId, string code, int instructionVersion, CancellationToken ct)
    {
        var row = NewRow(run, propertyId, instructionVersion, null, null, null);
        row.Decision = AssistantDecision.NotHandled;
        row.GateFailures = [code];
        var outcome = await SaveDecisionAsync(run, row, ct);

        logger.LogInformation(
            "Assistant on message {MessageId} of conversation {ThreadKey} (org {OrgId}): not handled ({Reason})",
            run.MessageId,
            run.Key.ToString(),
            run.OrgId,
            code);
        return outcome;
    }

    /// <summary>
    /// A decision that is not a draft. A person who asked for a new draft of a message the assistant had decided already, and got none, changes nothing of that decision (the draft that waits
    /// still waits); the reason is in the answer.
    /// </summary>
    private async Task<AssistantDraftOutcome> SaveDecisionAsync(Run run, AssistantDecisionLog row, CancellationToken ct) =>
        run.Existing is { } existing && row.Decision == AssistantDecision.NotHandled
            ? OutcomeOf(existing, alreadyProcessed: false) with { Decision = AssistantDecision.NotHandled, Reason = row.GateFailures.FirstOrDefault(), ApprovalItemId = null }
            : await SaveAsync(run, row, ct);

    /// <summary>
    /// Writes the row of the message: a new one, or the one of a message a person asked a new draft for (its «To do» is kept when the new decision raised none). Two runs that write the row
    /// of one message at once: the unique index keeps the first, which is returned.
    /// </summary>
    private async Task<AssistantDraftOutcome> SaveAsync(Run run, AssistantDecisionLog row, CancellationToken ct)
    {
        if (run.Existing is { } existing)
        {
            existing.PropertyId = row.PropertyId;
            existing.Situation = row.Situation;
            existing.Urgency = row.Urgency;
            existing.SensitiveCategories = row.SensitiveCategories;
            existing.ConfidenceScore = row.ConfidenceScore;
            existing.ScoreSituation = row.ScoreSituation;
            existing.ScoreFacts = row.ScoreFacts;
            existing.ScoreRules = row.ScoreRules;
            existing.Decision = row.Decision;
            existing.GateFailures = row.GateFailures;
            existing.Model = row.Model;
            existing.ModelVersion = row.ModelVersion;
            existing.InstructionVersion = row.InstructionVersion;
            existing.SourceRefs = row.SourceRefs;
            existing.ApprovalItemId = row.ApprovalItemId;
            existing.OutboundMessageId = row.OutboundMessageId;
            if (row.ActivityId is not null)
            {
                existing.ActivityId = row.ActivityId;
                existing.ActivityClosedAt = null;
            }

            existing.ApproverUserId = null;
            existing.ApprovedWithoutEdit = null;
            existing.CreatedAt = row.CreatedAt;
            await db.SaveChangesAsync(ct);
            return OutcomeOf(existing, alreadyProcessed: false);
        }

        db.AssistantDecisionLogs.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, AssistantDecisionLog.InboundMessageIndexName))
        {
            db.Entry(row).State = EntityState.Detached;
            var first = await FindLogAsync(run.OrgId, run.MessageId, ct);
            if (first is null)
                throw;

            return OutcomeOf(first, alreadyProcessed: true);
        }

        return OutcomeOf(row, alreadyProcessed: false);
    }

    private static AssistantDraftOutcome OutcomeOf(AssistantDecisionLog row, bool alreadyProcessed) => new(
        row.InboundMessageId,
        row.Decision,
        row.Decision is AssistantDecision.NotHandled or AssistantDecision.Escalated ? row.GateFailures.FirstOrDefault() : null,
        row.Situation,
        row.Urgency,
        row.ConfidenceScore,
        row.ApprovalItemId,
        row.ActivityId,
        alreadyProcessed);

    /// <summary>The sources of the draft for the log, as JSON: the references of the facts (<c>entry:{id}</c>, <c>listing:{field}</c>) and <c>template:{situation}</c> for a template. No text.</summary>
    private static string SourceRefsOf(Composition composed)
    {
        var sources = composed.Facts.Select(fact => fact.SourceRef).Distinct(StringComparer.Ordinal).ToList();
        if (composed.FromTemplate)
            sources.Add($"template:{composed.Info!.Code}");

        return JsonSerializer.Serialize(sources);
    }

    private static AssistantSimulation NotSimulated(string reason) =>
        new(AssistantDecision.NotHandled, reason, null, null, SensitiveCategory.None, null, null, [], false, [reason]);

    private static DomainRuleException SimulationInvalid() => new(AssistantDecisionCodes.SimulationInvalidCode, "AssistantSimulationInvalid");

    private static string? Bounded(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= AssistantDecisionLog.ModelMaxLength ? value
        : value[..AssistantDecisionLog.ModelMaxLength];

    // ─── The conversation ───

    /// <summary>A conversation as the pipeline reads it: where its rows are, its property (its own: an unassigned conversation has none), its channel, and whether it takes an answer.</summary>
    private sealed record ThreadState(
        MessageThreadKey Key,
        HostMessageSource Source,
        Guid RowId,
        Guid? PropertyId,
        OtaChannel? Channel,
        OtaThreadKind? Kind,
        bool Closed);

    /// <summary>The message of the guest: its id and when it was written (on the channel for a conversation of a channel).</summary>
    private sealed record InboundMessage(Guid Id, DateTime At, DateTime CreatedAt);

    private async Task<ThreadState?> ThreadOfAsync(Guid orgId, MessageThreadKey key, CancellationToken ct)
    {
        if (key.Source == MessageThreadSource.Ota)
        {
            var ota = await db.OtaMessageThreads
                .AsNoTracking()
                .Where(t => t.OrgId == orgId && t.Id == key.Id)
                .Select(t => new { t.Id, t.PropertyId, t.ChannelSource, t.Kind, Anonymized = t.AnonymizedAt != null })
                .FirstOrDefaultAsync(ct);
            return ota is null
                ? null
                : new ThreadState(key, HostMessageSource.Ota, ota.Id, ota.PropertyId, ota.ChannelSource, ota.Kind, ota.Anonymized);
        }

        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.Id == key.Id)
            .Select(b => new { b.PropertyId, b.Source, b.Status, b.CheckOutDate })
            .FirstOrDefaultAsync(ct);
        if (booking is null)
            return null;

        var threadId = await db.GuestThreads
            .AsNoTracking()
            .Where(t => t.OrgId == orgId && t.BookingId == key.Id)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
        var closed = GuestMessageRules.StateOf(booking.Source, booking.Status, booking.CheckOutDate, Now()) != GuestThreadState.Open;
        return new ThreadState(key, HostMessageSource.Direct, threadId ?? Guid.Empty, booking.PropertyId, null, null, closed);
    }

    private Task<InboundMessage?> InboundOfAsync(Guid orgId, ThreadState thread, Guid messageId, CancellationToken ct) =>
        thread.Source == HostMessageSource.Ota
            ? db.OtaMessages
                .AsNoTracking()
                .Where(m => m.OrgId == orgId && m.ThreadId == thread.RowId && m.Id == messageId && m.Direction == OtaMessageDirection.Inbound)
                .Select(m => new InboundMessage(m.Id, m.SentAt, m.CreatedAt))
                .FirstOrDefaultAsync(ct)
            : db.GuestMessages
                .AsNoTracking()
                .Where(m => m.OrgId == orgId && m.ThreadId == thread.RowId && m.Id == messageId && m.Sender == GuestMessageSender.Guest)
                .Select(m => new InboundMessage(m.Id, m.CreatedAt, m.CreatedAt))
                .FirstOrDefaultAsync(ct);

    /// <summary>The last message the guest wrote in the conversation, or <c>null</c> when it wrote none.</summary>
    private Task<Guid?> LastGuestMessageAsync(Guid orgId, ThreadState thread, CancellationToken ct) =>
        thread.Source == HostMessageSource.Ota
            ? db.OtaMessages
                .AsNoTracking()
                .Where(m => m.OrgId == orgId && m.ThreadId == thread.RowId && m.Direction == OtaMessageDirection.Inbound)
                .OrderByDescending(m => m.SentAt)
                .ThenByDescending(m => m.CreatedAt)
                .Select(m => (Guid?)m.Id)
                .FirstOrDefaultAsync(ct)
            : db.GuestMessages
                .AsNoTracking()
                .Where(m => m.OrgId == orgId && m.ThreadId == thread.RowId && m.Sender == GuestMessageSender.Guest)
                .OrderByDescending(m => m.CreatedAt)
                .ThenByDescending(m => m.Id)
                .Select(m => (Guid?)m.Id)
                .FirstOrDefaultAsync(ct);

    /// <summary>The guest wrote again after this message: the later one is the one to answer.</summary>
    private Task<bool> HasLaterGuestMessageAsync(Guid orgId, ThreadState thread, InboundMessage message, CancellationToken ct) =>
        thread.Source == HostMessageSource.Ota
            ? db.OtaMessages.AnyAsync(
                m => m.OrgId == orgId
                     && m.ThreadId == thread.RowId
                     && m.Direction == OtaMessageDirection.Inbound
                     && m.Id != message.Id
                     && (m.SentAt > message.At || (m.SentAt == message.At && m.CreatedAt > message.CreatedAt)),
                ct)
            : db.GuestMessages.AnyAsync(
                m => m.OrgId == orgId && m.ThreadId == thread.RowId && m.Sender == GuestMessageSender.Guest && m.Id != message.Id && m.CreatedAt > message.At,
                ct);

    /// <summary>The host side wrote after this message (a message that the channel refused is not an answer).</summary>
    private Task<bool> HostAnsweredSinceAsync(Guid orgId, ThreadState thread, InboundMessage message, CancellationToken ct) =>
        thread.Source == HostMessageSource.Ota
            ? db.OtaMessages.AnyAsync(
                m => m.OrgId == orgId
                     && m.ThreadId == thread.RowId
                     && m.Direction == OtaMessageDirection.Outbound
                     && m.DeliveryState != OtaMessageDeliveryState.Error
                     && m.SentAt > message.At,
                ct)
            : db.GuestMessages.AnyAsync(
                m => m.OrgId == orgId && m.ThreadId == thread.RowId && m.Sender == GuestMessageSender.Host && m.CreatedAt > message.At,
                ct);

    /// <summary>The last <see cref="AssistantPromptBuilder.ConversationMessages"/> messages of the conversation, oldest first, minimized; a message the channel refused is left out.</summary>
    private async Task<List<AssistantPromptMessage>> TranscriptAsync(Guid orgId, ThreadState thread, AssistantPromptNames names, CancellationToken ct)
    {
        List<(bool FromGuest, string Text)> latest;
        if (thread.Source == HostMessageSource.Ota)
        {
            var rows = await db.OtaMessages
                .AsNoTracking()
                .Where(m => m.OrgId == orgId
                            && m.ThreadId == thread.RowId
                            && (m.Direction == OtaMessageDirection.Inbound || m.DeliveryState != OtaMessageDeliveryState.Error))
                .OrderByDescending(m => m.SentAt)
                .ThenByDescending(m => m.CreatedAt)
                .Take(AssistantPromptBuilder.ConversationMessages)
                .Select(m => new { m.Direction, m.Text })
                .ToListAsync(ct);
            latest = rows.Select(r => (r.Direction == OtaMessageDirection.Inbound, r.Text)).ToList();
        }
        else
        {
            var rows = await db.GuestMessages
                .AsNoTracking()
                .Where(m => m.OrgId == orgId && m.ThreadId == thread.RowId)
                .OrderByDescending(m => m.CreatedAt)
                .ThenByDescending(m => m.Id)
                .Take(AssistantPromptBuilder.ConversationMessages)
                .Select(m => new { m.Sender, m.Text })
                .ToListAsync(ct);
            latest = rows.Select(r => (r.Sender == GuestMessageSender.Guest, r.Text)).ToList();
        }

        latest.Reverse();
        return latest
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => new AssistantPromptMessage(m.FromGuest, AssistantPromptBuilder.Minimize(m.Text, names, PiiScrubber.Scrub)))
            .ToList();
    }

    /// <summary>
    /// The names and the codes the conversation knows: the guest's (the booking and the name the channel gives; none for an erased guest, whose name is gone), the host's, the code of the
    /// booking and its references on the channel.
    /// </summary>
    private async Task<AssistantPromptNames> NamesOfAsync(Guid orgId, ConversationFacts conversation, CancellationToken ct)
    {
        var codes = new List<string?> { conversation.BookingCode };
        if (conversation.BookingId is { } bookingId)
        {
            var references = await db.Bookings
                .AsNoTracking()
                .Where(b => b.OrgId == orgId && b.Id == bookingId)
                .Select(b => new { b.ExternalId, b.OctorateReservationId })
                .FirstOrDefaultAsync(ct);
            codes.Add(references?.ExternalId);
            codes.Add(references?.OctorateReservationId);
        }

        List<string?> guest = conversation.GuestErased
            ? []
            : [conversation.GuestFirstName, conversation.GuestLastName, conversation.ThreadGuestName];
        return new AssistantPromptNames(guest, [conversation.HostName], codes);
    }

    /// <summary>The values of the variables of a template in this conversation (the same as the composer's).</summary>
    private static MessageTemplateValues ValuesOf(ConversationFacts conversation) => new(
        conversation.GuestFirstNameForTemplates,
        conversation.PropertyName,
        conversation.BookingCode is null ? null : BookingCodes.Format(conversation.BookingCode),
        conversation.CheckInDate is { } checkIn ? RomeCalendar.DateInRome(checkIn) : null,
        conversation.CheckOutDate is { } checkOut ? RomeCalendar.DateInRome(checkOut) : null,
        conversation.CheckInFrom,
        conversation.CheckOutBy,
        conversation.HostNameForTemplates);

    /// <summary>The rule of the channel that refuses <paramref name="text"/> in this conversation, or <c>null</c> when it may go out.</summary>
    private static string? RefusalOf(ThreadState thread, string text) =>
        thread is { Source: HostMessageSource.Ota, Channel: { } channel, Kind: { } kind }
            ? MessageChannelRules.CheckOutgoing(channel, kind, text)
            : DirectRefusalOf(text);

    /// <summary>The rule of a conversation with the guest of a direct booking: a text of 1 to <see cref="GuestMessageRules.TextMaxLength"/> characters.</summary>
    private static string? DirectRefusalOf(string text) =>
        GuestMessageRules.NormalizeText(text) is { Length: <= GuestMessageRules.TextMaxLength } ? null : MessageChannelRules.TextInvalidCode;

    /// <summary>Now, cut to the microsecond PostgreSQL keeps.</summary>
    private DateTime Now()
    {
        var utc = _clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }
}
