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
/// <c>Features:CheckInIdentityVerification</c> is on. An ISTAT item needs the deadline of the region: the monthly day of its row of the ISTAT region
/// table (<c>IstatRegionRules</c>, platform data), or the day set for it in <c>Compliance:IstatDeadlineDays</c>, which overrides the table: none is
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

        var arrivalHorizon = window is null
            ? today.AddDays(ComplianceLightRules.IdentityLookaheadDays)
            : Earlier(StoredDate(window.To), today);
        var stayQuery = db.Bookings.AsNoTracking();
        if (forReport)
            stayQuery = stayQuery.IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter]);
        else
            // The cockpit is about stays the host can open. A report on the past keeps a stay whose property was deleted since.
            stayQuery = stayQuery.Where(OpenableStayRules.CanShow);
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

        var connectionNotRead = connections
            .Where(pair => pair.Value.Alloggiati.Reason == ComplianceConnectionHealthRules.NotCheckedYetReason)
            .Select(pair => pair.Key)
            .ToHashSet();
        foreach (var propertyId in connectionNotRead)
            connections.Remove(propertyId);

        // The proofs the host uploaded, for the properties of the caller's reach and the days of the window (a month earlier, for the periods). A
        // receipt of the Alloggiati communication may cover several flats of the org (E20/F02/US01/T01): there is one row for each flat it covers, so
        // the stay of a flat is proven only by a receipt that names its flat (ManualProofCoverage, the one reading of it).
        var proofFrom = DateOnly.FromDateTime(windowStart).AddMonths(-1);
        var proofRows = await ManualProofCoverage.ReadAsync(db, orgId, propertyIds, proofFrom, null, ct);
        var proofs = proofRows.ToLookup(p => (p.PropertyId, p.Kind));

        var items = new List<ComplianceStatusItem>();
        var universe = new Universe();

        var italianPropertyIds = properties.Where(p => TouristTaxComune.IsItalianCountry(p.Country)).Select(p => p.Id).ToHashSet();
        var dueStays = stays.Where(s => s.CheckInDate <= today && italianPropertyIds.Contains(s.PropertyId)).ToList();
        var reads = await ReadAlloggiatiAsync(orgId, dueStays, SenderOf, ct);
        AddAlloggiati(items, universe, reads, proofs, dueStays, SenderOf, connections, propertyNames, nowUtc, today, todayDate);

        if (!forReport && featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification))
        {
            var upcoming = stays.Where(s => s.CheckInDate >= today && s.ArrivedAt == null).ToList();
            await AddIdentityAsync(items, universe, scope.OrgId, upcoming, propertyNames, settings.IdentityRedHoursBeforeArrival, nowUtc, ct);
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

    private sealed record AlloggiatiReads(
        ILookup<Guid, ReportRow> Reports,
        IReadOnlyDictionary<Guid, ReceiptRow> Receipts,
        ILookup<Guid, int> GuestPositions,
        ILookup<Guid, ChannelManagerGuestFact> Links);

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

        var receiptIds = reports.Where(r => r.ReceiptId.HasValue).Select(r => r.ReceiptId!.Value).Distinct().ToList();
        var receipts = receiptIds.Count == 0
            ? []
            : await db.AlloggiatiReceipts
                .AsNoTracking()
                .Where(r => r.OrgId == orgId && receiptIds.Contains(r.Id) && r.Mode == AlloggiatiMode.Live)
                .Select(r => new ReceiptRow(r.Id, r.RetrievedAt))
                .ToListAsync(ct);

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
                .Where(l => channelManagerStayIds.Contains(l.BookingId))
                .Select(l => new LinkRow(l.BookingId, l.Position, l.Status, l.SentAsDraft, l.ErrorCode, l.ErrorFields, l.Attempts, l.UpdatedAt))
                .ToListAsync(ct);

        return new AlloggiatiReads(
            reports.ToLookup(r => r.BookingId),
            receipts.ToDictionary(r => r.Id),
            guests.ToLookup(g => g.BookingId, g => g.Position),
            links.ToLookup(l => l.BookingId, l => new ChannelManagerGuestFact(l.Position, l.Status, l.SentAsDraft, l.ErrorCode, l.ErrorFields, l.Attempts, l.UpdatedAt)));
    }

    private static void AddAlloggiati(
        List<ComplianceStatusItem> items,
        Universe universe,
        AlloggiatiReads reads,
        ILookup<(Guid PropertyId, ManualProofKind Kind), CoveredProof> proofs,
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

            var sender = senderOf(stay.PropertyId);
            var connection = sender == ComplianceSenderType.ChannelManager && connections.TryGetValue(stay.PropertyId, out var known)
                ? known.Alloggiati.State
                : (ConnectionHealthState?)null;
            var judged = JudgeAlloggiati(stay, reads, proofs, sender, connection, nowUtc, today, todayDate);

            items.Add(new ComplianceStatusItem(
                ComplianceItemKind.Alloggiati,
                judged.Verdict.Action,
                stay.PropertyId,
                stay.Id,
                null,
                LabelOf(stay, propertyNames),
                judged.Verdict.Verdict,
                judged.DeadlineUtc));
        }
    }

    private sealed record AlloggiatiJudgement(
        AlloggiatiWebStatus Status,
        ChannelManagerWriteState ChannelManager,
        ComplianceStayVerdict Verdict,
        DateTime DeadlineUtc,
        ComplianceProof? Proof);

    /// <summary>
    /// The one place the Alloggiati facts of a stay become a status and a light, for the cockpit and for the per-stay status alike (E20/F01/US01/T02): the
    /// report that stands for the stay, its status with the channel manager taken into account (<see cref="AlloggiatiStatusRules.WithChannelManager"/>:
    /// «entrusted» when every guest is on Octorate for good, «write failed» when one could not be written), and the light of
    /// <see cref="ComplianceLightRules.Alloggiati"/>, which is green only with an archived receipt.
    /// </summary>
    private static AlloggiatiJudgement JudgeAlloggiati(
        StayRow stay,
        AlloggiatiReads reads,
        ILookup<(Guid PropertyId, ManualProofKind Kind), CoveredProof> proofs,
        ComplianceSenderType sender,
        ConnectionHealthState? connection,
        DateTime nowUtc,
        DateTime today,
        DateOnly todayDate)
    {
        var ofStay = reads.Reports[stay.Id].ToList();
        var report = ofStay.FirstOrDefault(r => r.GuestId == stay.GuestId) ?? ofStay.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();

        var channelManager = sender == ComplianceSenderType.ChannelManager
            ? AlloggiatiStatusRules.ChannelManagerStateOf(reads.GuestPositions[stay.Id].ToList(), reads.Links[stay.Id].ToList())
            : ChannelManagerWriteState.NotApplicable;
        var status = AlloggiatiStatusRules.WithChannelManager(AlloggiatiStatusRules.Effective(report?.Status, stay.CheckInDate, today), channelManager);
        var communicated = status is AlloggiatiWebStatus.Inviato or AlloggiatiWebStatus.InviatoManualmente or AlloggiatiWebStatus.SentReceiptPending;
        var deadline = AlloggiatiTerms.DeadlineUtc(stay.ArrivedAt, stay.CheckInDate, stay.CheckOutDate);
        var proof = AlloggiatiProof(stay, report, reads, proofs);

        var verdict = ComplianceLightRules.Alloggiati(new AlloggiatiFacts
        {
            Sender = sender,
            Status = status,
            StayNights = AlloggiatiTerms.StayDays(stay.CheckInDate, stay.CheckOutDate),
            DeadlineUtc = deadline,
            NowUtc = nowUtc,
            TodayRome = todayDate,
            GuestsEntrusted = channelManager == ChannelManagerWriteState.Entrusted,
            CommunicatedAt = communicated ? report?.ReportedAt ?? report?.UpdatedAt : null,
            Connection = connection,
            Proof = proof,
        });

        return new AlloggiatiJudgement(status, channelManager, verdict, deadline, proof);
    }

    public async Task<ComplianceStayStatus?> GetStayStatusAsync(HostScope scope, Guid bookingId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var orgId = scope.OrgId;
        var today = _clock.TodayInRome();

        var found = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.Id == bookingId)
            .InScope(scope)
            .Select(b => new
            {
                Stay = new StayRow(
                    b.Id,
                    b.PropertyId,
                    b.GuestId,
                    b.CheckInDate,
                    b.CheckOutDate,
                    b.ArrivedAt,
                    b.TouristTax,
                    (b.Guest.FirstName + " " + b.Guest.LastName).Trim()),
                b.Status,
                b.NoShowAt,
                b.Property.Country,
            })
            .FirstOrDefaultAsync(ct);
        if (found is null)
            return null;

        var stay = found.Stay;
        var info = (await senders.GetManyAsync([stay.PropertyId], ct)).GetValueOrDefault(stay.PropertyId);
        var sender = info is { Applies: true }
            ? info.AlloggiatiSender == ComplianceSenderType.ChannelManager && !featureFlags.IsEnabled(FeatureFlags.OctorateConnector)
                ? ComplianceSenderType.Manual
                : info.AlloggiatiSender
            : ComplianceSenderType.Manual;

        var reads = await ReadAlloggiatiAsync(orgId, [stay], _ => sender, ct);
        var day = RomeCalendar.DateInRome(stay.CheckInDate);
        var proofRows = await ManualProofCoverage.ReadAsync(db, orgId, [stay.PropertyId], day, day, ct);
        var proofs = proofRows.ToLookup(p => (p.PropertyId, p.Kind));

        ConnectionHealthState? connection = null;
        if (sender == ComplianceSenderType.ChannelManager)
        {
            var healthOf = (await health.GetManyAsync([stay.PropertyId], ct)).ToDictionary(pair => pair.Key, pair => pair.Value);
            if (healthOf.TryGetValue(stay.PropertyId, out var known) && known.Alloggiati.Reason != ComplianceConnectionHealthRules.NotCheckedYetReason)
                connection = known.Alloggiati.State;
        }

        var judged = JudgeAlloggiati(stay, reads, proofs, sender, connection, _clock.GetUtcNow().UtcDateTime, today, _clock.TodayInRomeAsDateOnly());

        // A stay that owes nothing (a no-show, a request still waiting, a cancelled booking: the one definition of the Alloggiati job) reads «not due»
        // whatever its guests are on the channel manager, has no light and offers no action (a retry of its write would do nothing: the send path sends
        // nothing for it); a communication already sent stays visible. A stay whose arrival day has not come owes it later: it keeps its status and has
        // no light yet.
        var owes = AlloggiatiStatusRules.ActiveBookingStatuses.Contains(found.Status) && found.NoShowAt is null
            && TouristTaxComune.IsItalianCountry(found.Country);
        var due = owes && stay.CheckInDate <= today;
        var status = !owes && !AlloggiatiStatusRules.IsSent(judged.Status) ? AlloggiatiWebStatus.NotDue : judged.Status;
        var links = reads.Links[stay.Id].OrderBy(l => l.Position).ToList();
        var actions = owes && sender == ComplianceSenderType.ChannelManager && AlloggiatiStatusRules.CanRetryChannelManagerWrite(links)
            ? new[] { AlloggiatiStayActions.RetryOctorateWrite }
            : [];
        return new ComplianceStayStatus(
            stay.Id,
            stay.PropertyId,
            sender,
            status,
            judged.ChannelManager,
            sender == ComplianceSenderType.ChannelManager ? links : [],
            judged.Proof,
            due ? judged.Verdict : null,
            actions);
    }

    public async Task<IReadOnlySet<Guid>> GetStaysAReceiptTurnsGreenAsync(Guid orgId, IReadOnlyCollection<Guid> bookingIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bookingIds);

        var ids = bookingIds.Distinct().ToList();
        var green = new HashSet<Guid>();
        if (ids.Count == 0)
            return green;

        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var today = _clock.TodayInRome();
        var todayDate = _clock.TodayInRomeAsDateOnly();

        var stays = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && ids.Contains(b.Id))
            .Where(AlloggiatiStatusRules.ItalianProperties)
            .Where(b => AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt == null)
            .Select(b => new StayRow(b.Id, b.PropertyId, b.GuestId, b.CheckInDate, b.CheckOutDate, b.ArrivedAt, b.TouristTax, string.Empty))
            .ToListAsync(ct);
        var dueStays = stays.Where(s => s.CheckInDate <= today).ToList();
        if (dueStays.Count == 0)
            return green;

        // Who sends, as the cockpit reads it (the history of a property deleted since is kept) and where the guests stand on the channel manager: the same reads.
        var senderOf = (await senders.GetManyForHistoryAsync(dueStays.Select(s => s.PropertyId).Distinct().ToList(), ct))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var connectorOn = featureFlags.IsEnabled(FeatureFlags.OctorateConnector);
        ComplianceSenderType SenderOf(Guid propertyId) =>
            senderOf.TryGetValue(propertyId, out var info) && info.Applies
                ? info.AlloggiatiSender == ComplianceSenderType.ChannelManager && !connectorOn ? ComplianceSenderType.Manual : info.AlloggiatiSender
                : ComplianceSenderType.Manual;
        var reads = await ReadAlloggiatiAsync(orgId, dueStays, SenderOf, ct);

        var proofs = dueStays
            .Select(s => new CoveredProof(Guid.Empty, s.PropertyId, ManualProofKind.AlloggiatiReceipt, RomeCalendar.DateInRome(s.CheckInDate), nowUtc))
            .ToLookup(p => (p.PropertyId, p.Kind));
        foreach (var stay in dueStays)
        {
            var judged = JudgeAlloggiati(stay, reads, proofs, SenderOf(stay.PropertyId), connection: null, nowUtc, today, todayDate);
            if (judged.Verdict.Verdict.Light == ComplianceLight.Green)
                green.Add(stay.Id);
        }

        return green;
    }

    private static ComplianceProof? AlloggiatiProof(
        StayRow stay,
        ReportRow? report,
        AlloggiatiReads reads,
        ILookup<(Guid PropertyId, ManualProofKind Kind), CoveredProof> proofs)
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

    private async Task AddIdentityAsync(
        List<ComplianceStatusItem> items,
        Universe universe,
        Guid orgId,
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

        var sessions = (await db.GuestCheckInSessions
                .AsNoTracking()
                .Where(s => bookingIds.Contains(s.BookingId))
                .Select(s => s.BookingId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var waitingForOperator = (await db.GuestCheckInSessions.Unanswered(db)
                .Where(s => bookingIds.Contains(s.BookingId))
                .Select(s => s.BookingId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
        var awaitingPhoto = (await VerificationHandOff.AwaitingReplacementPhoto(db, orgId)
                .Where(id => bookingIds.Contains(id)).ToListAsync(ct)).ToHashSet();

        var logs = (await db.BiometricVerificationLogs
                .AsNoTracking()
                .Where(l => bookingIds.Contains(l.BookingId))
                .Select(l => new LogRow(l.Id, l.BookingId, l.Outcome, l.Method == VerificationMethod.Operator, l.CreatedAt))
                .ToListAsync(ct))
            .GroupBy(l => l.BookingId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.CreatedAt).First());

        foreach (var stay in upcoming)
        {
            if (!sessions.Contains(stay.Id))
                continue;

            ComplianceProof? proof = null;
            if (logs.TryGetValue(stay.Id, out var log) && log.Outcome == BiometricOutcome.Accept)
            {
                proof = new ComplianceProof(
                    $"biometric-log:{log.Id:N}",
                    log.ByOperator ? ComplianceEvidenceSources.OperatorDecision : ComplianceEvidenceSources.BiometricLog,
                    log.CreatedAt);
            }

            var arrivalStart = RomeCalendar.StartOfDayUtc(stay.CheckInDate);
            var verdict = ComplianceLightRules.Identity(new IdentityFacts
            {
                ArrivalStartUtc = arrivalStart,
                NowUtc = nowUtc,
                RedHoursBeforeArrival = redHoursBeforeArrival,
                Proof = proof,
                OperatorDecisionNeeded = waitingForOperator.Contains(stay.Id),
                ReplacementPhotoRequested = awaitingPhoto.Contains(stay.Id),
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

    private async Task AddPropertyItemsAsync(
        List<ComplianceStatusItem> items,
        Universe universe,
        IReadOnlyList<PropertyRow> properties,
        IReadOnlyList<StayRow> stays,
        ILookup<(Guid PropertyId, ManualProofKind Kind), CoveredProof> proofs,
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

        // The region of each comune and its row of the ISTAT region table (platform data): the deadline of the region comes from the table, and
        // Compliance:IstatDeadlineDays overrides it. A comune that is not in the list, or a region without a monthly day, has no deadline.
        var territoryOf = await IstatRegionService.FindForComuniAsync(db, properties.Select(p => p.ComuneIstatCode), ct);

        var warnedOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredRates = !propertyChecks
            ? []
            : await db.OrgTouristTaxRates
                .AsNoTracking()
                .Where(r => r.OrgId == orgId && r.EffectiveFrom <= todayDate)
                .ToListAsync(ct);
        var rateKnown = new Dictionary<string, bool>(StringComparer.Ordinal);
        var staysOf = stays.ToLookup(s => s.PropertyId);

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

            // The ISTAT deadline of the region of the comune: the monthly day of its row of the table, or the one configured for the region; a region
            // with neither is «to define» and gets no item.
            var territory = territoryOf.GetValueOrDefault(property.ComuneIstatCode?.Trim() ?? string.Empty);
            var istatDay = IstatRegionResolver.DeadlineDay(
                territory?.Rule,
                territory?.RegionCode,
                settings.IstatDeadlineDays,
                (region, configured) =>
                {
                    if (warnedOverrides.Add(region))
                        logger.LogWarning(IstatRegionService.InvalidOverrideMessage, region, configured);
                });

            var istatSender = IstatRegionResolver.IstatSenderFor(sender, territory?.Rule?.OctorateCoverage ?? IstatCoverage.Unknown);

            foreach (var month in months)
            {
                var inMonth = propertyStays.Where(s => MonthOf(s.CheckInDate) == month).ToList();
                if (inMonth.Count == 0)
                    continue;

                var period = ComplianceLightRules.PeriodKey(month);
                if (istatDay is { } deadlineDay
                    && ComplianceLightRules.Istat(new IstatFacts
                    {
                        Sender = istatSender,
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

                if (inMonth.Any(s => s.TouristTax > 0)
                    && ComplianceLightRules.TouristTaxPeriod(new TaxPeriodFacts
                    {
                        Today = todayDate,
                        PeriodStart = month,
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

    private static ComplianceProof? ProofOf(
        ILookup<(Guid PropertyId, ManualProofKind Kind), CoveredProof> proofs,
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

    internal static IReadOnlyList<DateOnly> ClosedMonthsWithin(DateOnly today, DateOnly from, DateOnly to)
    {
        var first = from.Day == 1 ? from : new DateOnly(from.Year, from.Month, 1).AddMonths(1);
        var current = new DateOnly(today.Year, today.Month, 1);
        var months = new List<DateOnly>();
        for (var month = first; month < current && month.AddMonths(1).AddDays(-1) <= to; month = month.AddMonths(1))
            months.Add(month);

        return months;
    }

    private static DateTime StoredDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static DateTime Earlier(DateTime first, DateTime second) => first <= second ? first : second;

    private static DateOnly MonthOf(DateTime checkInDate)
    {
        var date = RomeCalendar.DateInRome(checkInDate);
        return new DateOnly(date.Year, date.Month, 1);
    }

    private static string LabelOf(StayRow stay, IReadOnlyDictionary<Guid, string> propertyNames) =>
        string.IsNullOrWhiteSpace(stay.GuestName) ? propertyNames.GetValueOrDefault(stay.PropertyId, string.Empty) : stay.GuestName;

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

    private sealed record RemittanceRow(Guid Id, string ComuneKey, DateOnly PeriodFrom, DateOnly PeriodTo, DateTime CreatedAt);

    private sealed record GuestRow(Guid BookingId, int Position);

    private sealed record LinkRow(
        Guid BookingId,
        int Position,
        OctorateSyncStatus Status,
        bool SentAsDraft,
        string? ErrorCode,
        string? ErrorFields,
        int Attempts,
        DateTime UpdatedAt);

    private sealed record LogRow(Guid Id, Guid BookingId, BiometricOutcome Outcome, bool ByOperator, DateTime CreatedAt);
}
