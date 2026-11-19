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
/// <item><b>Order of what is checked.</b> The booking (a stay that does not happen: cancelled); the routine and its action (inactive: cancelled); the text (the host's edit, else the template in the language of the guest,
/// Italian when there is none); the variables the booking has no value for (blocked); the way to the guest (the rules of the channel and a missing way: skipped by the system); only then the two things that have a side
/// effect: the access code is released (<see cref="IAccessCodeService.ReleaseToGuestAsync"/>, never without the verification of the guest, ADR-022-R11) and the check-in link is issued (the previous one stops
/// working), so nothing is released or replaced for a message that is not going out.</item>
/// <item><b>What the channel answers is the state of the row.</b> A refusal of the rules is a skip of the system with an alert; a message the channel did not take, or a channel that could not be reached, is <c>Failed</c>
/// with its code, and the sweep tries again (a refused message with the key of the next attempt); an outcome nobody knows is a message that may have left, so the row is <c>Sent</c> and it is never sent again. An
/// unexpected failure (not an answer of the channel) is recorded on the row and thrown, so whoever can try again does.</item>
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

    /// <summary>The code of a message whose outcome nobody knows: it may have left, it is never sent again.</summary>
    internal const string ManualVerifyCode = "manual_verify";

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
            // The way to the guest could not be read (a limit, an outage): nothing was sent, and it is tried again.
            return await FailAsync(row, CodeOf(ex), now, userId, ct);
        }

        if (channel.Channel == MessageContactChannel.None)
            return await SkipBySystemAsync(row, SkipReasonOf(channel.Reason), now, ct);

        if (channel.Channel == MessageContactChannel.Ota && channel.ThreadKey is { } threadKey && await IsRequestThreadAsync(row, threadKey, ct))
            return await SkipBySystemAsync(row, ScheduledMessageReasons.ChannelRule, now, ct);

        // A message that is already there (a send that went out and was never saved): only the row is brought up to date, nothing is released, issued or sent.
        if (await AlreadyWrittenAsync(row, channel, ct))
            return await SentAsync(row, now, channel.ThreadKey, OtaMessageDeliveryState.Sent, mode, userId, facts, ct);

        string? accessCode = null;
        if (used.Contains(MessageTemplateRenderer.AccessCodeVariable))
        {
            var released = await ReleaseAccessCodeAsync(row, channel, ct);
            if (released.Code is null)
                return await BlockAsync(row, released.Reason!, wasBlocked, previousReason, now, ct);

            accessCode = released.Code;
        }

        string? checkInLink = null;
        if (used.Contains(MessageTemplateRenderer.CheckInLinkVariable))
        {
            checkInLink = await IssueCheckInLinkAsync(row, ct);
            if (checkInLink is null)
                return await BlockAsync(row, ScheduledMessageReasons.VariableMissing(MessageTemplateRenderer.CheckInLinkVariable), wasBlocked, previousReason, now, ct);
        }

        var reviewLink = used.Contains(MessageTemplateRenderer.ReviewLinkVariable) ? ReviewLinkOf(facts, conversation) : null;
        string text;
        try
        {
            text = MessageTemplateRenderer.Render(body.Text, values with { CheckInLink = checkInLink, AccessCode = accessCode, ReviewLink = reviewLink }, body.Culture);
        }
        catch (DomainRuleException ex)
        {
            var reason = ex.Code == MessageTemplateRenderer.MissingCode && ex.MessageArgs.Count > 0
                ? ScheduledMessageReasons.VariableMissing(ex.MessageArgs[0]?.ToString() ?? "unknown")
                : TextInvalidCode;
            return await BlockAsync(row, reason, wasBlocked, previousReason, now, ct);
        }

        return await SendAsync(row, scope, channel, text, now, mode, userId, facts, ct);
    }

    private async Task<ScheduledMessageDispatchResult> SendAsync(
        ScheduledMessage row,
        HostScope scope,
        MessageContactOptions channel,
        string text,
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

        return await SentAsync(row, now, result.ThreadKey, result.State, mode, userId, facts, ct);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // How a send ends
    // ------------------------------------------------------------------------------------------------------------------------------------

    private async Task<ScheduledMessageDispatchResult> SentAsync(
        ScheduledMessage row,
        DateTime now,
        MessageThreadKey? threadKey,
        OtaMessageDeliveryState state,
        ScheduledMessageDispatchMode mode,
        string? userId,
        BookingFacts facts,
        CancellationToken ct)
    {
        var (from, to) = options.Value.EffectiveSendWindow;
        var outside = !ScheduledMessagePlanner.IsInsideWindow(now, RomeCalendar.ZoneOf(facts.Timezone), from, to);
        var key = threadKey?.ToString();

        // The message is out: this outcome is the truth, even over a change somebody made to the row after the claim.
        await SaveFinalAsync(
            row,
            r =>
            {
                r.Status = ScheduledMessageStatus.Sent;
                r.Attempts++;
                r.SentAt = now;
                r.SentThreadKey = key is { Length: <= ScheduledMessage.SentThreadKeyMaxLength } ? key : null;
                r.OutsideHours = outside;
                r.BlockedReason = state == OtaMessageDeliveryState.ManualVerify ? ManualVerifyCode : null;
                r.SkipReason = null;
                r.ActivityId = null;
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

        return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.Sent, ScheduledMessageStatus.Sent, row.BlockedReason);
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

    private async Task<ScheduledMessageDispatchResult> FailAsync(ScheduledMessage row, string code, DateTime now, string? userId, CancellationToken ct)
    {
        var applied = await SaveFinalAsync(
            row,
            r =>
            {
                r.Status = ScheduledMessageStatus.Failed;
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
        if (row.Attempts >= ScheduledMessageRules.MaxSendAttempts && userId is null)
        {
            await RaiseTodoAsync(row, code, ct);
            await AlertAsync(row, ScheduledMessageAlertKinds.Failed, null, now);
        }

        return new ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome.Failed, ScheduledMessageStatus.Failed, code);
    }

    private async Task<ScheduledMessageDispatchResult> SkipBySystemAsync(ScheduledMessage row, string reason, DateTime now, CancellationToken ct)
    {
        var applied = await SaveFinalAsync(
            row,
            r =>
            {
                r.Status = ScheduledMessageStatus.Skipped;
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
    /// Saves what a send decided. A change somebody made to the row after the claim (the host edited or skipped it) makes the save fail on the <c>xmin</c>: the row is read again and the outcome is written
    /// over it when it is what happened (a message that went out), or only if the row is still open when it is a decision of the system (a skip of the host is not undone).
    /// </summary>
    private async Task<bool> SaveFinalAsync(ScheduledMessage row, Action<ScheduledMessage> apply, bool onlyIfStillOpen, CancellationToken ct)
    {
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
        DateTime CreatedAt)
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
                b.CreatedAt))
            .FirstOrDefaultAsync(ct);

    /// <summary>The routine a row stands for, when it is still active, not in test mode, and still holds a <c>MessageToGuest</c> at the place of the row: its id and its condition.</summary>
    private sealed record HeldAction(Guid RoutineId, string ConditionJson);

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
            ? new HeldAction(routineId, routine.ConditionJson)
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

    private async Task<Release> ReleaseAccessCodeAsync(ScheduledMessage row, MessageContactOptions channel, CancellationToken ct)
    {
        // A property with no code set is a thing the host fills in; a stay whose guest is not verified is a thing the guest does: two different reasons.
        var hasCode = await db.PropertyAccessCodes
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .AnyAsync(c => c.PropertyId == row.PropertyId && c.OrgId == row.OrgId, ct);
        if (!hasCode)
            return new Release(null, ScheduledMessageReasons.VariableMissing(MessageTemplateRenderer.AccessCodeVariable));

        var code = await accessCodes.ReleaseToGuestAsync(row.BookingId, ScheduledMessageRules.ReleaseChannelOf(channel.Channel), ct);
        if (!string.IsNullOrEmpty(code))
            return new Release(code, null);

        return new Release(null, await accessCodes.GetHoldReasonAsync(row.BookingId, ct) is not null ? ScheduledMessageReasons.AccessCodeHeld : ScheduledMessageReasons.IdNotVerified);
    }

    /// <summary>
    /// Issues the link of the check-in at this moment (the previous one stops working) and ends the delivery of the link the portal chat was carrying, which is replaced. <c>null</c> when no link can be
    /// made: the public address is not set, or the check-in does not take a link (not open, already completed).
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

    /// <summary>A scheduled message is for the conversation of a reservation, never for a request that has no booking behind it.</summary>
    private async Task<bool> IsRequestThreadAsync(ScheduledMessage row, MessageThreadKey threadKey, CancellationToken ct) =>
        await db.OtaMessageThreads
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .AnyAsync(t => t.Id == threadKey.Id && t.OrgId == row.OrgId && t.Kind == OtaThreadKind.Request, ct);

    /// <summary>The message layer already holds a message written for this row and it did not fail: the row is only to be marked as sent.</summary>
    private async Task<bool> AlreadyWrittenAsync(ScheduledMessage row, MessageContactOptions channel, CancellationToken ct)
    {
        var prefix = ScheduledMessageRules.SendKeyOf(row.Id);
        if (channel.Channel == MessageContactChannel.Ota && channel.ThreadKey is { } ota)
        {
            return await db.OtaMessages
                .IgnoreQueryFilters([Tenant])
                .AsNoTracking()
                .AnyAsync(
                    m => m.OrgId == row.OrgId
                         && m.ThreadId == ota.Id
                         && m.IdempotencyKey != null
                         && m.IdempotencyKey.StartsWith(prefix)
                         && m.DeliveryState != OtaMessageDeliveryState.Error,
                    ct);
        }

        if (channel.Channel == MessageContactChannel.Direct)
        {
            return await db.GuestMessages
                .IgnoreQueryFilters([Tenant])
                .AsNoTracking()
                .AnyAsync(m => m.OrgId == row.OrgId && m.Thread.BookingId == row.BookingId && m.IdempotencyKey != null && m.IdempotencyKey.StartsWith(prefix), ct);
        }

        return false;
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

        var stale = await db.ScheduledMessages
            .IgnoreQueryFilters([Tenant])
            .Where(m => (m.Status == scheduled && m.PlannedAtUtc <= tooLate)
                        || ((m.Status == blocked || m.Status == failed) && m.PlannedAtUtc <= tooLate && m.Booking.CheckOutDate <= checkedOutBefore))
            .OrderBy(m => m.PlannedAtUtc)
            .Take(ScheduledMessageRules.SweepBatchSize)
            .ToListAsync(ct);

        foreach (var row in stale)
        {
            var reason = row.Status == ScheduledMessageStatus.Scheduled ? ScheduledMessageReasons.Missed : ScheduledMessageReasons.CheckOutReached;
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
