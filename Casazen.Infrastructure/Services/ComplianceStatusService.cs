using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IComplianceStatusService" />
/// <remarks>
/// <para><b>Facts in, light out.</b> This service reads facts (the reports of the stays, the receipts and the proofs of the host, the guests sent to
/// Octorate, the sender of each property, the connection health, the check-in sessions and verifications, the ISTAT and tourist tax months, the
/// rate of the comune, the CIN) and hands them to <see cref="ComplianceLightRules"/>, which alone decides a light. Nothing is stored and nothing
/// is sent: it is a read.</para>
/// <para><b>The reach of the caller.</b> The properties and the stays are read with <c>InScope(scope)</c> and everything else is read for those
/// ids only, so a collaborator «Solo alcuni» gets the lights of its properties and nothing of the others (the proof
/// <c>ComplianceStatus_RespectsHostScope_Test</c>). A stay that is a no-show, cancelled, a request or a hold owes no communication and is not
/// here: the one definition of <see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/> and the no-show mark.</para>
/// <para><b>Modules that are off.</b> The channel manager is a sender only while <c>Features:OctorateConnector</c> is on (otherwise nothing is
/// entrusted to it and nothing is read from it, and the host sends); the identity items exist only while
/// <c>Features:CheckInIdentityVerification</c> is on. An ISTAT item needs the region's deadline in <c>Compliance:IstatDeadlineDays</c>: none is
/// guessed.</para>
/// <para><b>Cost.</b> About a dozen reads, each by the ids of the caller's stays or properties; the only call outside the database is the
/// connection health of the channel-manager properties, which keeps its answers for a few minutes and is bounded per call
/// (<see cref="IComplianceConnectionHealthService.GetManyAsync"/>).</para>
/// </remarks>
public sealed class ComplianceStatusService(
    AppDbContext db,
    IPropertySenderStore senders,
    IComplianceConnectionHealthService health,
    ITouristTaxQuoteService touristTax,
    IFeatureFlags featureFlags,
    IOptions<ComplianceOptions> options,
    ILogger<ComplianceStatusService> logger,
    TimeProvider? timeProvider = null) : IComplianceStatusService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public Task<ComplianceStatusResult> EvaluateAsync(HostScope scope, CancellationToken ct) =>
        EvaluateCoreAsync(scope, null, ct);

    public Task<ComplianceStatusResult> EvaluateAsync(HostScope scope, ComplianceStatusWindow window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(window);
        return EvaluateCoreAsync(scope, window, ct);
    }

    /// <summary>
    /// The evaluation. With no <paramref name="window"/> it is the one of the cockpit (the lookback, the identity of the arriving guests, the
    /// checks of each property). With a window it is the one of the coverage report of the archive: the stays that arrived in the window, the
    /// closed months inside it, the Alloggiati, ISTAT and tourist tax items only, no read of Octorate, and the properties deleted since still
    /// counted (the history of compliance is kept; <c>IgnoreQueryFilters([SoftDelete])</c> keeps the tenant filter).
    /// </summary>
    private async Task<ComplianceStatusResult> EvaluateCoreAsync(HostScope scope, ComplianceStatusWindow? window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var forReport = window is not null;
        var orgId = scope.OrgId;
        var settings = options.Value;
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var today = _clock.TodayInRome();
        var todayDate = _clock.TodayInRomeAsDateOnly();
        var lookbackDays = Math.Max(1, settings.StatusLookbackDays);
        var windowStart = window is null ? today.AddDays(-lookbackDays) : StoredDate(window.From);
        var propertyFilter = window?.PropertyId;
        var connectorOn = featureFlags.IsEnabled(FeatureFlags.OctorateConnector);

        // The properties and the stays of the caller's reach: everything below is read for these ids only.
        var propertyQuery = db.Properties.AsNoTracking();
        if (forReport)
            propertyQuery = propertyQuery.IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter]);
        if (propertyFilter is { } onlyProperty)
            propertyQuery = propertyQuery.Where(p => p.Id == onlyProperty);

        var properties = await propertyQuery
            .Where(PropertyRentalModeRules.IsShortRent)
            .Where(p => p.OrgId == orgId)
            .InScope(scope)
            .Select(p => new PropertyRow(p.Id, p.Name, p.Country, p.City, p.ComuneIstatCode, p.CinCode))
            .ToListAsync(ct);

        // The cockpit looks a week ahead (the arriving guests' identity); a report looks at the stays that have arrived, up to its last day.
        var arrivalHorizon = window is null
            ? today.AddDays(ComplianceLightRules.IdentityLookaheadDays)
            : Earlier(StoredDate(window.To), today);
        var stayQuery = db.Bookings.AsNoTracking();
        if (forReport)
            stayQuery = stayQuery.IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter]);
        if (propertyFilter is { } onlyStayProperty)
            stayQuery = stayQuery.Where(b => b.PropertyId == onlyStayProperty);

        var stays = await stayQuery
            .Where(b => b.OrgId == orgId)
            .InScope(scope)
            .Where(b => AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt == null)
            .Where(b => b.CheckInDate >= windowStart && b.CheckInDate <= arrivalHorizon)
            .Select(b => new StayRow(
                b.Id,
                b.PropertyId,
                b.GuestId,
                b.CheckInDate,
                b.CheckOutDate,
                b.ArrivedAt,
                b.TouristTax,
                (b.Guest.FirstName + " " + b.Guest.LastName).Trim()))
            .ToListAsync(ct);

        var propertyIds = properties.Select(p => p.Id).Concat(stays.Select(s => s.PropertyId)).Distinct().ToList();
        var propertyNames = properties.ToDictionary(p => p.Id, p => p.Name);

        // A report on the past also knows who sent for a property deleted since (its history is kept, like its stays above); the cockpit, which is
        // about the present, reads the senders of the properties that are still there, as it always did.
        var senderOf = propertyIds.Count == 0
            ? new Dictionary<Guid, PropertySenderInfo>()
            : (await (forReport ? senders.GetManyForHistoryAsync(propertyIds, ct) : senders.GetManyAsync(propertyIds, ct)))
                .ToDictionary(pair => pair.Key, pair => pair.Value);

        // Who sends, as far as CasaZen can rely on it: the channel manager only while the connector is on, and nobody outside Italy.
        ComplianceSenderType SenderOf(Guid propertyId)
        {
            if (!senderOf.TryGetValue(propertyId, out var info) || !info.Applies)
                return ComplianceSenderType.Manual;

            return info.AlloggiatiSender == ComplianceSenderType.ChannelManager && !connectorOn
                ? ComplianceSenderType.Manual
                : info.AlloggiatiSender;
        }

        // The state of the send of the channel manager, read once per property (cached, bounded per call). A report reads none: it is about the
        // obligations of the past and must not depend on Octorate answering now.
        var channelManagerProperties = propertyIds.Where(id => SenderOf(id) == ComplianceSenderType.ChannelManager).ToList();
        var connections = channelManagerProperties.Count == 0 || forReport
            ? new Dictionary<Guid, ComplianceConnectionHealth>()
            : (await health.GetManyAsync(channelManagerProperties, ct)).ToDictionary(pair => pair.Key, pair => pair.Value);

        // A property whose send was NOT read in this call (over the limit of live reads, no answer in memory) is neither fine nor in trouble: it gets
        // no connection item and no verdict, and the universe says so, so its To do about the connection is neither raised nor closed on that.
        var connectionNotRead = connections
            .Where(pair => pair.Value.Alloggiati.Reason == ComplianceConnectionHealthRules.NotCheckedYetReason)
            .Select(pair => pair.Key)
            .ToHashSet();
        foreach (var propertyId in connectionNotRead)
            connections.Remove(propertyId);

        // The proofs the host uploaded, for the properties of the caller's reach and the days of the window (a month earlier, for the periods).
        var proofFrom = DateOnly.FromDateTime(windowStart).AddMonths(-1);
        var proofRows = propertyIds.Count == 0
            ? []
            : await db.ManualComplianceProofs
                .AsNoTracking()
                .Where(p => p.OrgId == orgId && propertyIds.Contains(p.PropertyId) && p.Day >= proofFrom)
                .Select(p => new ProofRow(p.Id, p.PropertyId, p.Kind, p.Day, p.UploadedAt))
                .ToListAsync(ct);
        var proofs = proofRows.ToLookup(p => (p.PropertyId, p.Kind));

        var items = new List<ComplianceStatusItem>();
        var universe = new Universe();

        var dueStays = stays.Where(s => s.CheckInDate <= today).ToList();
        var reads = await ReadAlloggiatiAsync(orgId, dueStays, SenderOf, ct);
        AddAlloggiati(items, universe, reads, proofs, dueStays, SenderOf, connections, propertyNames, nowUtc, today, todayDate);

        if (!forReport && featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification))
        {
            var upcoming = stays.Where(s => s.CheckInDate >= today && s.ArrivedAt == null).ToList();
            await AddIdentityAsync(items, universe, upcoming, propertyNames, settings.IdentityRedHoursBeforeArrival, nowUtc, ct);
        }

        var months = window is null
            ? ClosedMonths(todayDate, settings.StatusLookbackDays)
            : ClosedMonthsWithin(todayDate, window.From, window.To);
        await AddPropertyItemsAsync(
            items, universe, properties, stays, proofs, SenderOf, connections, settings, months, propertyChecks: !forReport, todayDate, orgId, ct);

        logger.LogDebug(
            "Compliance status of org {OrgId}: {Items} items over {Stays} stays and {Properties} properties",
            orgId, items.Count, stays.Count, properties.Count);

        return new ComplianceStatusResult(
            items,
            new ComplianceEvaluatedUniverse(
                universe.Bookings, universe.IdentityBookings, universe.Properties, universe.Periods, windowStart, connectionNotRead));
    }

    // ─── Alloggiati Web ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything read about the communications of the stays whose arrival day has come.</summary>
    private sealed record AlloggiatiReads(
        ILookup<Guid, ReportRow> Reports,
        IReadOnlyDictionary<Guid, ReceiptRow> Receipts,
        ILookup<Guid, int> GuestPositions,
        ILookup<Guid, LinkRow> SentLinks);

    private async Task<AlloggiatiReads> ReadAlloggiatiAsync(
        Guid orgId,
        IReadOnlyList<StayRow> dueStays,
        Func<Guid, ComplianceSenderType> senderOf,
        CancellationToken ct)
    {
        var stayIds = dueStays.Select(s => s.Id).ToList();
        var reports = stayIds.Count == 0
            ? []
            : await db.AlloggiatiWebReports
                .AsNoTracking()
                .Where(r => stayIds.Contains(r.BookingId))
                .Select(r => new ReportRow(r.BookingId, r.GuestId, r.Status, r.ReportedAt, r.UpdatedAt, r.ReceiptId))
                .ToListAsync(ct);

        // A receipt CasaZen downloaded itself, live: a simulated one never stands for a real one.
        var receiptIds = reports.Where(r => r.ReceiptId.HasValue).Select(r => r.ReceiptId!.Value).Distinct().ToList();
        var receipts = receiptIds.Count == 0
            ? []
            : await db.AlloggiatiReceipts
                .AsNoTracking()
                .Where(r => r.OrgId == orgId && receiptIds.Contains(r.Id) && r.Mode == AlloggiatiMode.Live)
                .Select(r => new ReceiptRow(r.Id, r.RetrievedAt))
                .ToListAsync(ct);

        // What was entrusted to the channel manager: the guests of a stay that are on Octorate for good (not as a draft).
        var channelManagerStayIds = dueStays.Where(s => senderOf(s.PropertyId) == ComplianceSenderType.ChannelManager).Select(s => s.Id).ToList();
        var guests = channelManagerStayIds.Count == 0
            ? []
            : await db.StayGuests
                .AsNoTracking()
                .Where(g => channelManagerStayIds.Contains(g.BookingId))
                .Select(g => new GuestRow(g.BookingId, g.Position))
                .ToListAsync(ct);
        var links = channelManagerStayIds.Count == 0
            ? []
            : await db.StayGuestOctorateLinks
                .AsNoTracking()
                .Where(l => channelManagerStayIds.Contains(l.BookingId) && l.Status == OctorateSyncStatus.Sent && !l.SentAsDraft)
                .Select(l => new LinkRow(l.BookingId, l.Position, l.SentAt))
                .ToListAsync(ct);

        return new AlloggiatiReads(
            reports.ToLookup(r => r.BookingId),
            receipts.ToDictionary(r => r.Id),
            guests.ToLookup(g => g.BookingId, g => g.Position),
            links.ToLookup(l => l.BookingId));
    }

    private static void AddAlloggiati(
        List<ComplianceStatusItem> items,
        Universe universe,
        AlloggiatiReads reads,
        ILookup<(Guid PropertyId, ManualProofKind Kind), ProofRow> proofs,
        IReadOnlyList<StayRow> dueStays,
        Func<Guid, ComplianceSenderType> senderOf,
        IReadOnlyDictionary<Guid, ComplianceConnectionHealth> connections,
        IReadOnlyDictionary<Guid, string> propertyNames,
        DateTime nowUtc,
        DateTime today,
        DateOnly todayDate)
    {
        foreach (var stay in dueStays)
        {
            universe.Bookings.Add(stay.Id);

            // The report that stands for the stay: the one of its booker, or else the latest.
            var ofStay = reads.Reports[stay.Id].ToList();
            var report = ofStay.FirstOrDefault(r => r.GuestId == stay.GuestId) ?? ofStay.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
            var status = AlloggiatiStatusRules.Effective(report?.Status, stay.CheckInDate, today);
            var sender = senderOf(stay.PropertyId);

            var (entrusted, entrustedAt) = sender == ComplianceSenderType.ChannelManager ? EntrustedState(stay.Id, reads) : (false, null);
            var communicated = status is AlloggiatiWebStatus.Inviato or AlloggiatiWebStatus.InviatoManualmente or AlloggiatiWebStatus.SentReceiptPending;
            DateTime? communicatedAt = communicated ? report?.ReportedAt ?? report?.UpdatedAt : entrustedAt;
            var deadline = AlloggiatiTerms.DeadlineUtc(stay.ArrivedAt, stay.CheckInDate, stay.CheckOutDate);

            var verdict = ComplianceLightRules.Alloggiati(new AlloggiatiFacts
            {
                Sender = sender,
                Status = status,
                StayNights = AlloggiatiTerms.StayDays(stay.CheckInDate, stay.CheckOutDate),
                DeadlineUtc = deadline,
                NowUtc = nowUtc,
                TodayRome = todayDate,
                GuestsEntrusted = entrusted,
                CommunicatedAt = communicatedAt,
                Connection = sender == ComplianceSenderType.ChannelManager && connections.TryGetValue(stay.PropertyId, out var known)
                    ? known.Alloggiati.State
                    : null,
                Proof = AlloggiatiProof(stay, report, reads, proofs),
            });

            items.Add(new ComplianceStatusItem(
                ComplianceItemKind.Alloggiati,
                verdict.Action,
                stay.PropertyId,
                stay.Id,
                null,
                LabelOf(stay, propertyNames),
                verdict.Verdict,
                deadline));
        }
    }

    /// <summary>
    /// The archived proof of the arrival day of a stay: the live receipt a report points to, or else a receipt the host uploaded for that
    /// property and day. Both are real files in the private bucket; neither is a declaration.
    /// </summary>
    private static ComplianceProof? AlloggiatiProof(
        StayRow stay,
        ReportRow? report,
        AlloggiatiReads reads,
        ILookup<(Guid PropertyId, ManualProofKind Kind), ProofRow> proofs)
    {
        if (report?.ReceiptId is { } receiptId && reads.Receipts.TryGetValue(receiptId, out var receipt))
            return new ComplianceProof($"receipt:{receipt.Id:N}", ComplianceEvidenceSources.CasazenReceipt, receipt.RetrievedAt);

        var day = RomeCalendar.DateInRome(stay.CheckInDate);
        var manual = proofs[(stay.PropertyId, ManualProofKind.AlloggiatiReceipt)]
            .Where(p => p.Day == day)
            .OrderByDescending(p => p.UploadedAt)
            .FirstOrDefault();
        return manual is null
            ? null
            : new ComplianceProof($"manual-proof:{manual.Id:N}", ComplianceEvidenceSources.ManualUpload, manual.UploadedAt);
    }

    /// <summary>
    /// Whether every registered guest of the stay is on Octorate for good, and since when (the latest send): a stay with no registered guest
    /// has nothing to entrust.
    /// </summary>
    private static (bool Entrusted, DateTime? At) EntrustedState(Guid bookingId, AlloggiatiReads reads)
    {
        var positions = reads.GuestPositions[bookingId].Distinct().ToList();
        if (positions.Count == 0)
            return (false, null);

        var sentAt = reads.SentLinks[bookingId]
            .GroupBy(l => l.Position)
            .ToDictionary(g => g.Key, g => g.Max(l => l.SentAt));
        return positions.All(sentAt.ContainsKey)
            ? (true, positions.Select(p => sentAt[p]).Max())
            : (false, null);
    }

    // ─── Identity ───────────────────────────────────────────────────────────────────────────────────────

    private async Task AddIdentityAsync(
        List<ComplianceStatusItem> items,
        Universe universe,
        IReadOnlyList<StayRow> upcoming,
        IReadOnlyDictionary<Guid, string> propertyNames,
        int redHoursBeforeArrival,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var bookingIds = upcoming.Select(s => s.Id).ToList();
        foreach (var id in bookingIds)
            universe.IdentityBookings.Add(id);

        if (bookingIds.Count == 0)
            return;

        // The check-in session says whether the stay goes through the online check-in of the document-leaving guest; the audit log of the
        // verification and the decision of the operator say whether it passed (the rule of the access code: accepted, or decided by an operator).
        var sessions = (await db.GuestCheckInSessions
                .AsNoTracking()
                .Where(s => bookingIds.Contains(s.BookingId))
                .Select(s => new SessionRow(s.Id, s.BookingId, s.OperatorRequestedAt, s.CreatedAt))
                .ToListAsync(ct))
            .GroupBy(s => s.BookingId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CreatedAt).First());

        // Which sessions wait for an operator: the definition of the operator queue itself (VerificationOperatorRules), read in the same shape, so the
        // stay the queue lists is the stay painted red here (a request of the guest, a refused consent, a review, the attempts used, a session not expired).
        var waitingForOperator = (await db.GuestCheckInSessions
                .AsNoTracking()
                .Where(s => bookingIds.Contains(s.BookingId))
                .Where(VerificationOperatorRules.NeedsOperator)
                .Select(s => s.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var logsOfBooking = (await db.BiometricVerificationLogs
                .AsNoTracking()
                .Where(l => bookingIds.Contains(l.BookingId))
                .Select(l => new LogRow(l.Id, l.BookingId, l.Outcome, l.Method == VerificationMethod.Operator, l.CreatedAt))
                .ToListAsync(ct))
            .GroupBy(l => l.BookingId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var logs = logsOfBooking.ToDictionary(pair => pair.Key, pair => pair.Value.OrderByDescending(l => l.CreatedAt).First());

        foreach (var stay in upcoming)
        {
            if (!sessions.TryGetValue(stay.Id, out var session))
                continue;

            // The one rule of the gate of the access code (AccessCodeService): the verification passed when the LATEST row of its log is an
            // Accept, written by the agent or by an operator's approval. Who decided only names the source: an operator's rejection (Reject) and
            // the row that asks the guest for a new photo (Escalate) are rows of an operator too, and neither is a pass.
            ComplianceProof? proof = null;
            if (logs.TryGetValue(stay.Id, out var log) && log.Outcome == BiometricOutcome.Accept)
            {
                proof = new ComplianceProof(
                    $"biometric-log:{log.Id:N}",
                    log.ByOperator ? ComplianceEvidenceSources.OperatorDecision : ComplianceEvidenceSources.BiometricLog,
                    log.CreatedAt);
            }

            // An operator is needed when the session waits for one and no row of an operator answered the hand-off since (a decision, or the
            // new photo asked for): the same reading as the queue.
            DateTime? lastOperatorRow = logsOfBooking.TryGetValue(stay.Id, out var rows)
                ? rows.Where(l => l.ByOperator).Select(l => (DateTime?)l.CreatedAt).Max()
                : null;
            var operatorNeeded = proof is null
                                 && waitingForOperator.Contains(session.Id)
                                 && !VerificationOperatorRules.IsDecided(
                                     VerificationOperatorRules.HandOffStartedAt(session.OperatorRequestedAt, session.CreatedAt),
                                     lastOperatorRow);

            var arrivalStart = RomeCalendar.StartOfDayUtc(stay.CheckInDate);
            var verdict = ComplianceLightRules.Identity(new IdentityFacts
            {
                ArrivalStartUtc = arrivalStart,
                NowUtc = nowUtc,
                RedHoursBeforeArrival = redHoursBeforeArrival,
                Proof = proof,
                OperatorDecisionNeeded = operatorNeeded,
            });

            items.Add(new ComplianceStatusItem(
                ComplianceItemKind.Identity,
                ComplianceCockpitAction.VerifyIdentity,
                stay.PropertyId,
                stay.Id,
                null,
                LabelOf(stay, propertyNames),
                verdict,
                arrivalStart));
        }
    }

    // ─── Properties and periods ─────────────────────────────────────────────────────────────────────────

    private async Task AddPropertyItemsAsync(
        List<ComplianceStatusItem> items,
        Universe universe,
        IReadOnlyList<PropertyRow> properties,
        IReadOnlyList<StayRow> stays,
        ILookup<(Guid PropertyId, ManualProofKind Kind), ProofRow> proofs,
        Func<Guid, ComplianceSenderType> senderOf,
        IReadOnlyDictionary<Guid, ComplianceConnectionHealth> connections,
        ComplianceOptions settings,
        IReadOnlyList<DateOnly> months,
        bool propertyChecks,
        DateOnly todayDate,
        Guid orgId,
        CancellationToken ct)
    {
        var scopedIds = properties.Select(p => p.Id).ToList();
        foreach (var id in scopedIds)
            universe.Properties.Add(id);

        foreach (var month in months)
            universe.Periods.Add(ComplianceLightRules.PeriodKey(month));

        if (properties.Count == 0)
            return;

        // The credentials kept in CasaZen: only the outcome of the last verification is read, never the encrypted values. The checks of
        // the present of a property (CIN, connection, credentials, tax rate) are the cockpit's; a coverage report does not make them.
        var verified = !propertyChecks
            ? new Dictionary<Guid, bool?>()
            : (await db.PropertyQuesturaCredentials
                    .AsNoTracking()
                    .Where(c => scopedIds.Contains(c.PropertyId))
                    .Select(c => new { c.PropertyId, c.LastVerificationOk })
                    .ToListAsync(ct))
                .ToDictionary(c => c.PropertyId, c => c.LastVerificationOk);

        var regionOf = settings.IstatDeadlineDays.Count == 0
            ? new Dictionary<string, string?>()
            : await RegionsOfAsync(properties, ct);
        var declaredRates = !propertyChecks
            ? []
            : await db.OrgTouristTaxRates
                .AsNoTracking()
                .Where(r => r.OrgId == orgId && r.EffectiveFrom <= todayDate)
                .ToListAsync(ct);
        var rateKnown = new Dictionary<string, bool>(StringComparer.Ordinal);
        var staysOf = stays.ToLookup(s => s.PropertyId);

        // The payments of the tourist tax the host already recorded with the receipt of the payment (TouristTaxRemittance, NF-100): the existing
        // archive of what was paid and declared for a period of a comune, read as the proof of the month it covers, besides the proofs uploaded here.
        var periodsFrom = months.Count == 0 ? default : months[0];
        var periodsTo = months.Count == 0 ? default : months[^1].AddMonths(1).AddDays(-1);
        var remittances = months.Count == 0
            ? []
            : await db.TouristTaxRemittances
                .AsNoTracking()
                .Where(r => r.OrgId == orgId && r.ReceiptStorageKey != null && r.PeriodFrom <= periodsTo && r.PeriodTo >= periodsFrom)
                .Select(r => new RemittanceRow(r.Id, r.ComuneKey, r.PeriodFrom, r.PeriodTo, r.CreatedAt))
                .ToListAsync(ct);

        foreach (var property in properties)
        {
            var sender = senderOf(property.Id);
            var italian = TouristTaxComune.IsItalianCountry(property.Country);

            if (propertyChecks && italian)
                items.Add(PropertyItem(ComplianceItemKind.Cin, ComplianceCockpitAction.ActivateProperty, property, null, ComplianceLightRules.Cin(property.CinCode), null));

            if (propertyChecks
                && sender == ComplianceSenderType.ChannelManager
                && connections.TryGetValue(property.Id, out var connection)
                && ComplianceLightRules.Connection(new ConnectionFacts
                {
                    State = connection.Alloggiati.State,
                    DeclaredByHost = connection.Alloggiati.DeclaredAt is not null,
                }) is { } connectionVerdict)
            {
                items.Add(PropertyItem(ComplianceItemKind.Connection, ComplianceCockpitAction.ActivateOctorateSend, property, null, connectionVerdict, null));
            }

            if (propertyChecks
                && ComplianceLightRules.Credentials(new CredentialsFacts { LastVerificationOk = verified.GetValueOrDefault(property.Id) }) is { } credentialsVerdict)
                items.Add(PropertyItem(ComplianceItemKind.Credentials, ComplianceCockpitAction.RenewPoliceCredentials, property, null, credentialsVerdict, null));

            var propertyStays = staysOf[property.Id].ToList();
            if (!italian || propertyStays.Count == 0)
                continue;

            // The tourist tax rate of the comune: known from the official table or declared by the org, never presumed zero.
            var comune = new TouristTaxComune(property.ComuneIstatCode, property.City);
            if (propertyChecks && !comune.IsEmpty)
            {
                var comuneKey = $"{comune.IstatCode}|{TouristTaxComune.NormalizeName(comune.Name)}";
                if (!rateKnown.TryGetValue(comuneKey, out var known))
                {
                    var inForce = await touristTax.GetRatesInForceAsync(comune, todayDate, ct);
                    known = TouristTaxCalculator.RateFor(inForce, todayDate) is not null
                            || declaredRates.Any(r => comune.Matches(r.ToTransientRate()));
                    rateKnown[comuneKey] = known;
                }

                if (ComplianceLightRules.MunicipalityRate(known) is { } rateVerdict)
                    items.Add(PropertyItem(ComplianceItemKind.MunicipalityRate, ComplianceCockpitAction.EnterMunicipalityTaxRate, property, null, rateVerdict, null));
            }

            // The ISTAT deadline of the region of the comune; a region that is not configured is «to define» and gets no item.
            int? istatDay = regionOf.TryGetValue(property.ComuneIstatCode ?? string.Empty, out var region)
                            && region is not null
                            && settings.IstatDeadlineDays.TryGetValue(region, out var day)
                ? day
                : null;

            foreach (var month in months)
            {
                var inMonth = propertyStays.Where(s => MonthOf(s.CheckInDate) == month).ToList();
                if (inMonth.Count == 0)
                    continue;

                var period = ComplianceLightRules.PeriodKey(month);
                if (istatDay is { } deadlineDay
                    && ComplianceLightRules.Istat(new IstatFacts
                    {
                        Sender = sender,
                        Today = todayDate,
                        PeriodStart = month,
                        DeadlineDay = deadlineDay,
                        Proof = ProofOf(proofs, property.Id, ManualProofKind.IstatProof, month),
                    }) is { } istatVerdict)
                {
                    items.Add(PropertyItem(
                        ComplianceItemKind.Istat,
                        ComplianceCockpitAction.UploadIstatProof,
                        property,
                        period,
                        istatVerdict,
                        RomeCalendar.StartOfDayUtc(ComplianceLightRules.IstatDeadline(month, deadlineDay))));
                }

                // The tax is owed for the stays the engine priced above zero; a month without any owes nothing (a stay with no amount is not a zero).
                if (inMonth.Any(s => s.TouristTax > 0)
                    && ComplianceLightRules.TouristTaxPeriod(new TaxPeriodFacts
                    {
                        Today = todayDate,
                        PeriodStart = month,
                        // A payment recorded with its receipt declares and pays the period it names: it proves both.
                        Declaration = ProofOf(proofs, property.Id, ManualProofKind.TouristTaxDeclaration, month) ?? RemittanceProof(remittances, property, month),
                        Payment = ProofOf(proofs, property.Id, ManualProofKind.TouristTaxPayment, month) ?? RemittanceProof(remittances, property, month),
                    }) is { } taxVerdict)
                {
                    items.Add(PropertyItem(ComplianceItemKind.TouristTax, ComplianceCockpitAction.DeclareTouristTax, property, period, taxVerdict, null));
                }
            }
        }
    }

    private static ComplianceStatusItem PropertyItem(
        ComplianceItemKind kind,
        ComplianceCockpitAction action,
        PropertyRow property,
        string? period,
        ComplianceVerdict verdict,
        DateTime? dueAt) =>
        new(kind, action, property.Id, null, period, property.Name, verdict, dueAt);

    /// <summary>The latest proof of a kind for the property and the first day of a month (the day a period proof is filed under).</summary>
    private static ComplianceProof? ProofOf(
        ILookup<(Guid PropertyId, ManualProofKind Kind), ProofRow> proofs,
        Guid propertyId,
        ManualProofKind kind,
        DateOnly periodStart)
    {
        var proof = proofs[(propertyId, kind)]
            .Where(p => p.Day == periodStart)
            .OrderByDescending(p => p.UploadedAt)
            .FirstOrDefault();
        return proof is null
            ? null
            : new ComplianceProof($"manual-proof:{proof.Id:N}", ComplianceEvidenceSources.ManualUpload, proof.UploadedAt);
    }

    /// <summary>
    /// The latest payment of the tourist tax the host recorded with a receipt for the comune of the property whose period covers the whole month
    /// (a monthly or a quarterly payment alike): <c>null</c> when there is none. The comune is compared as the tourist tax report compares it
    /// (<see cref="TouristTaxComune.NormalizeName"/>), so a payment typed "forli" meets the stays of "Forlì".
    /// </summary>
    private static ComplianceProof? RemittanceProof(IReadOnlyList<RemittanceRow> remittances, PropertyRow property, DateOnly month)
    {
        var comuneKey = TouristTaxComune.NormalizeName(property.City);
        if (comuneKey.Length == 0)
            return null;

        var lastDay = month.AddMonths(1).AddDays(-1);
        var covering = remittances
            .Where(r => string.Equals(r.ComuneKey, comuneKey, StringComparison.Ordinal) && r.PeriodFrom <= month && r.PeriodTo >= lastDay)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault();
        return covering is null
            ? null
            : new ComplianceProof($"tourist-tax-remittance:{covering.Id:N}", ComplianceEvidenceSources.ManualUpload, covering.CreatedAt);
    }

    /// <summary>
    /// The regions of the properties' comuni, by ISTAT code of the comune (<see cref="ItalianRegions"/> code, <c>LOM</c>); <c>null</c> when the
    /// comune is not in the list. Platform data: the same for every org.
    /// </summary>
    private async Task<Dictionary<string, string?>> RegionsOfAsync(IReadOnlyList<PropertyRow> properties, CancellationToken ct)
    {
        var codes = properties
            .Select(p => p.ComuneIstatCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct()
            .ToList();
        if (codes.Count == 0)
            return new Dictionary<string, string?>();

        return (await db.Comuni
                .AsNoTracking()
                .Where(c => codes.Contains(c.IstatCode))
                .Select(c => new { c.IstatCode, c.RegionIstatCode })
                .ToListAsync(ct))
            .ToDictionary(c => c.IstatCode, c => ItalianRegions.FindByIstatCode(c.RegionIstatCode)?.Code);
    }

    /// <summary>
    /// The closed months the periods are looked at for: from the first full month inside the lookback to the month before today. The month in
    /// progress is not a period yet, and a month cut by the lookback is not looked at (its stays would be partial).
    /// </summary>
    internal static IReadOnlyList<DateOnly> ClosedMonths(DateOnly today, int lookbackDays)
    {
        var from = today.AddDays(-Math.Max(1, lookbackDays));
        var first = from.Day == 1 ? from : new DateOnly(from.Year, from.Month, 1).AddMonths(1);
        var current = new DateOnly(today.Year, today.Month, 1);
        var months = new List<DateOnly>();
        for (var month = first; month < current; month = month.AddMonths(1))
            months.Add(month);

        return months;
    }

    /// <summary>
    /// The closed months a coverage report looks at: the months that lie wholly between <paramref name="from"/> and <paramref name="to"/> and
    /// are over (before the month of <paramref name="today"/>). A month cut by the window is not looked at: its stays would be partial.
    /// </summary>
    internal static IReadOnlyList<DateOnly> ClosedMonthsWithin(DateOnly today, DateOnly from, DateOnly to)
    {
        var first = from.Day == 1 ? from : new DateOnly(from.Year, from.Month, 1).AddMonths(1);
        var current = new DateOnly(today.Year, today.Month, 1);
        var months = new List<DateOnly>();
        for (var month = first; month < current && month.AddMonths(1).AddDays(-1) <= to; month = month.AddMonths(1))
            months.Add(month);

        return months;
    }

    /// <summary>A Europe/Rome day as the stays store it: midnight UTC of that date.</summary>
    private static DateTime StoredDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static DateTime Earlier(DateTime first, DateTime second) => first <= second ? first : second;

    private static DateOnly MonthOf(DateTime checkInDate)
    {
        var date = RomeCalendar.DateInRome(checkInDate);
        return new DateOnly(date.Year, date.Month, 1);
    }

    /// <summary>What the cockpit calls a stay: its guest, as the other items do; the name of the property when the guest has none.</summary>
    private static string LabelOf(StayRow stay, IReadOnlyDictionary<Guid, string> propertyNames) =>
        string.IsNullOrWhiteSpace(stay.GuestName) ? propertyNames.GetValueOrDefault(stay.PropertyId, string.Empty) : stay.GuestName;

    // ─── Rows ───────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Universe
    {
        public HashSet<Guid> Bookings { get; } = [];

        public HashSet<Guid> IdentityBookings { get; } = [];

        public HashSet<Guid> Properties { get; } = [];

        public HashSet<string> Periods { get; } = new(StringComparer.Ordinal);
    }

    private sealed record PropertyRow(Guid Id, string Name, string Country, string City, string? ComuneIstatCode, string? CinCode);

    private sealed record StayRow(
        Guid Id,
        Guid PropertyId,
        Guid GuestId,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        DateTime? ArrivedAt,
        decimal TouristTax,
        string GuestName);

    private sealed record ReportRow(Guid BookingId, Guid GuestId, AlloggiatiWebStatus Status, DateTime? ReportedAt, DateTime UpdatedAt, Guid? ReceiptId);

    private sealed record ReceiptRow(Guid Id, DateTime RetrievedAt);

    private sealed record ProofRow(Guid Id, Guid PropertyId, ManualProofKind Kind, DateOnly Day, DateTime UploadedAt);

    private sealed record RemittanceRow(Guid Id, string ComuneKey, DateOnly PeriodFrom, DateOnly PeriodTo, DateTime CreatedAt);

    private sealed record GuestRow(Guid BookingId, int Position);

    private sealed record LinkRow(Guid BookingId, int Position, DateTime? SentAt);

    private sealed record SessionRow(Guid Id, Guid BookingId, DateTime? OperatorRequestedAt, DateTime CreatedAt);

    private sealed record LogRow(Guid Id, Guid BookingId, BiometricOutcome Outcome, bool ByOperator, DateTime CreatedAt);
}
