using System.Collections.Concurrent;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IComplianceAutopilotService"/>
/// <remarks>
/// <para><b>One source.</b> The items are the ones <see cref="IComplianceWizardService.GetSummaryAsync"/> lists for the caller's scope; the
/// decisions are <see cref="AutopilotDecisions"/>. A run plans again before it acts, so it never acts on what a plan said some minutes
/// ago, and two runs of the same org never overlap (one at a time per org in this process; the cooldown of a stay does the rest
/// when there are several).</para>
/// <para><b>What changes.</b> A link of the check-in is issued (the previous one stops working, as for the host's own «Invia link»), its e-mail
/// is queued with the real outcome kept on the session, and the check-out is the one of <c>POST /check-out</c>
/// (<see cref="IStayLifecycleService.CheckOutAsync"/>, which declares nothing: the property is not ready until the host says so). No
/// guest data is ever written, no communication ever declared sent.</para>
/// <para><b>What is logged.</b> Counts and codes, never a name, an address, a number or a text of a message.</para>
/// </remarks>
public sealed class ComplianceAutopilotService(
    AppDbContext db,
    IComplianceWizardService wizard,
    IAlloggiatiWebService alloggiati,
    IGuestCheckInService checkIn,
    IGuestCheckInLinkEmailQueue linkEmails,
    IStayLifecycleService stayLifecycle,
    IAutopilotMessageWriter writer,
    IAiRuntimeSettings aiSettings,
    PublicSiteLinks links,
    IOptions<EmailOptions> emailOptions,
    ILogger<ComplianceAutopilotService> logger,
    TimeProvider? timeProvider = null) : IComplianceAutopilotService
{
    /// <summary>One run at a time per org: a double click, two tabs, the app and the web.</summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<AutopilotPlan> PlanAsync(HostScope scope, AutopilotCaller caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(caller);

        return (await SnapshotAsync(scope, caller, cancellationToken)).Plan;
    }

    public async Task<AutopilotRunResult> RunAsync(
        HostScope scope,
        AutopilotCaller caller,
        IReadOnlyCollection<string>? keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(caller);

        var gate = Gates.GetOrAdd(scope.OrgId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await RunLockedAsync(scope, caller, keys, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AutopilotRunResult> RunLockedAsync(
        HostScope scope,
        AutopilotCaller caller,
        IReadOnlyCollection<string>? keys,
        CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(scope, caller, cancellationToken);
        var wanted = keys?.ToHashSet(StringComparer.Ordinal);

        var ready = snapshot.Plan.Items
            .Where(i => i.Status == AutopilotItemStatus.Ready && (wanted is null || wanted.Contains(i.Key)))
            .ToList();
        var requests = ready.Where(i => i.Topic == AutopilotTopic.GuestData).Take(AutopilotLimits.MaxGuestRequestsPerRun).ToList();
        var departures = ready.Where(i => i.Topic == AutopilotTopic.Departure).Take(AutopilotLimits.MaxDeparturesPerRun).ToList();
        var beyondLimit = ready.Count - requests.Count - departures.Count;

        var outcomes = new List<AutopilotOutcome>();

        // What the host saw as to do and is no longer: said, not silently dropped.
        if (wanted is not null)
        {
            var done = ready.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            outcomes.AddRange(snapshot.Plan.Items
                .Where(i => wanted.Contains(i.Key) && !done.Contains(i.Key))
                .Select(i => Outcome(i, AutopilotOutcomeStatus.Skipped, AutopilotOutcomeCodes.NoLongerNeeded)));
        }

        // The guests first: their link works only while the stay is open, and nothing here closes a stay that is waiting for them.
        var wording = await WriteChatTextsAsync(scope.OrgId, caller, requests, snapshot, cancellationToken);
        foreach (var item in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(await GuardedAsync(
                item,
                () => AskGuestAsync(item, snapshot.Bookings[item.BookingId], snapshot.History.GetValueOrDefault(item.BookingId, StayHistory.None), wording, cancellationToken)));
        }

        foreach (var item in departures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(await GuardedAsync(item, () => CloseDepartureAsync(item, cancellationToken)));
        }

        logger.LogInformation(
            "Autopilot run of org {OrgId}: {Done} done, {Failed} failed, {Skipped} skipped, {Beyond} beyond the limit",
            scope.OrgId,
            outcomes.Count(o => o.Status == AutopilotOutcomeStatus.Done),
            outcomes.Count(o => o.Status == AutopilotOutcomeStatus.Failed),
            outcomes.Count(o => o.Status == AutopilotOutcomeStatus.Skipped),
            beyondLimit);
        return new AutopilotRunResult(outcomes, wording.AiUsed, wording.AiReason, beyondLimit);
    }

    // ───────────────────────────── the guests ─────────────────────────────

    /// <summary>One call of the AI for all the chat messages of the run (a message per language and reminder), the plain texts when it is not there.</summary>
    private async Task<AutopilotWording> WriteChatTextsAsync(
        Guid orgId,
        AutopilotCaller caller,
        IReadOnlyList<AutopilotItem> requests,
        Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        var variants = requests
            .Where(i => i.Channels.Contains(AutopilotChannels.Chat))
            .Select(i => VariantOf(snapshot.Bookings[i.BookingId], snapshot.History.GetValueOrDefault(i.BookingId, StayHistory.None)))
            .DistinctBy(v => v.Key)
            .ToList();
        return await writer.WriteAsync(orgId, variants, caller.AcquireAiPermit, cancellationToken);
    }

    private static AutopilotChatVariant VariantOf(Booking booking, StayHistory history) =>
        new(AutopilotLanguages.Of(booking.Guest.Country, booking.Guest.Nationality), AutopilotDecisions.ReminderOf(history.Requests));

    private async Task<AutopilotOutcome> AskGuestAsync(
        AutopilotItem item,
        Booking booking,
        StayHistory history,
        AutopilotWording wording,
        CancellationToken cancellationToken)
    {
        // No public address of the web app (only a development host lacks it): no link can be built, and a link issued is a link lost.
        if (!links.IsConfigured)
            return Outcome(item, AutopilotOutcomeStatus.Failed, AutopilotOutcomeCodes.Failed, errorCode: GuestCheckInLinkEmailErrors.LinkUnavailable);

        IssuedCheckInLink link;
        try
        {
            // The link of an earlier request stops working, as for the host's own «Invia link»: only the last one is the guest's.
            link = await checkIn.IssueLinkAsync(booking.Id, booking.OrgId);
        }
        catch (DomainException ex)
        {
            return Outcome(item, AutopilotOutcomeStatus.Failed, AutopilotOutcomeCodes.Failed, errorCode: ex.Code);
        }
        catch (NotFoundException)
        {
            return Outcome(item, AutopilotOutcomeStatus.Skipped, AutopilotOutcomeCodes.NoLongerNeeded);
        }

        string? emailStatus = null;
        string? emailError = null;
        if (item.Channels.Contains(AutopilotChannels.Email))
        {
            var email = await linkEmails.QueueAsync(link.SessionId, link.Token, cancellationToken);
            emailStatus = email.Status.ToString();
            emailError = email.Error;
        }

        var chat = item.Channels.Contains(AutopilotChannels.Chat) ? ChatDraftOf(booking, history, link, wording) : null;
        var queued = emailStatus == nameof(GuestCheckInLinkEmailStatus.Queued);
        var (status, code) = queued
            ? (AutopilotOutcomeStatus.Done, AutopilotOutcomeCodes.EmailQueued)
            : chat is not null
                ? (AutopilotOutcomeStatus.Done, AutopilotOutcomeCodes.ChatReady)
                : (AutopilotOutcomeStatus.Failed, AutopilotOutcomeCodes.EmailFailed);

        return Outcome(item, status, code, emailStatus, emailError, chat);
    }

    private AutopilotChatDraft ChatDraftOf(Booking booking, StayHistory history, IssuedCheckInLink link, AutopilotWording wording)
    {
        var guest = booking.Guest;
        var variant = VariantOf(booking, history);
        var written = wording.Texts.GetValueOrDefault(variant.Key)
            ?? new AutopilotWrittenText(AutopilotChatText.Fallback(variant.Language, variant.Reminder), false);
        var text = AutopilotChatText.Fill(written.Template, guest.FirstName, booking.Property.Name, booking.CheckInDate, links.GuestCheckIn(link.Token));
        var phone = AutopilotContacts.InternationalPhone(guest.PhoneNumber, guest.Country);

        return new AutopilotChatDraft(
            variant.Language,
            text,
            phone is null ? null : AutopilotContacts.WhatsAppUrl(phone, text),
            phone is null ? null : AutopilotContacts.SmsUrl(phone, text),
            written.AiWritten);
    }

    // ───────────────────────────── the departures ─────────────────────────────

    private async Task<AutopilotOutcome> CloseDepartureAsync(AutopilotItem item, CancellationToken cancellationToken)
    {
        try
        {
            // The check-out of POST /check-out: the departure only, nothing declared for the property, the tax or the cleaning.
            await stayLifecycle.CheckOutAsync(item.BookingId, new StayCheckOut(RegisterArrival: false), cancellationToken);
            return Outcome(item, AutopilotOutcomeStatus.Done, AutopilotOutcomeCodes.DepartureClosed);
        }
        catch (DomainConflictException ex) when (ex.Code == BookingErrorCodes.AlreadyCheckedOut)
        {
            return Outcome(item, AutopilotOutcomeStatus.Skipped, AutopilotOutcomeCodes.NoLongerNeeded);
        }
        catch (DomainException ex)
        {
            return Outcome(item, AutopilotOutcomeStatus.Failed, AutopilotOutcomeCodes.Failed, errorCode: ex.Code);
        }
        catch (NotFoundException)
        {
            return Outcome(item, AutopilotOutcomeStatus.Skipped, AutopilotOutcomeCodes.NoLongerNeeded);
        }
    }

    // ───────────────────────────── the plan ─────────────────────────────

    private async Task<Snapshot> SnapshotAsync(HostScope scope, AutopilotCaller caller, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var today = _clock.TodayInRome();
        var aiAvailable = (await aiSettings.GetEffectiveAsync(cancellationToken)).IsExternalProviderActive();
        var emailConfigured = emailOptions.Value.IsConfigured;

        var summary = await wizard.GetSummaryAsync(scope, cancellationToken);
        var dataItems = summary.GuestCheckInsIncomplete.Items;
        var departureItems = summary.CheckoutsDue.Items;
        var alloggiatiItems = summary.AlloggiatiFailures.Items.Select(i => (Item: i, Failed: true))
            .Concat(summary.AlloggiatiManualRequired.Items.Select(i => (Item: i, Failed: false)))
            .ToList();

        var ids = dataItems.Concat(departureItems).Concat(alloggiatiItems.Select(a => a.Item))
            .Select(i => i.BookingId)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return new Snapshot(new AutopilotPlan([], aiAvailable, 0), new Dictionary<Guid, Booking>(), new Dictionary<Guid, StayHistory>());

        var bookings = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Include(b => b.Property)
            .Where(b => b.OrgId == scope.OrgId && ids.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        var history = await HistoryAsync(scope.OrgId, ids, cancellationToken);

        var writable = new Dictionary<Guid, bool>();
        async Task<bool> CanWriteAsync(Guid propertyId)
        {
            if (!writable.TryGetValue(propertyId, out var allowed))
            {
                allowed = await caller.CanWriteProperty(propertyId, cancellationToken);
                writable[propertyId] = allowed;
            }

            return allowed;
        }

        var items = new List<AutopilotItem>();
        var asking = new Dictionary<Guid, AutopilotItem>();

        foreach (var cockpit in dataItems)
        {
            if (!TryBooking(cockpit, bookings, out var booking))
                continue;

            var (status, reason, channels) = AutopilotDecisions.GuestData(
                await CanWriteAsync(booking.PropertyId),
                history.GetValueOrDefault(booking.Id, StayHistory.None).Requests,
                history.GetValueOrDefault(booking.Id, StayHistory.None).LastRequestAt,
                history.GetValueOrDefault(booking.Id, StayHistory.None).Completed,
                booking.Guest,
                emailConfigured,
                now);
            var item = ItemOf(AutopilotTopic.GuestData, status, reason, cockpit, booking, channels);
            items.Add(item);
            asking[booking.Id] = item;
        }

        foreach (var cockpit in departureItems)
        {
            if (!TryBooking(cockpit, bookings, out var booking))
                continue;

            var (status, reason) = AutopilotDecisions.Departure(
                await CanWriteAsync(booking.PropertyId),
                booking.Status,
                booking.CheckOutDate,
                today,
                asking.TryGetValue(booking.Id, out var data) ? data.Status : null);
            items.Add(ItemOf(AutopilotTopic.Departure, status, reason, cockpit, booking, []));
        }

        foreach (var (cockpit, failed) in alloggiatiItems)
        {
            // The stay whose guests are asked for their data is already one item: this would say the same thing twice.
            if (!TryBooking(cockpit, bookings, out var booking) || asking.ContainsKey(booking.Id))
                continue;

            var reason = failed ? AutopilotReasons.Failed : await AlloggiatiReasonAsync(booking.Id, cancellationToken);
            if (reason is not null)
                items.Add(ItemOf(AutopilotTopic.Alloggiati, AutopilotItemStatus.Host, reason, cockpit, booking, []));
        }

        var ordered = items
            .OrderBy(i => i.DueAt ?? DateTime.MaxValue)
            .ThenBy(i => i.Topic)
            .ThenBy(i => i.GuestName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var beyond =
            Math.Max(0, ordered.Count(i => i.Topic == AutopilotTopic.GuestData && i.Status == AutopilotItemStatus.Ready) - AutopilotLimits.MaxGuestRequestsPerRun)
            + Math.Max(0, ordered.Count(i => i.Topic == AutopilotTopic.Departure && i.Status == AutopilotItemStatus.Ready) - AutopilotLimits.MaxDeparturesPerRun);
        return new Snapshot(new AutopilotPlan(ordered, aiAvailable, beyond), bookings, history);
    }

    private static bool TryBooking(ComplianceSummaryItem cockpit, IReadOnlyDictionary<Guid, Booking> bookings, out Booking booking)
    {
        booking = null!;
        return cockpit.BookingId is { } id && bookings.TryGetValue(id, out booking!);
    }

    private static AutopilotItem ItemOf(
        AutopilotTopic topic,
        AutopilotItemStatus status,
        string reason,
        ComplianceSummaryItem cockpit,
        Booking booking,
        IReadOnlyList<string> channels) =>
        new(
            $"{topic}:{booking.Id:N}",
            topic,
            status,
            reason,
            booking.Id,
            booking.PropertyId,
            cockpit.Label,
            booking.Property.Name,
            channels,
            cockpit.DueAt);

    /// <summary>Whether the data of a communication are ready for the portal, from the same summary the Alloggiati tab shows; null when the stay is gone.</summary>
    private async Task<string?> AlloggiatiReasonAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        try
        {
            return AutopilotDecisions.Alloggiati(await alloggiati.GetGuestSummaryAsync(bookingId));
        }
        catch (NotFoundException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>The links issued for each stay: how many, when the last one, and whether the guest has submitted. The sessions of other stays are not read.</summary>
    private async Task<Dictionary<Guid, StayHistory>> HistoryAsync(Guid orgId, IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken)
    {
        var sessions = await db.GuestCheckInSessions
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && bookingIds.Contains(s.BookingId))
            .Select(s => new { s.BookingId, s.CreatedAt, s.Status })
            .ToListAsync(cancellationToken);

        return sessions
            .GroupBy(s => s.BookingId)
            .ToDictionary(
                g => g.Key,
                g => new StayHistory(
                    g.Count(),
                    g.Max(s => s.CreatedAt),
                    g.Any(s => s.Status is GuestCheckInSessionStatus.Completo or GuestCheckInSessionStatus.AlloggiatiInviato)));
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>
    /// One item is its own unit: what goes wrong in it (a rule that refuses, a database error) is its failure, not the run's. A failed
    /// save leaves the change tracker dirty and the next item would save it again, so it is cleared.
    /// </summary>
    private async Task<AutopilotOutcome> GuardedAsync(AutopilotItem item, Func<Task<AutopilotOutcome>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Autopilot item {Topic} failed unexpectedly", item.Topic);
            db.ChangeTracker.Clear();
            return Outcome(item, AutopilotOutcomeStatus.Failed, AutopilotOutcomeCodes.Failed, errorCode: "unexpected");
        }
    }

    private static AutopilotOutcome Outcome(
        AutopilotItem item,
        AutopilotOutcomeStatus status,
        string code,
        string? emailStatus = null,
        string? emailError = null,
        AutopilotChatDraft? chat = null,
        string? errorCode = null) =>
        new(item.Key, item.Topic, item.BookingId, item.GuestName, item.PropertyName, status, code, emailStatus, emailError, chat, errorCode);

    /// <summary>What the plan keeps for a run: the bookings it was made from and how often each stay was asked.</summary>
    private sealed record Snapshot(
        AutopilotPlan Plan,
        IReadOnlyDictionary<Guid, Booking> Bookings,
        IReadOnlyDictionary<Guid, StayHistory> History);

    private sealed record StayHistory(int Requests, DateTime? LastRequestAt, bool Completed)
    {
        public static readonly StayHistory None = new(0, null, false);
    }
}
