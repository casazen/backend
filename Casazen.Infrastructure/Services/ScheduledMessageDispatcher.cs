using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Scheduling;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IScheduledMessageDispatcher"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Once.</b> A try claims the row first (<see cref="ScheduledMessage.LastCheckedAt"/> held for <see cref="ScheduledMessageRules.ClaimLease"/>, saved with the <c>xmin</c> of the row): the routine that
/// fires, the sweep and «Send now» arriving together find the row held and do nothing. The message goes out with the key of the row (<see cref="ScheduledMessageRules.SendKeyOf"/>), so even a claim that
/// lapsed (a process that died) cannot write the message twice: the message layer finds the one that exists. A row that already has its message is only marked sent, never sent again.</item>
/// <item><b>Order of what is checked.</b> The booking (a stay that does not happen: cancelled); the routine and its action (inactive: cancelled); its condition (cancelled); the marketing consent of a campaign
/// (<c>isCampaign</c>: skipped, <see cref="CampaignConsent"/>, ADR-022-R11: the engine reads it when the routine fires, the sweep sends what the routine did not); the text (the host's edit, else the template in
/// the language of the guest, Italian when there is none); the variables the booking has no value for (blocked); the way to the guest (the rules of the channel and a missing way: skipped by the system); a
/// message that is already there (only the row is brought up to date); the rules of the check-in link (<c>GuestCheckInAccessRules</c>, <c>VerificationHandOff</c>: a check-in that is done or with an operator skips,
/// a guest filling it in waits); the access code TAKEN through the gate of the verification of the guest (<see cref="IAccessCodeService.GetReleasableCodeAsync"/>, ADR-022-R11: nothing is logged); the text tried
/// with the rules of the message layer and a stand-in for the link. Only then the things that have a side effect, last, right before the send: the check-in link is issued (the previous one stops working)
/// and, once the message layer has the message, the release of the code is logged (<see cref="IAccessCodeService.RecordReleaseAsync"/>). Nothing is issued or logged for a message that is not going out.</item>
/// <item><b>What the message layer answers is the state of the row.</b> Only a message the channel took is <c>Sent</c>. A refusal of the rules is a skip of the system with an alert; a message the channel did not take, a
/// channel that could not be reached, one the channel manager holds without having taken it (<c>message_unconfirmed</c>) and one whose outcome nobody knows (<c>manual_verify</c>: it may have left) are <c>Failed</c>
/// with their code, and the sweep tries again: a refused message with the key of the next attempt, a held one with the same key, so the message layer finds it and never writes a second. A failure before the channel
/// was reached (the way to the guest could not be read) counts no try. An unexpected failure (not an answer of the channel) is recorded on the row and thrown, so whoever can try again does.</item>
/// <item><b>The host.</b> A blocked message raises ONE «To do» for the booking (shared by its blocked messages, closed when none is left) and alerts the people who reach the property, once; a skip of the system
/// and a message that went out outside the send window alert them too (<see cref="ScheduledMessageAlertDelivery"/>). Nothing is said of the words of the message.</item>
/// <item><b>Logs</b> carry the row, the org, the state and a code: never a word of a message, a link, a code or an address.</item>
/// </list>
/// </remarks>
public sealed class ScheduledMessageDispatcher(
    AppDbContext db,
    IMessageReplyService replies,
    IAccessCodeService accessCodes,
    IGuestCheckInService checkIn,
    IInternalActivityService activities,
    ScheduledMessageAlertDelivery alerts,
    IConditionEvaluator conditions,
    PublicSiteLinks links,
    ReviewLinkTokens reviewTokens,
    IOptions<ScheduledMessageOptions> options,
    IFeatureFlags featureFlags,
    ILogger<ScheduledMessageDispatcher> logger,
    ICheckInLinkDeliveryService? linkDeliveries = null) : IScheduledMessageDispatcher
{
    /// <summary>The code recorded on a row whose send failed in a way nobody expected (the database, a call that is not an answer of the channel).</summary>
    internal const string UnexpectedErrorCode = "scheduled_message_error";

    /// <summary>The code of a message whose template is gone or off, with no text of the host to replace it.</summary>
    internal const string TemplateMissingCode = "template_missing";

    /// <summary>The code of a text the renderer refuses (empty, too long, a name that is not a variable).</summary>
    internal const string TextInvalidCode = "message_text_invalid";

    /// <summary>The code of a message whose outcome nobody knows: it may have left. It is never written again (the key of the row finds it); the row waits, tried again, for the message layer to know.</summary>
    internal const string ManualVerifyCode = "manual_verify";

    /// <summary>The code of a message the channel manager holds and has not accepted (created, waiting for the authorization of the channel): not a message that went out. Same as the routine's <c>routine_message_unconfirmed</c>.</summary>
    internal const string UnconfirmedCode = "message_unconfirmed";

    /// <summary>A message planned for a moment that passed more than this long ago is not sent late by the sweep: it expires.</summary>
    internal static readonly TimeSpan MaxLateness = TimeSpan.FromHours(24);

    private const string Tenant = AppDbContext.TenantQueryFilter;

    // The messages are the work of the routines: with the module of the routines closed nothing is sent, whatever the flag of the messages says.
    private bool Enabled => featureFlags.IsEnabled(FeatureFlags.ScheduledMessages) && featureFlags.IsEnabled(FeatureFlags.Routines);

    public async Task<ScheduledMessageDispatchResult> TryDispatchAsync(
        Guid id,
        DateTime nowUtc,
        ScheduledMessageDispatchMode mode,
        string? userId,
        CancellationToken ct = default)
    {
        if (!Enabled)
            return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.NothingToDo, ScheduledMessageStatus.Cancelled, null);

        var row = await db.ScheduledMessages.IgnoreQueryFilters([Tenant]).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (row is null)
            return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.NothingToDo, ScheduledMessageStatus.Cancelled, ScheduledMessageErrors.NotFound);

        if (!row.IsOpen)
            return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.NothingToDo, row.Status, null);

        // Nothing goes out before its time but by the hand of the host: the routine fires at the moment of the plan (a message planned for later, the send window, is left to the sweep), and a blocked or failed
        // message that is tried again waits for its time too (the booking moved to later after it was blocked).
        if (mode != ScheduledMessageDispatchMode.SendNow && nowUtc < row.PlannedAtUtc)
            return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.NotDue, row.Status, null);

        // A send under way holds the row: nobody sends it a second time while the lease lasts.
        if (row.LastCheckedAt is { } held && nowUtc - held < ScheduledMessageRules.ClaimLease)
            return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.InProgress, row.Status, null);

        // The claim counts nothing: what is counted is the tries that reached the channel (Attempts), so a message that waited blocked for days has all its tries left when it goes out.
        row.LastCheckedAt = nowUtc;
        row.UpdatedAt = nowUtc;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another actor changed the row between the read and the claim: it won, this one does nothing.
            db.ChangeTracker.Clear();
            logger.LogInformation("Scheduled message {MessageId}: another actor claimed it first, nothing is sent", id);
            return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.InProgress, row.Status, null);
        }

        try
        {
            return await DispatchClaimedAsync(row, nowUtc, mode, userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Scheduled message {MessageId} of org {OrgId}: the send failed in a way nobody expected", id, row.OrgId);
            await RecordUnexpectedFailureAsync(id, nowUtc);
            throw;
        }
    }

    public async Task<int> SweepAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            logger.LogDebug("Scheduled messages are off: the sweep does nothing");
            return 0;
        }

        var settings = options.Value;
        var due = nowUtc - TimeSpan.FromMinutes(settings.EffectiveCatchUpMinutes);
        var retryBefore = nowUtc - TimeSpan.FromMinutes(settings.EffectiveBlockedRetryMinutes);
        var leaseBefore = nowUtc - ScheduledMessageRules.ClaimLease;
        var tooLate = nowUtc - MaxLateness;
        var scheduled = ScheduledMessageStatus.Scheduled;
        var blocked = ScheduledMessageStatus.Blocked;
        var failed = ScheduledMessageStatus.Failed;
        var maxAttempts = ScheduledMessageRules.MaxSendAttempts;

        var ended = await ExpireAsync(nowUtc, tooLate, ct);

        var ids = await db.ScheduledMessages
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(m => (m.Status == scheduled && m.PlannedAtUtc <= due)
                        || ((m.Status == blocked || (m.Status == failed && m.Attempts < maxAttempts)) && m.PlannedAtUtc <= nowUtc && m.UpdatedAt <= retryBefore))
            .Where(m => m.LastCheckedAt == null || m.LastCheckedAt <= leaseBefore)
            .OrderBy(m => m.PlannedAtUtc)
            .Select(m => m.Id)
            .Take(ScheduledMessageRules.SweepBatchSize)
            .ToListAsync(ct);

        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            // One row at a time: nothing of the previous row stays in the change tracker.
            db.ChangeTracker.Clear();
            try
            {
                await TryDispatchAsync(id, nowUtc, ScheduledMessageDispatchMode.Sweep, null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled message {MessageId} could not be sent by the sweep", id);
                failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        }

        logger.LogInformation("Scheduled messages sweep: {Rows} rows looked at, {Ended} expired", ids.Count, ended);
        failure?.Throw();
        return ids.Count + ended;
    }

    public async Task<ScheduledMessagePreview> PreviewAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.ScheduledMessages.IgnoreQueryFilters([Tenant]).AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (row is not { IsOpen: true } || await LoadFactsAsync(row, ct) is not { } facts)
            return new ScheduledMessagePreview(null, []);

        var language = await GuestLanguageAsync(row, facts, ct);
        if (await BodyOfAsync(row, language, ct) is not { } body)
            return new ScheduledMessagePreview(null, [TemplateMissingCode]);

        var conversation = await MessageConversationLookup.FindAsync(db, new HostScope(row.OrgId), MessageThreadKey.ForDirect(row.BookingId), ct);

        // The secrets show as placeholders (square brackets: the text must not hold a double brace, which the filling of a text takes out): a preview issues no link and releases no code.
        var values = ValuesOf(conversation, facts, language) with
        {
            CheckInLink = "[" + MessageTemplateRenderer.CheckInLinkVariable + "]",
            AccessCode = "[" + MessageTemplateRenderer.AccessCodeVariable + "]",
            ReviewLink = CanMakeReviewLink(facts) ? "[" + MessageTemplateRenderer.ReviewLinkVariable + "]" : null,
        };
        try
        {
            var missing = MessageTemplateRenderer.MissingVariables(body.Text, values, body.Culture);
            var text = MessageTemplateRenderer.FillKnown(body.Text, values, body.Culture, out _);
            return new ScheduledMessagePreview(text.Length == 0 ? null : text, missing);
        }
        catch (DomainRuleException)
        {
            return new ScheduledMessagePreview(null, [TextInvalidCode]);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // One send
    // ------------------------------------------------------------------------------------------------------------------------------------

    private async Task<ScheduledMessageDispatchResult> DispatchClaimedAsync(
        ScheduledMessage row,
        DateTime now,
        ScheduledMessageDispatchMode mode,
        string? userId,
        CancellationToken ct)
    {
        var wasBlocked = row.Status == ScheduledMessageStatus.Blocked;
        var previousReason = row.BlockedReason;

        var facts = await LoadFactsAsync(row, ct);
        if (facts is null || !facts.Happens)
            return await EndAsync(row, ScheduledMessageStatus.Cancelled, ScheduledMessageReasons.BookingCancelled, now, ct);

        var held = await ActionOfAsync(row, ct);
        if (held is null)
            return await EndAsync(row, ScheduledMessageStatus.Cancelled, ScheduledMessageReasons.RoutineInactive, now, ct);

        // The condition of the routine, as the engine reads it when the routine fires: a stay that does not (or no longer does) meet it is not for this message. «Send now» is the host's own decision.
        if (mode != ScheduledMessageDispatchMode.SendNow
            && !await ScheduledMessageConditions.HoldsAsync(conditions, row.OrgId, held.RoutineId, held.ConditionJson, facts.ConditionFacts(), logger, ct))
        {
            return await EndAsync(row, ScheduledMessageStatus.Cancelled, ScheduledMessageReasons.ConditionNotMet, now, ct);
        }

        // A campaign goes only to a guest who gave the marketing consent (ADR-022-R11), read as the engine reads it (CampaignConsent). The engine reads it when the routine fires, but the sweep sends what the
        // routine did not: a row the engine blocked is still planned, so the consent is read here too, whoever asks (the routine, the sweep, «Send now»: a guest who withdrew the consent is not written to).
        // Nothing reached the channel and it is the expected end of a campaign for a guest who said no: no count of a try, no alert.
        if (held.IsCampaign && !await CampaignConsent.IsGrantedAsync(db, row.OrgId, facts.GuestId, ct))
            return await SkipBySystemAsync(row, ScheduledMessageReasons.CampaignNeedsConsent, now, ct, alert: false, counted: false);

        var scope = new HostScope(row.OrgId);
        var language = await GuestLanguageAsync(row, facts, ct);
        var body = await BodyOfAsync(row, language, ct);
        if (body is null)
            return await BlockAsync(row, TemplateMissingCode, wasBlocked, previousReason, now, ct);

        // The values the booking has: the same ones the composer of the hub fills a template with, and the three the plan adds.
        var conversation = await MessageConversationLookup.FindAsync(db, scope, MessageThreadKey.ForDirect(row.BookingId), ct);
        var values = ValuesOf(conversation, facts, language);

        IReadOnlyList<string> used;
        try
        {
            used = MessageTemplateRenderer.VariablesOf(body.Text);
        }
        catch (DomainRuleException)
        {
            return await BlockAsync(row, TextInvalidCode, wasBlocked, previousReason, now, ct);
        }

        var missing = used
            .Where(name => name is not (MessageTemplateRenderer.CheckInLinkVariable or MessageTemplateRenderer.AccessCodeVariable or MessageTemplateRenderer.ReviewLinkVariable))
            .FirstOrDefault(name => string.IsNullOrWhiteSpace(ValueOf(name, values)));
        if (missing is null && used.Contains(MessageTemplateRenderer.ReviewLinkVariable) && !CanMakeReviewLink(facts))
            missing = MessageTemplateRenderer.ReviewLinkVariable;
        if (missing is not null)
            return await BlockAsync(row, ScheduledMessageReasons.VariableMissing(missing), wasBlocked, previousReason, now, ct);

        MessageContactOptions channel;
        try
        {
            channel = await replies.ChooseChannelAsync(scope, row.BookingId, ct);
        }
        catch (Exception ex) when (ex is OctorateException or DomainException or NotFoundException)
        {
            // The way to the guest could not be read (a limit, an outage): nothing reached the channel, so it counts as no try, and it is tried again.
            return await FailAsync(row, CodeOf(ex), now, userId, ct, counted: false);
        }

        if (channel.Channel == MessageContactChannel.None)
            return await SkipBySystemAsync(row, SkipReasonOf(channel.Reason), now, ct);

        OtaThreadFacts? otaThread = null;
        if (channel.Channel == MessageContactChannel.Ota && channel.ThreadKey is { } threadKey)
        {
            otaThread = await OtaThreadOfAsync(row, threadKey, ct);
            if (otaThread is { Kind: OtaThreadKind.Request })
                return await SkipBySystemAsync(row, ScheduledMessageReasons.ChannelRule, now, ct);
        }

        // A message that is already there (a send that went out and was never saved, or one the message layer still holds): the row is brought up to date with what the message layer says of it, and nothing is
        // released, issued or sent again.
        if (await WrittenStateAsync(row, channel, ct) is { } written)
            return await EndOfSendAsync(row, now, channel.ThreadKey, written, mode, userId, facts, ct);

        // What can refuse the message is checked before the first thing that has a side effect (a code released is logged, a link issued ends the session of the guest). The link: the public address,
        // and the check-in of the stay (IssueLinkAsync refuses nothing, so the rules of the other senders of a link are applied here).
        var usesLink = used.Contains(MessageTemplateRenderer.CheckInLinkVariable);
        if (usesLink)
        {
            if (!links.IsConfigured)
                return await BlockAsync(row, ScheduledMessageReasons.VariableMissing(MessageTemplateRenderer.CheckInLinkVariable), wasBlocked, previousReason, now, ct);

            if (await CheckInLinkRefusalAsync(row, facts, now, mode == ScheduledMessageDispatchMode.SendNow, ct) is { } refusal)
            {
                return refusal.Waits
                    ? await BlockAsync(row, refusal.Reason, wasBlocked, previousReason, now, ct)
                    : await SkipBySystemAsync(row, refusal.Reason, now, ct, alert: false, counted: false);
            }
        }

        // The code is taken, not released: nothing is logged until the message that carries it has gone (RecordReleaseAsync, after the send). A message that is refused, blocked on something else or fails leaves no
        // line that says a code reached a guest, and a retry writes none either.
        string? accessCode = null;
        string? codeChannel = null;
        if (used.Contains(MessageTemplateRenderer.AccessCodeVariable))
        {
            codeChannel = ScheduledMessageRules.ReleaseChannelOf(channel.Channel);
            var taken = await TakeAccessCodeAsync(row, codeChannel, ct);
            if (taken.Code is null)
                return await BlockAsync(row, taken.Reason!, wasBlocked, previousReason, now, ct);

            accessCode = taken.Code;
        }

        // The text is tried with the rules of the message layer before the link exists: a stand-in of the length of a real link takes its place. A text that cannot go out never costs the guest the link it holds.
        var reviewLink = used.Contains(MessageTemplateRenderer.ReviewLinkVariable) ? ReviewLinkOf(facts, conversation) : null;
        var standIn = usesLink ? links.GuestCheckIn(new string('0', CheckInTokenLength)) : null;
        var trial = RenderOrReason(body, values with { CheckInLink = standIn, AccessCode = accessCode, ReviewLink = reviewLink }, out var trialReason);
        if (trial is null)
            return await BlockAsync(row, trialReason!, wasBlocked, previousReason, now, ct);

        switch (TextRefusalOf(channel, otaThread, trial))
        {
            case MessageChannelRules.ContactDetailsCode:
                return await SkipBySystemAsync(row, ScheduledMessageReasons.ChannelRule, now, ct);
            case { } invalid:
                return await BlockAsync(row, invalid == MessageChannelRules.TextInvalidCode ? TextInvalidCode : invalid, wasBlocked, previousReason, now, ct);
        }

        // The link is issued now, right before the send: this is the last step that can still stop the message, and the one that replaces the link the guest holds.
        string? checkInLink = null;
        if (usesLink)
        {
            checkInLink = await IssueCheckInLinkAsync(row, ct);
            if (checkInLink is null)
                return await BlockAsync(row, ScheduledMessageReasons.VariableMissing(MessageTemplateRenderer.CheckInLinkVariable), wasBlocked, previousReason, now, ct);
        }

        var text = RenderOrReason(body, values with { CheckInLink = checkInLink, AccessCode = accessCode, ReviewLink = reviewLink }, out var reason);
        if (text is null)
            return await BlockAsync(row, reason!, wasBlocked, previousReason, now, ct);

        return await SendAsync(row, scope, channel, text, codeChannel, now, mode, userId, facts, ct);
    }

    /// <summary>The length of the token of a check-in link (64 hexadecimal characters, <c>GuestCheckInService</c>): the stand-in the text is tried with has the length of a real link.</summary>
    private const int CheckInTokenLength = 64;

    /// <summary>The text of the template filled with <paramref name="values"/>; <c>null</c> with the reason the message is held back when the renderer refuses it (a variable with no value, a text that is not valid).</summary>
    private static string? RenderOrReason(Body body, MessageTemplateValues values, out string? reason)
    {
        try
        {
            reason = null;
            return MessageTemplateRenderer.Render(body.Text, values, body.Culture);
        }
        catch (DomainRuleException ex)
        {
            reason = ex.Code == MessageTemplateRenderer.MissingCode && ex.MessageArgs.Count > 0
                ? ScheduledMessageReasons.VariableMissing(ex.MessageArgs[0]?.ToString() ?? "unknown")
                : TextInvalidCode;
            return null;
        }
    }

    /// <summary>
    /// What the message layer would refuse in <paramref name="text"/> for this way to the guest, answered before anything is issued: a code of <see cref="MessageChannelRules"/> (<see cref="MessageChannelRules.TextInvalidCode"/>,
    /// <see cref="MessageChannelRules.ContactDetailsCode"/> for a portal whose rules do not take a link), or <c>null</c> when it would take it. The same rule, with the same inputs, as <c>MessageReplyService</c>.
    /// </summary>
    private static string? TextRefusalOf(MessageContactOptions channel, OtaThreadFacts? thread, string text)
    {
        if (channel.Channel == MessageContactChannel.Ota && thread is { } ota)
            return MessageChannelRules.CheckOutgoing(ota.Source, ota.Kind, text);

        return GuestMessageRules.NormalizeText(text) is { Length: <= GuestMessageRules.TextMaxLength } ? null : MessageChannelRules.TextInvalidCode;
    }

    private async Task<ScheduledMessageDispatchResult> SendAsync(
        ScheduledMessage row,
        HostScope scope,
        MessageContactOptions channel,
        string text,
        string? codeChannel,
        DateTime now,
        ScheduledMessageDispatchMode mode,
        string? userId,
        BookingFacts facts,
        CancellationToken ct)
    {
        var attempt = await NextKeyAttemptAsync(row, channel, ct);
        MessageReplyResult result;
        try
        {
            result = await replies.StartConversationAsync(scope, row.BookingId, text, ScheduledMessageRules.SendKeyOf(row.Id, attempt), null, MessageOrigin.Automation, ct);
        }
        catch (DomainRuleException ex) when (ex.Code == MessageChannelRules.ContactDetailsCode)
        {
            return await SkipBySystemAsync(row, ScheduledMessageReasons.ChannelRule, now, ct);
        }
        catch (DomainRuleException ex) when (ex.Code == MessageChannelRules.TextInvalidCode)
        {
            return await BlockAsync(row, TextInvalidCode, row.Status == ScheduledMessageStatus.Blocked, row.BlockedReason, now, ct);
        }
        catch (DomainConflictException ex)
        {
            return ex.Code switch
            {
                MessageReplyService.MappingNotConfirmedCode => await SkipBySystemAsync(row, ScheduledMessageReasons.MappingNotConfirmed, now, ct),
                MessageReplyService.ContactInfoUnavailableCode
                    or MessageReplyService.NoContactChannelCode
                    or "thread_closed"
                    or OctorateAuthException.NotConnectedCode => await SkipBySystemAsync(row, ScheduledMessageReasons.ContactUnavailable, now, ct),
                _ => await FailAsync(row, ex.Code, now, userId, ct),
            };
        }
        catch (NotFoundException)
        {
            // The conversation went between the choice of the channel and the send (a new mapping moved it, the guest was erased): there is none now.
            return await SkipBySystemAsync(row, ScheduledMessageReasons.ContactUnavailable, now, ct);
        }
        catch (OctorateException ex) when (ex.Code == OctorateAuthException.NotConnectedCode)
        {
            // The connector is off: there is no way to a guest of a portal now.
            return await SkipBySystemAsync(row, ScheduledMessageReasons.ContactUnavailable, now, ct);
        }
        catch (OctorateException ex)
        {
            // A limit, an outage, a refusal that is not an answer of the message layer: nothing is known to have left, the row failed with its code and the sweep tries again.
            return await FailAsync(row, ex.Code, now, userId, ct);
        }

        if (result.State == OtaMessageDeliveryState.Error)
            return await FailAsync(row, result.ErrorCode ?? "message_not_sent", now, userId, ct);

        // The message is in the hands of the message layer: the code it carries has gone, or may have: this is the moment the release is written (once: a message found again under its key is not a second release).
        if (codeChannel is not null && !result.Replayed)
            await accessCodes.RecordReleaseAsync(row.BookingId, codeChannel, ct);

        return await EndOfSendAsync(row, now, result.ThreadKey, result.State, mode, userId, facts, ct);
    }

    /// <summary>
    /// What the message layer says of the message is the state of the row, as for the routine that writes straight to it (<c>RoutineBookingActions</c>, which counts only <c>Sent</c>): only a message the channel
    /// took is <c>Sent</c>. One whose outcome nobody knows (<see cref="OtaMessageDeliveryState.ManualVerify"/>: it may have left) is <c>Failed</c> with <see cref="ManualVerifyCode"/>, and one the channel manager
    /// holds but has not accepted (<see cref="OtaMessageDeliveryState.Created"/>, <see cref="OtaMessageDeliveryState.WaitingAuthorization"/>) is <c>Failed</c> with <see cref="UnconfirmedCode"/>. The sweep tries
    /// them again with the key of the row, so the message layer finds the message it has and never writes a second: when its state becomes <c>Sent</c> the row follows it, when it is refused the next key sends it
    /// again, and when the tries are used (four) the host is told.
    /// </summary>
    private Task<ScheduledMessageDispatchResult> EndOfSendAsync(
        ScheduledMessage row,
        DateTime now,
        MessageThreadKey? threadKey,
        OtaMessageDeliveryState state,
        ScheduledMessageDispatchMode mode,
        string? userId,
        BookingFacts facts,
        CancellationToken ct) =>
        state switch
        {
            OtaMessageDeliveryState.Sent => SentAsync(row, now, threadKey, mode, userId, facts, ct),
            OtaMessageDeliveryState.ManualVerify => FailAsync(row, ManualVerifyCode, now, userId, ct),
            _ => FailAsync(row, UnconfirmedCode, now, userId, ct),
        };

    // ------------------------------------------------------------------------------------------------------------------------------------
    // How a send ends
    // ------------------------------------------------------------------------------------------------------------------------------------

    private async Task<ScheduledMessageDispatchResult> SentAsync(
        ScheduledMessage row,
        DateTime now,
        MessageThreadKey? threadKey,
        ScheduledMessageDispatchMode mode,
        string? userId,
        BookingFacts facts,
        CancellationToken ct)
    {
        var (from, to) = options.Value.EffectiveSendWindow;
        var outside = !ScheduledMessagePlanner.IsInsideWindow(now, RomeCalendar.ZoneOf(facts.Timezone), from, to);
        var key = threadKey?.ToString();

        // The facts of the send are the row's whatever happened to it meanwhile: the message is out. Its status is another matter: a skip of the host that landed while the message was on its way (the lease of the claim
        // had lapsed, so the host was not held off) is not undone, so the row stays skipped with the time and the place of the send beside it; only a row that is still open becomes sent.
        await SaveFinalAsync(
            row,
            r =>
            {
                if (r.IsOpen)
                {
                    r.Status = ScheduledMessageStatus.Sent;
                    r.BlockedReason = null;
                    r.SkipReason = null;
                    r.ActivityId = null;
                }

                r.Attempts++;
                r.SentAt = now;
                r.SentThreadKey = key is { Length: <= ScheduledMessage.SentThreadKeyMaxLength } ? key : null;
                r.OutsideHours = outside;
                r.LastCheckedAt = null;
                r.UpdatedAt = now;
            },
            onlyIfStillOpen: false,
            ct);

        logger.LogInformation(
            "Scheduled message {MessageId} of org {OrgId} sent through {Channel}{OutsideHours}",
            row.Id,
            row.OrgId,
            threadKey?.Source.ToString() ?? "e-mail",
            outside ? " outside the send window" : string.Empty);

        await CloseTodoAsync(row, ct);
        if (outside)
            await AlertAsync(row, ScheduledMessageAlertKinds.OutsideHours, mode == ScheduledMessageDispatchMode.SendNow ? userId : null, now);

        return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.Sent, row.Status, row.BlockedReason);
    }

    private async Task<ScheduledMessageDispatchResult> BlockAsync(
        ScheduledMessage row,
        string reason,
        bool wasBlocked,
        string? previousReason,
        DateTime now,
        CancellationToken ct)
    {
        var applied = await SaveFinalAsync(
            row,
            r =>
            {
                r.Status = ScheduledMessageStatus.Blocked;
                r.BlockedReason = Cut(reason);
                r.LastCheckedAt = null;
                r.UpdatedAt = now;
            },
            onlyIfStillOpen: true,
            ct);
        if (!applied)
            return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.NothingToDo, row.Status, null);

        logger.LogInformation("Scheduled message {MessageId} of org {OrgId} is blocked: {Reason}", row.Id, row.OrgId, reason);

        // The «To do» and the alert come once for each time the message becomes blocked (or blocked for another reason), not at every retry of the sweep. A «To do» that could not be raised (the
        // failure was logged, the row says why it is held back) is raised again at the next pass: a blocked message with no «To do» linked never stays so.
        var becameBlocked = !wasBlocked || !string.Equals(previousReason, reason, StringComparison.Ordinal);
        if (becameBlocked || row.ActivityId is null)
            await RaiseTodoAsync(row, reason, ct);

        if (becameBlocked)
            await AlertAsync(row, ScheduledMessageAlertKinds.Blocked, null, now);

        return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.Blocked, ScheduledMessageStatus.Blocked, reason);
    }

    /// <summary>
    /// The send did not end: the row is <c>Failed</c> with the code and the sweep tries again. <paramref name="counted"/>: the try reached the channel and counts (four in all, then the host is told); a failure
    /// before the channel was reached (the way to the guest could not be read: a limit, an outage of the gateway) counts nothing, so an outage does not use the tries of every message due while it lasts.
    /// A row that never reached the channel is not sent days late either: the sweep ends it like a planned message that is more than a day overdue.
    /// </summary>
    private async Task<ScheduledMessageDispatchResult> FailAsync(ScheduledMessage row, string code, DateTime now, string? userId, CancellationToken ct, bool counted = true)
    {
        var applied = await SaveFinalAsync(
            row,
            r =>
            {
                r.Status = ScheduledMessageStatus.Failed;
                if (counted)
                    r.Attempts++;

                r.BlockedReason = Cut(code);
                r.LastCheckedAt = null;
                r.UpdatedAt = now;
            },
            onlyIfStillOpen: true,
            ct);
        if (!applied)
            return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.NothingToDo, row.Status, null);

        logger.LogWarning("Scheduled message {MessageId} of org {OrgId} failed ({Code}), attempt {Attempts}", row.Id, row.OrgId, code, row.Attempts);

        // The sweep tries again; the host is told when the tries are used (or asked for it: the person who pressed «Send now» sees the answer itself).
        if (counted && row.Attempts >= ScheduledMessageRules.MaxSendAttempts && userId is null)
        {
            await RaiseTodoAsync(row, code, ct);
            await AlertAsync(row, ScheduledMessageAlertKinds.Failed, null, now);
        }

        return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.Failed, ScheduledMessageStatus.Failed, code);
    }

    /// <summary>
    /// The system decided the message does not go out. <paramref name="alert"/>: the people who reach the property are told, as for a message the channel refused; a skip that is the normal course of a message
    /// (a campaign for a guest who did not consent, a link for a check-in that is done) tells nobody. <paramref name="counted"/>: it counts as a try; a decision made before the channel was reached does not.
    /// </summary>
    private async Task<ScheduledMessageDispatchResult> SkipBySystemAsync(ScheduledMessage row, string reason, DateTime now, CancellationToken ct, bool alert = true, bool counted = true)
    {
        var applied = await SaveFinalAsync(
            row,
            r =>
            {
                r.Status = ScheduledMessageStatus.Skipped;
                if (counted)
                    r.Attempts++;

                r.SkipReason = reason;
                r.SkippedByUserId = null;
                r.SkippedAt = now;
                r.ActivityId = null;
                r.LastCheckedAt = null;
                r.UpdatedAt = now;
            },
            onlyIfStillOpen: true,
            ct);
        if (!applied)
            return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.NothingToDo, row.Status, null);

        logger.LogInformation("Scheduled message {MessageId} of org {OrgId} skipped by the system: {Reason}", row.Id, row.OrgId, reason);

        await CloseTodoAsync(row, ct);
        if (alert)
            await AlertAsync(row, ScheduledMessageAlertKinds.Skipped, null, now);

        return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.Skipped, ScheduledMessageStatus.Skipped, reason);
    }

    private async Task<ScheduledMessageDispatchResult> EndAsync(ScheduledMessage row, ScheduledMessageStatus status, string reason, DateTime now, CancellationToken ct)
    {
        var applied = await SaveFinalAsync(
            row,
            r =>
            {
                r.Status = status;
                r.SkipReason = reason;
                r.ActivityId = null;
                r.LastCheckedAt = null;
                r.UpdatedAt = now;
            },
            onlyIfStillOpen: true,
            ct);
        if (!applied)
            return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.NothingToDo, row.Status, null);

        logger.LogInformation("Scheduled message {MessageId} of org {OrgId} is {Status}: {Reason}", row.Id, row.OrgId, status, reason);
        await CloseTodoAsync(row, ct);
        return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.Cancelled, status, reason);
    }

    /// <summary>
    /// Saves what a send decided, on what the row says NOW and not on what it said at the claim: the row is read again first, and a change somebody made after the claim (the host skipped it when the lease had
    /// lapsed, the booking was cancelled) is seen before the outcome is applied; one that lands between the read and the save fails the save on the <c>xmin</c> and the row is read again. A decision of the system
    /// (<paramref name="onlyIfStillOpen"/>) is written only if the row is still open (a skip of the host is not undone); the outcome of a message that went out is applied to the facts of the row (when, where) in
    /// any case, but its <see cref="ScheduledMessage.Status"/> is only the caller's to change on a row that is still open.
    /// </summary>
    private async Task<bool> SaveFinalAsync(ScheduledMessage row, Action<ScheduledMessage> apply, bool onlyIfStillOpen, CancellationToken ct)
    {
        await db.Entry(row).ReloadAsync(ct);
        if (onlyIfStillOpen && !row.IsOpen)
            return false;

        apply(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await db.Entry(row).ReloadAsync(ct);
            if (onlyIfStillOpen && !row.IsOpen)
                return false;

            apply(row);
            await db.SaveChangesAsync(ct);
            return true;
        }
    }

    /// <summary>
    /// A failure nobody expected (the database, a call that is not an answer of the channel): the row is read again from what the database says and closed as <c>Failed</c> with a code, so the claim does not stay
    /// held and the host sees it; if even this cannot be saved it is logged and the original error goes on.
    /// </summary>
    private async Task RecordUnexpectedFailureAsync(Guid id, DateTime now)
    {
        try
        {
            db.ChangeTracker.Clear();
            var row = await db.ScheduledMessages.IgnoreQueryFilters([Tenant]).FirstOrDefaultAsync(m => m.Id == id, CancellationToken.None);
            if (row is not { IsOpen: true })
                return;

            // It counts as a try, so a failure that keeps coming (a bug, a database that is down) is not tried again by the sweep for ever.
            row.Status = ScheduledMessageStatus.Failed;
            row.Attempts++;
            row.BlockedReason = UnexpectedErrorCode;
            row.LastCheckedAt = null;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception recordingError)
        {
            logger.LogError(recordingError, "Scheduled message {MessageId} could not be marked as failed", id);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The host: the «To do» and the alert
    // ------------------------------------------------------------------------------------------------------------------------------------

    private async Task RaiseTodoAsync(ScheduledMessage row, string reason, CancellationToken ct)
    {
        try
        {
            if (await ScheduledMessageTodos.RaiseAsync(activities, row, reason, logger, ct) is { } activityId && row.ActivityId != activityId)
            {
                row.ActivityId = activityId;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row says why it is held back: the «To do» is the second signal, and the sweep raises it again at the next pass of a row that stays blocked after a change.
            db.ChangeTracker.Clear();
            logger.LogError(ex, "The To do of scheduled message {MessageId} of org {OrgId} could not be raised", row.Id, row.OrgId);
        }
    }

    private async Task CloseTodoAsync(ScheduledMessage row, CancellationToken ct)
    {
        try
        {
            await ScheduledMessageTodos.CloseIfIdleAsync(db, activities, row.OrgId, row.BookingId, row.Id, logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The To do of scheduled message {MessageId} of org {OrgId} could not be closed", row.Id, row.OrgId);
        }
    }

    private async Task AlertAsync(ScheduledMessage row, string kind, string? exceptUserId, DateTime now)
    {
        try
        {
            await alerts.AlertOrRetryAsync(new ScheduledMessageAlert(row.OrgId, row.PropertyId, row.BookingId, row.Id, kind, exceptUserId, now), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The alert about scheduled message {MessageId} ({Kind}) of org {OrgId} could not even be queued", row.Id, kind, row.OrgId);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // What the text needs
    // ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>What the dispatch reads of the booking, its property and its org, in one query in the org of the row.</summary>
    private sealed record BookingFacts(
        BookingStatus Status,
        bool NoShow,
        BookingSource Source,
        string? GuestCountry,
        string? Timezone,
        string Address,
        string City,
        string PostalCode,
        string? SiteProfileJson,
        string OrgSlug,
        Guid PropertyId,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        int NumberOfGuests,
        DateTime CreatedAt,
        Guid GuestId)
    {
        /// <summary>A stay that happens: confirmed, checked in or checked out, not a no-show (the one definition of the time triggers).</summary>
        public bool Happens => !NoShow && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(Status);

        /// <summary>The facts a condition of a routine reads, taken from the booking as the engine takes them.</summary>
        public RoutineConditionFacts ConditionFacts() => RoutineConditionFacts.OfBooking(PropertyId, Source, CheckInDate, CheckOutDate, NumberOfGuests, CreatedAt);
    }

    private Task<BookingFacts?> LoadFactsAsync(ScheduledMessage row, CancellationToken ct) =>
        db.Bookings
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(b => b.Id == row.BookingId && b.OrgId == row.OrgId)
            .Select(b => new BookingFacts(
                b.Status,
                b.NoShowAt != null,
                b.Source,
                b.Guest.Country,
                b.Property.Timezone,
                b.Property.Address,
                b.Property.City,
                b.Property.PostalCode,
                b.Property.SiteProfileJson,
                b.Org.Slug,
                b.PropertyId,
                b.CheckInDate,
                b.CheckOutDate,
                b.NumberOfGuests,
                b.CreatedAt,
                b.GuestId))
            .FirstOrDefaultAsync(ct);

    /// <summary>The routine a row stands for, when it is still active, not in test mode, and still holds a <c>MessageToGuest</c> at the place of the row: its id, its condition and whether the action is a campaign.</summary>
    private sealed record HeldAction(Guid RoutineId, string ConditionJson, bool IsCampaign);

    /// <summary>The routine of the row with its condition, when the routine is still active, not in test mode, and still holds a <c>MessageToGuest</c> at that place; <c>null</c> otherwise.</summary>
    private async Task<HeldAction?> ActionOfAsync(ScheduledMessage row, CancellationToken ct)
    {
        if (row.RoutineId is not { } routineId)
            return null;

        var routine = await db.Routines
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(r => r.Id == routineId && r.OrgId == row.OrgId)
            .Select(r => new { r.Active, r.TestMode, r.ActionsJson, r.ConditionJson })
            .FirstOrDefaultAsync(ct);
        if (routine is not { Active: true, TestMode: false } || !RoutineAction.TryParseList(routine.ActionsJson, out var actions))
            return null;

        return row.ActionIndex >= 0 && row.ActionIndex < actions.Count && actions[row.ActionIndex].Type == RoutineActionType.MessageToGuest
            ? new HeldAction(routineId, routine.ConditionJson, actions[row.ActionIndex].IsOnUnlessFalse(ScheduledMessagePlanner.CampaignMarker))
            : null;
    }

    /// <summary>The language of the guest: the one it chose on the page of the check-in, else the one of its country, else Italian.</summary>
    private async Task<string> GuestLanguageAsync(ScheduledMessage row, BookingFacts facts, CancellationToken ct)
    {
        var chosen = await db.GuestCheckInSessions
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(s => s.BookingId == row.BookingId && s.OrgId == row.OrgId && s.Language != null)
            .OrderByDescending(s => s.ExpiresAt)
            .Select(s => s.Language)
            .FirstOrDefaultAsync(ct);
        return GuestLanguages.Normalize(chosen) ?? GuestLanguages.OfCountry(facts.GuestCountry) ?? "it";
    }

    /// <summary>The text and the culture of its language.</summary>
    private sealed record Body(string Text, CultureInfo Culture);

    /// <summary>
    /// The text of the message: the edit of the host for this booking, else the template the action names in the language of the guest, Italian when it has none, the template itself when it has neither. <c>null</c>
    /// when the template is gone or off and the host wrote nothing.
    /// </summary>
    private async Task<Body?> BodyOfAsync(ScheduledMessage row, string language, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(row.TextOverride))
            return new Body(row.TextOverride, GuestLanguages.CultureOf(language));

        if (!Guid.TryParse(row.TemplateRef, out var templateId))
            return null;

        var named = await db.MessageTemplates
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(t => t.Id == templateId && t.OrgId == row.OrgId && t.IsActive)
            .FirstOrDefaultAsync(ct);
        if (named is null)
            return null;

        var siblings = await db.MessageTemplates
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(t => t.OrgId == row.OrgId && t.IsActive && t.Name == named.Name && (t.Language == language || t.Language == "it"))
            .ToListAsync(ct);
        var chosen = siblings.FirstOrDefault(t => t.Language == language) ?? siblings.FirstOrDefault(t => t.Language == "it") ?? named;
        return new Body(chosen.Body, MessageTemplateService.CultureOf(chosen.Language));
    }

    private static MessageTemplateValues ValuesOf(ConversationFacts conversation, BookingFacts facts, string language) => new(
        conversation.GuestFirstNameForTemplates,
        conversation.PropertyName,
        conversation.BookingCode is null ? null : BookingCodes.Format(conversation.BookingCode),
        conversation.CheckInDate is { } checkIn ? RomeCalendar.DateInRome(checkIn) : null,
        conversation.CheckOutDate is { } checkOut ? RomeCalendar.DateInRome(checkOut) : null,
        conversation.CheckInFrom,
        conversation.CheckOutBy,
        conversation.HostNameForTemplates,
        CheckInMethod: CheckInMethodOf(facts.SiteProfileJson, language),
        PropertyAddress: AddressOf(facts));

    private static string? ValueOf(string name, MessageTemplateValues values) => name switch
    {
        MessageTemplateRenderer.GuestNameVariable => values.GuestName,
        MessageTemplateRenderer.PropertyNameVariable => values.PropertyName,
        MessageTemplateRenderer.BookingCodeVariable => values.BookingCode,
        MessageTemplateRenderer.CheckInDateVariable => values.CheckInDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        MessageTemplateRenderer.CheckOutDateVariable => values.CheckOutDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        MessageTemplateRenderer.CheckInTimeVariable => values.CheckInTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
        MessageTemplateRenderer.CheckOutTimeVariable => values.CheckOutTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
        MessageTemplateRenderer.HostNameVariable => values.HostName,
        MessageTemplateRenderer.CheckInMethodVariable => values.CheckInMethod,
        MessageTemplateRenderer.PropertyAddressVariable => values.PropertyAddress,
        _ => null,
    };

    /// <summary>The check-in method of the property in the language of the guest: the text of that language, else the English one for English, else the Italian one the host always writes.</summary>
    private static string? CheckInMethodOf(string? siteProfileJson, string language)
    {
        var text = SitePropertyProfileJson.Parse(siteProfileJson).CheckInMethod;
        if (text is null)
            return null;

        var chosen = language switch
        {
            "it" => text.It,
            "en" => text.En,
            _ => text.Others is { } others && others.TryGetValue(language, out var other) ? other : null,
        };
        return string.IsNullOrWhiteSpace(chosen) ? text.It : chosen;
    }

    private static string? AddressOf(BookingFacts facts)
    {
        var line = string.Join(", ", new[] { facts.Address, $"{facts.PostalCode} {facts.City}".Trim() }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return line.Length == 0 ? null : line;
    }

    private bool CanMakeReviewLink(BookingFacts facts) => facts.Source == BookingSource.Direct && links.IsConfigured;

    /// <summary>The link where the guest of a direct booking writes the review: the signed link of the review invitation, valid until the end of the review window.</summary>
    private string? ReviewLinkOf(BookingFacts facts, ConversationFacts conversation)
    {
        if (!CanMakeReviewLink(facts) || conversation.BookingId is not { } bookingId || conversation.CheckOutDate is not { } checkOut)
            return null;

        var expiresAt = ReviewRules.WindowEndsAtUtc(checkOut);
        return links.GuestReview(facts.OrgSlug, bookingId, reviewTokens.Create(bookingId, new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc))));
    }

    /// <summary>The result of the release of the access code: the code, or the reason it cannot be given.</summary>
    private sealed record Release(string? Code, string? Reason);

    /// <summary>
    /// Takes the code for the message through the gate of the release (verification of the guest, the stay, the hold of the tourist tax: <see cref="IAccessCodeService.GetReleasableCodeAsync"/>, ADR-022-R11) and
    /// writes nothing: the line of the release log is written after the send (<see cref="IAccessCodeService.RecordReleaseAsync"/>).
    /// </summary>
    private async Task<Release> TakeAccessCodeAsync(ScheduledMessage row, string codeChannel, CancellationToken ct)
    {
        // A property with no code set is a thing the host fills in; a stay whose guest is not verified is a thing the guest does: two different reasons.
        var hasCode = await db.PropertyAccessCodes
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .AnyAsync(c => c.PropertyId == row.PropertyId && c.OrgId == row.OrgId, ct);
        if (!hasCode)
            return new Release(null, ScheduledMessageReasons.VariableMissing(MessageTemplateRenderer.AccessCodeVariable));

        var code = await accessCodes.GetReleasableCodeAsync(row.BookingId, codeChannel, ct);
        if (!string.IsNullOrEmpty(code))
            return new Release(code, null);

        return new Release(null, await accessCodes.GetHoldReasonAsync(row.BookingId, ct) is not null ? ScheduledMessageReasons.AccessCodeHeld : ScheduledMessageReasons.IdNotVerified);
    }

    /// <summary>Why a message that names the check-in link does not go: the reason, and whether it waits (<c>Blocked</c>, tried again) or ends (<c>Skipped</c>).</summary>
    private sealed record CheckInLinkRefusal(string Reason, bool Waits);

    /// <summary>
    /// The rules every automatic sender of a check-in link applies, read before the link is issued (<c>IGuestCheckInService.IssueLinkAsync</c> refuses nothing: it expires every open session of the booking, the
    /// one the guest is filling in included, and starts another). <c>GuestCheckInAccessRules</c>, the rules of the daily job and of the e-mail that confirms the booking: the stay takes a link (confirmed or checked
    /// in, not over) and its check-in is not done (<see cref="ScheduledMessageReasons.CheckInNotOpen"/>, <see cref="ScheduledMessageReasons.CheckInCompleted"/>: the data or the communication were sent);
    /// <c>VerificationHandOff</c>: the verification is not with an operator, because a new session would start it again against the decision of a person (<see cref="ScheduledMessageReasons.CheckInWithOperator"/>);
    /// and the guest is not filling the check-in in right now (<see cref="ScheduledMessageReasons.CheckInInProgress"/>, the only one that waits: the session ends or expires, and «Send now» is the host's
    /// own decision to replace it). <c>null</c> when a link may be issued.
    /// </summary>
    private async Task<CheckInLinkRefusal?> CheckInLinkRefusalAsync(ScheduledMessage row, BookingFacts facts, DateTime now, bool hostAsked, CancellationToken ct)
    {
        var instant = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));

        // The window of days before the arrival is the daily job's own: a host who plans a message earlier chose it, so only «done» and «not applicable» refuse here.
        var access = await GuestCheckInAccessRules.ReadAsync(
            db,
            row.BookingId,
            facts.Status,
            facts.CheckInDate,
            facts.CheckOutDate,
            DateOnly.FromDateTime(RomeCalendar.TodayAt(instant)),
            now,
            sendWindowDays: 0,
            ct);
        switch (access.Status)
        {
            case GuestCheckInAccessStatus.Completed:
                return new CheckInLinkRefusal(ScheduledMessageReasons.CheckInCompleted, false);
            case GuestCheckInAccessStatus.NotApplicable:
                return new CheckInLinkRefusal(ScheduledMessageReasons.CheckInNotOpen, false);
        }

        if (await VerificationHandOff.IsWithOperatorAsync(db, row.BookingId, ct))
            return new CheckInLinkRefusal(ScheduledMessageReasons.CheckInWithOperator, false);

        if (!hostAsked && await db.GuestCheckInSessions
                .IgnoreQueryFilters([Tenant])
                .AsNoTracking()
                .AnyAsync(s => s.BookingId == row.BookingId && s.OrgId == row.OrgId && s.Status == GuestCheckInSessionStatus.InCompilazione && s.ExpiresAt > now, ct))
        {
            return new CheckInLinkRefusal(ScheduledMessageReasons.CheckInInProgress, true);
        }

        return null;
    }

    /// <summary>
    /// Issues the link of the check-in at this moment (the previous one stops working) and ends the delivery of the link the portal chat was carrying, which is replaced. Called last, right before the send, once
    /// every rule that can refuse the message has passed (<see cref="CheckInLinkRefusalAsync"/>, the text tried with a stand-in): never for a message that does not go out. <c>null</c> when no link can be made
    /// (the public address is not set, or the service refuses the booking).
    /// </summary>
    private async Task<string?> IssueCheckInLinkAsync(ScheduledMessage row, CancellationToken ct)
    {
        if (!links.IsConfigured)
            return null;

        IssuedCheckInLink issued;
        try
        {
            issued = await checkIn.IssueLinkAsync(row.BookingId, row.OrgId);
        }
        catch (Exception ex) when (ex is NotFoundException or DomainException)
        {
            logger.LogInformation("Scheduled message {MessageId} of org {OrgId}: no check-in link can be issued ({Code})", row.Id, row.OrgId, CodeOf(ex));
            return null;
        }

        if (linkDeliveries is not null)
        {
            try
            {
                await linkDeliveries.EndAsync(row.OrgId, row.BookingId, CheckInLinkEndReasons.LinkReplaced, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The delivery of the check-in link of booking {BookingId} (org {OrgId}) could not be ended", row.BookingId, row.OrgId);
            }
        }

        return links.GuestCheckIn(issued.Token);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The conversation
    // ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The portal and the kind of a conversation of a channel: what the rules of the channel read (<see cref="MessageChannelRules.CheckOutgoing"/>).</summary>
    private sealed record OtaThreadFacts(OtaChannel Source, OtaThreadKind Kind);

    /// <summary>The conversation of the channel the message would go to, in the org of the row; <c>null</c> when it is not there. A scheduled message is for the conversation of a reservation, never for a request that has no booking behind it.</summary>
    private async Task<OtaThreadFacts?> OtaThreadOfAsync(ScheduledMessage row, MessageThreadKey threadKey, CancellationToken ct) =>
        await db.OtaMessageThreads
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(t => t.Id == threadKey.Id && t.OrgId == row.OrgId)
            .Select(t => new OtaThreadFacts(t.ChannelSource, t.Kind))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// What the message layer says of a message it already holds for this row (a message that did not fail: one refused by the channel has the key of the next attempt), or <c>null</c> when it holds none. The
    /// state is the message layer's own: <see cref="OtaMessageDeliveryState.Sent"/> when the channel took it, <see cref="OtaMessageDeliveryState.ManualVerify"/> when nobody knows whether it left,
    /// <see cref="OtaMessageDeliveryState.Created"/> or <see cref="OtaMessageDeliveryState.WaitingAuthorization"/> when it waits. A message of a direct conversation is in the guest area as soon as it is written: sent.
    /// </summary>
    private async Task<OtaMessageDeliveryState?> WrittenStateAsync(ScheduledMessage row, MessageContactOptions channel, CancellationToken ct)
    {
        var prefix = ScheduledMessageRules.SendKeyOf(row.Id);
        if (channel.Channel == MessageContactChannel.Ota && channel.ThreadKey is { } ota)
        {
            var states = await db.OtaMessages
                .IgnoreQueryFilters([Tenant])
                .AsNoTracking()
                .Where(
                    m => m.OrgId == row.OrgId
                         && m.ThreadId == ota.Id
                         && m.IdempotencyKey != null
                         && m.IdempotencyKey.StartsWith(prefix)
                         && m.DeliveryState != OtaMessageDeliveryState.Error)
                .Select(m => m.DeliveryState)
                .ToListAsync(ct);
            if (states.Count == 0)
                return null;

            // More than one is not expected; if there is, what the channel took counts, then what nobody knows, then what waits.
            return states.Contains(OtaMessageDeliveryState.Sent)
                ? OtaMessageDeliveryState.Sent
                : states.Contains(OtaMessageDeliveryState.ManualVerify) ? OtaMessageDeliveryState.ManualVerify : states[0];
        }

        if (channel.Channel == MessageContactChannel.Direct
            && await db.GuestMessages
                .IgnoreQueryFilters([Tenant])
                .AsNoTracking()
                .AnyAsync(m => m.OrgId == row.OrgId && m.Thread.BookingId == row.BookingId && m.IdempotencyKey != null && m.IdempotencyKey.StartsWith(prefix), ct))
        {
            return OtaMessageDeliveryState.Sent;
        }

        return null;
    }

    /// <summary>
    /// The number of the attempt the next send uses: after a message the channel manager refused (<see cref="OtaMessageDeliveryState.Error"/>: it did not go out) the same key would replay the refusal for ever, so the
    /// key of the next attempt is used; any other state of a message of this row is the one to use again (the message layer finds it and sends nothing).
    /// </summary>
    private async Task<int> NextKeyAttemptAsync(ScheduledMessage row, MessageContactOptions channel, CancellationToken ct)
    {
        if (channel.Channel != MessageContactChannel.Ota || channel.ThreadKey is not { } ota)
            return 1;

        var prefix = ScheduledMessageRules.SendKeyOf(row.Id);
        var refused = await db.OtaMessages
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .CountAsync(
                m => m.OrgId == row.OrgId
                     && m.ThreadId == ota.Id
                     && m.IdempotencyKey != null
                     && m.IdempotencyKey.StartsWith(prefix)
                     && m.DeliveryState == OtaMessageDeliveryState.Error,
                ct);
        return refused + 1;
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The sweep: what has passed for good
    // ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What has passed for good ends without a send: a planned message overdue by more than a day (it is never sent days late), and a blocked or failed one whose planned time and whose stay are over by more than a day
    /// (the day after the check-out), with its «To do» closed. Returns how many rows ended.
    /// </summary>
    private async Task<int> ExpireAsync(DateTime nowUtc, DateTime tooLate, CancellationToken ct)
    {
        var scheduled = ScheduledMessageStatus.Scheduled;
        var blocked = ScheduledMessageStatus.Blocked;
        var failed = ScheduledMessageStatus.Failed;
        var checkedOutBefore = RomeCalendar.TodayAt(new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc))).AddDays(-1);

        // A message that never reached the channel (planned, or failed before it: the way to the guest could not be read) is not sent days late; a blocked one, or one the channel had, waits for the check-out.
        var stale = await db.ScheduledMessages
            .IgnoreQueryFilters([Tenant])
            .Where(m => ((m.Status == scheduled || (m.Status == failed && m.Attempts == 0)) && m.PlannedAtUtc <= tooLate)
                        || ((m.Status == blocked || m.Status == failed) && m.PlannedAtUtc <= tooLate && m.Booking.CheckOutDate <= checkedOutBefore))
            .OrderBy(m => m.PlannedAtUtc)
            .Take(ScheduledMessageRules.SweepBatchSize)
            .ToListAsync(ct);

        foreach (var row in stale)
        {
            var neverReached = row.Status == ScheduledMessageStatus.Scheduled || (row.Status == ScheduledMessageStatus.Failed && row.Attempts == 0);
            var reason = neverReached && row.PlannedAtUtc <= tooLate ? ScheduledMessageReasons.Missed : ScheduledMessageReasons.CheckOutReached;
            row.Status = ScheduledMessageStatus.Expired;
            row.SkipReason = reason;
            row.ActivityId = null;
            row.LastCheckedAt = null;
            row.UpdatedAt = nowUtc;
        }

        if (stale.Count == 0)
            return 0;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody changed one of them meanwhile: the next pass looks again.
            db.ChangeTracker.Clear();
            return 0;
        }

        foreach (var row in stale)
            await CloseTodoAsync(row, ct);

        db.ChangeTracker.Clear();
        return stale.Count;
    }

    // ------------------------------------------------------------------------------------------------------------------------------------

    private static string SkipReasonOf(string? reason) => reason switch
    {
        MessageContactOptions.MappingNotConfirmedReason => ScheduledMessageReasons.MappingNotConfirmed,
        _ => ScheduledMessageReasons.ContactUnavailable,
    };

    private static string CodeOf(Exception ex) => ex switch
    {
        OctorateException octorate => octorate.Code,
        DomainException domain => domain.Code,
        NotFoundException notFound => notFound.Code ?? "not_found",
        _ => ex.GetType().Name,
    };

    private static string Cut(string code) => code.Length <= ScheduledMessage.ReasonMaxLength ? code : code[..ScheduledMessage.ReasonMaxLength];
}
