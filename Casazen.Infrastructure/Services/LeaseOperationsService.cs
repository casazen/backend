using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services.LeaseDrafts;
using Casazen.Infrastructure.Services.LeaseLetters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The operations of a lease in force (LR-OPS-1, <see cref="ILeaseOperationsService"/>): what the landlord can do about the end of the lease,
/// the letters CasaZen prepares and the record that a letter was sent. Nothing here writes to the tenant and nothing keeps a copy of a letter.
/// </summary>
/// <remarks>
/// <para><b>Reads, never rules of its own.</b> When a notice is on time and where the withdrawal of the tenant ends the lease are
/// <see cref="LeaseOperationRules"/>, which use the figures of <see cref="LongRentRules"/>; the withdrawal itself is
/// <see cref="ILeaseTermService.RecordWithdrawalAsync"/>.</para>
/// <para><b>No personal data in a log or an event:</b> ids, names of kinds, dates and counts. The residences of the parties are read here and
/// go only into the PDF the person downloads.</para>
/// </remarks>
public sealed class LeaseOperationsService(
    AppDbContext db,
    ILeaseLetterCatalog letters,
    IPdfDocumentRenderer pdfRenderer,
    ILogger<LeaseOperationsService> logger,
    TimeProvider? timeProvider = null) : ILeaseOperationsService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<Guid?> FindPropertyIdAsync(Guid leaseId, CancellationToken cancellationToken = default) =>
        await db.LeaseContracts
            .AsNoTracking()
            .Where(l => l.Id == leaseId)
            .Select(l => (Guid?)l.PropertyId)
            .FirstOrDefaultAsync(cancellationToken);

    // ---- what the landlord can do ----------------------------------------------------------------------------------

    public async Task<LeaseOperationsDto> GetAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await db.LeaseContracts
                        .AsNoTracking()
                        .Include(l => l.Events)
                        .FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
                    ?? throw NotFound(leaseId);

        var today = _clock.TodayInRomeAsDateOnly();
        var disdetta = LeaseOperationRules.Disdetta(lease, today);
        var withdrawal = LeaseOperationRules.Withdrawal(lease, today);
        var renewal = LeaseOperationRules.Renewal(lease, today);
        var confirmation = LeaseOperationRules.TransitoryConfirmation(lease, today);
        var istat = LeaseIstatRules.Facts(lease, today);
        var cedolare = LeaseOperationRules.CedolareNotice(lease, today);

        // The lease of the same property that already covers days of the standard renewal: told before the person tries (the server refuses it too).
        LeasePeriodConflictDto? conflict = null;
        if (renewal.Available
            && await LeasePeriodGuard.FindAsync(
                db, lease.PropertyId, lease.Id, ToStoredDate(renewal.StartsOn), ToStoredDate(renewal.StandardEnd), cancellationToken) is { } inTheWay)
        {
            conflict = new LeasePeriodConflictDto(inTheWay.ConflictingLeaseId, inTheWay.ConflictingFrom, inTheWay.ConflictingTo);
        }

        return new LeaseOperationsDto(
            new LeaseDisdettaOperationDto(
                disdetta.Available,
                disdetta.FirstPeriod,
                disdetta.PeriodEnd,
                disdetta.LastDayToGiveNotice,
                disdetta.Late,
                disdetta.Available && disdetta.FirstPeriod ? Enum.GetValues<LeaseDisdettaReason>() : []),
            new LeaseWithdrawalOperationDto(
                withdrawal.Available,
                withdrawal.MinReceivedOn,
                withdrawal.MaxReceivedOn,
                LeaseOperationRules.MaxContractualNoticeMonths,
                RecordedWithdrawal(lease)),
            Enum.GetValues<LeaseLetterKind>()
                .Select(kind => new LeaseLetterStateDto(kind, letters.Get(kind).IsApproved, LastSent(lease, kind)))
                .ToList(),
            new LeaseRenewalOperationDto(
                renewal.Available,
                renewal.OpensOn,
                renewal.StartsOn,
                renewal.StandardEnd,
                renewal.StandardMonths,
                renewal.LatestAgreedEnd,
                conflict,
                RecordedRenewal(lease, RomeCalendar.DateInRome(lease.CurrentTermEndDate))),
            new LeaseTransitoryConfirmationDto(confirmation.Applies, confirmation.Available, confirmation.Holder, confirmation.ConfirmBy),
            new LeaseIstatOperationDto(
                istat.Available, istat.CurrentRent, istat.DefaultSharePercent, istat.MaxSharePercent, istat.MinEffectiveOn, istat.MaxEffectiveOn),
            new LeaseCedolareNoticeDto(cedolare.Available, cedolare.MinEffectiveOn, cedolare.MaxEffectiveOn));
    }

    // ---- the letters -----------------------------------------------------------------------------------------------

    public async Task<LeaseLetterFile> BuildDisdettaAsync(Guid leaseId, LeaseDisdettaReason? reason, CancellationToken cancellationToken = default)
    {
        var lease = await LoadForLetterAsync(leaseId, cancellationToken);
        EnsureInForce(lease);
        if (!LeaseRenewal.Renews(lease.ContractType))
            throw new DomainRuleException(LeaseLifecycleCodes.TypeHasNoRenewal, "LeaseTypeHasNoRenewal");
        if (lease.TerminatedOn is not null)
            throw new DomainConflictException(LeaseLifecycleCodes.AlreadyTerminated, "LeaseAlreadyTerminated");

        // At the first expiry the law lets the landlord deny the renewal only for a reason of its list, and asks the letter to state it; later
        // there is none to state.
        var flags = new HashSet<string>(StringComparer.Ordinal);
        if (LeaseOperationRules.IsFirstPeriod(lease))
        {
            if (reason is not { } chosen || !Enum.IsDefined(chosen))
                throw new DomainRuleException(LeaseLifecycleCodes.DisdettaReasonRequired, "LeaseDisdettaReasonRequired");

            flags.Add(LeaseLetterFlags.FirstPeriod);
            flags.Add(LeaseLetterFlags.Reason(chosen));
        }
        else
        {
            flags.Add(LeaseLetterFlags.LaterPeriod);
        }

        return Build(lease, LeaseLetterKind.Disdetta, flags, LeaseLetterData.Common(lease), $"disdetta-{lease.Id}.pdf");
    }

    public async Task<LeaseLetterFile> BuildPresaAttoRecessoAsync(
        Guid leaseId, DateOnly receivedOn, TenantWithdrawalReason reason, int? noticeMonths, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown reason");

        var months = LeaseOperationRules.WithdrawalNoticeMonths(reason, noticeMonths);
        if (months is < 1 or > LeaseOperationRules.MaxContractualNoticeMonths)
        {
            throw new DomainRuleException(
                LeaseLifecycleCodes.WithdrawalNoticeInvalid, "LeaseWithdrawalNoticeInvalid", LeaseOperationRules.MaxContractualNoticeMonths);
        }

        var lease = await LoadForLetterAsync(leaseId, cancellationToken);
        EnsureInForce(lease);
        if (receivedOn > _clock.TodayInRomeAsDateOnly() || receivedOn < RomeCalendar.DateInRome(lease.StartDate))
            throw new DomainRuleException(LeaseLifecycleCodes.WithdrawalDateInvalid, "LeaseWithdrawalDateInvalid");

        var values = LeaseLetterData.Common(lease);
        values[LeaseLetterPlaceholders.ReceivedOn] = LeaseLetterData.Day(receivedOn);
        values[LeaseLetterPlaceholders.EndsOn] = LeaseLetterData.Day(LeaseOperationRules.WithdrawalEffectiveOn(receivedOn, months));
        values[LeaseLetterPlaceholders.NoticeMonths] = months.ToString(CultureInfo.InvariantCulture);

        return Build(
            lease,
            LeaseLetterKind.PresaAttoRecesso,
            new HashSet<string>(StringComparer.Ordinal) { LeaseLetterFlags.Withdrawal(reason) },
            values,
            $"presa-atto-recesso-{lease.Id}.pdf");
    }

    public async Task<LeaseLetterFile> BuildRinnovoAsync(
        Guid leaseId, DateOnly startsOn, DateOnly endsOn, bool agreed, CancellationToken cancellationToken = default)
    {
        var lease = await LoadForLetterAsync(leaseId, cancellationToken);
        EnsureInForce(lease);
        if (!LeaseRenewal.Renews(lease.ContractType))
            throw new DomainRuleException(LeaseLifecycleCodes.LetterNotApplicable, "LeaseLetterNotApplicable");

        // The days of the period the agreement adds: after the start of the lease, in order and not further than a typo would take them.
        var furthest = startsOn.AddYears(LeaseOperationRules.MaxAgreedExtensionYears);
        if (startsOn < RomeCalendar.DateInRome(lease.StartDate) || endsOn < startsOn || endsOn > furthest)
            throw new DomainRuleException(LeaseLifecycleCodes.RenewalEndInvalid, "LeaseRenewalEndInvalid", startsOn, furthest);

        var values = LeaseLetterData.Common(lease);
        values[LeaseLetterPlaceholders.NewPeriodStart] = LeaseLetterData.Day(startsOn);
        values[LeaseLetterPlaceholders.NewPeriodEnd] = LeaseLetterData.Day(endsOn);

        return Build(
            lease,
            LeaseLetterKind.Rinnovo,
            new HashSet<string>(StringComparer.Ordinal) { agreed ? LeaseLetterFlags.RenewalAgreed : LeaseLetterFlags.RenewalStandard },
            values,
            $"rinnovo-{lease.Id}.pdf");
    }

    public async Task<LeaseLetterFile> BuildConfermaEsigenzaAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await LoadForLetterAsync(leaseId, cancellationToken);
        EnsureInForce(lease);

        // Only a transitory lease, and only when the need is the landlord's: when it is the tenant's, the tenant confirms it.
        if (!LeaseOperationRules.TransitoryConfirmation(lease, _clock.TodayInRomeAsDateOnly()).Available)
            throw new DomainRuleException(LeaseLifecycleCodes.LetterNotApplicable, "LeaseLetterNotApplicable");

        return Build(
            lease,
            LeaseLetterKind.ConfermaEsigenza,
            new HashSet<string>(StringComparer.Ordinal),
            LeaseLetterData.Common(lease),
            $"conferma-esigenza-{lease.Id}.pdf");
    }

    public async Task<LeaseIstatCalculationDto> CalculateIstatAsync(
        Guid leaseId, decimal foiPercent, decimal sharePercent, CancellationToken cancellationToken = default)
    {
        var lease = await db.LeaseContracts.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken) ?? throw NotFound(leaseId);
        var calculation = CalculateIstat(lease, foiPercent, sharePercent);
        return new LeaseIstatCalculationDto(
            calculation.CurrentRent, calculation.FoiPercent, calculation.SharePercent, calculation.AppliedPercent, calculation.Increase, calculation.NewRent);
    }

    public async Task<LeaseLetterFile> BuildAggiornamentoIstatAsync(
        Guid leaseId, decimal foiPercent, decimal sharePercent, DateOnly effectiveFrom, CancellationToken cancellationToken = default)
    {
        var lease = await LoadForLetterAsync(leaseId, cancellationToken);
        var calculation = CalculateIstat(lease, foiPercent, sharePercent);

        // An update starts from the day it is asked for, never from one that has passed, and not after the period the lease is in.
        var facts = LeaseIstatRules.Facts(lease, _clock.TodayInRomeAsDateOnly());
        if (effectiveFrom < facts.MinEffectiveOn || effectiveFrom > facts.MaxEffectiveOn)
        {
            throw new DomainRuleException(
                LeaseLifecycleCodes.LetterEffectDateInvalid, "LeaseLetterEffectDateInvalid", facts.MinEffectiveOn, facts.MaxEffectiveOn);
        }

        var values = LeaseLetterData.Common(lease);
        values[LeaseLetterPlaceholders.CurrentRent] = LeaseLetterData.Money(calculation.CurrentRent);
        values[LeaseLetterPlaceholders.FoiPercent] = LeaseLetterData.Percent(calculation.FoiPercent);
        values[LeaseLetterPlaceholders.SharePercent] = LeaseLetterData.Percent(calculation.SharePercent);
        values[LeaseLetterPlaceholders.AppliedPercent] = LeaseLetterData.Percent(calculation.AppliedPercent);
        values[LeaseLetterPlaceholders.IncreaseAmount] = LeaseLetterData.Money(calculation.Increase);
        values[LeaseLetterPlaceholders.NewRent] = LeaseLetterData.Money(calculation.NewRent);
        values[LeaseLetterPlaceholders.EffectiveOn] = LeaseLetterData.Day(effectiveFrom);

        return Build(lease, LeaseLetterKind.AggiornamentoIstat, new HashSet<string>(StringComparer.Ordinal), values, $"aggiornamento-istat-{lease.Id}.pdf");
    }

    public async Task<LeaseLetterFile> BuildOpzioneCedolareAsync(Guid leaseId, DateOnly? effectiveFrom, CancellationToken cancellationToken = default)
    {
        var lease = await LoadForLetterAsync(leaseId, cancellationToken);
        EnsureInForce(lease);

        // Only a lease under the cedolare secca has the option to communicate.
        var facts = LeaseOperationRules.CedolareNotice(lease, _clock.TodayInRomeAsDateOnly());
        if (!facts.Available)
            throw new DomainRuleException(LeaseLifecycleCodes.LetterNotApplicable, "LeaseLetterNotApplicable");
        if (effectiveFrom is { } day && (day < facts.MinEffectiveOn || day > facts.MaxEffectiveOn))
        {
            throw new DomainRuleException(
                LeaseLifecycleCodes.LetterEffectDateInvalid, "LeaseLetterEffectDateInvalid", facts.MinEffectiveOn, facts.MaxEffectiveOn);
        }

        var values = LeaseLetterData.Common(lease);
        values[LeaseLetterPlaceholders.EffectiveOn] = effectiveFrom is { } firstDay ? LeaseLetterData.Day(firstDay) : null;

        return Build(lease, LeaseLetterKind.OpzioneCedolare, new HashSet<string>(StringComparer.Ordinal), values, $"opzione-cedolare-{lease.Id}.pdf");
    }

    /// <summary>
    /// The checks the calculation and the letter of the ISTAT update share: the lease is in force and not ended, is not under the cedolare secca
    /// (which suspends every update), has a rent; the variation is above zero (the rent only ever goes up), the percentages are valid and the
    /// increase is not rounded away. Then the calculation of <see cref="LeaseIstatRules.Calculate"/>.
    /// </summary>
    private static LeaseIstatCalculation CalculateIstat(LeaseContract lease, decimal foiPercent, decimal sharePercent)
    {
        EnsureInForce(lease);
        if (lease.TerminatedOn is not null)
            throw new DomainConflictException(LeaseLifecycleCodes.AlreadyTerminated, "LeaseAlreadyTerminated");
        if (LeaseIstatRules.UpdatesWaived(lease))
            throw new DomainRuleException(LeaseLifecycleCodes.IstatWaived, "LeaseIstatWaived");
        if (lease.MonthlyRent <= 0)
            throw new DomainRuleException(LeaseLifecycleCodes.IstatNoRent, "LeaseIstatNoRent");
        if (foiPercent <= 0)
            throw new DomainRuleException(LeaseLifecycleCodes.IstatNoIncrease, "LeaseIstatNoIncrease");
        if (LeaseIstatRules.Check(foiPercent, sharePercent) != LeaseIstatProblem.None)
            throw new DomainRuleException(LeaseLifecycleCodes.IstatPercentInvalid, "LeaseIstatPercentInvalid", LeaseIstatRules.MaxSharePercent);

        var calculation = LeaseIstatRules.Calculate(lease.MonthlyRent, foiPercent, sharePercent);
        if (calculation.Increase <= 0)
            throw new DomainRuleException(LeaseLifecycleCodes.IstatNoIncrease, "LeaseIstatNoIncrease");
        return calculation;
    }

    private LeaseLetterFile Build(
        LeaseContract lease, LeaseLetterKind kind, IReadOnlySet<string> flags, IReadOnlyDictionary<string, string?> values, string fileName)
    {
        var state = letters.Get(kind);
        if (!state.IsUsable)
        {
            // A problem of the deployment (its file is missing or broken), never of the person: told in the log, with no personal data.
            logger.LogError(
                "Lease letter cannot be built: {Kind} is {Status} (version {VersionId}): {Problems}. LeaseId={LeaseId}",
                kind, state.Status, state.VersionId, string.Join("; ", state.Problems), lease.Id);
            throw new DomainRuleException(LeaseLifecycleCodes.LetterUnavailable, "LeaseLetterUnavailable");
        }

        var pdf = pdfRenderer.Render(LeaseLetterDocument.Build(state, flags, values));
        logger.LogInformation(
            "Lease letter built. LeaseId={LeaseId} Kind={Kind} Version={VersionId} Approved={Approved}", lease.Id, kind, state.VersionId, state.IsApproved);
        return new LeaseLetterFile(pdf, fileName);
    }

    // ---- the letter was sent ---------------------------------------------------------------------------------------

    public async Task MarkLetterSentAsync(
        Guid leaseId, LeaseLetterKind kind, DateOnly sentOn, LeaseLetterMeans means, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown letter");
        if (!Enum.IsDefined(means))
            throw new ArgumentOutOfRangeException(nameof(means), means, "Unknown means");

        var lease = await db.LeaseContracts.FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken) ?? throw NotFound(leaseId);
        EnsureInForce(lease);
        if (sentOn > _clock.TodayInRomeAsDateOnly() || sentOn < RomeCalendar.DateInRome(lease.StartDate))
            throw new DomainRuleException(LeaseLifecycleCodes.LetterDateInvalid, "LeaseLetterDateInvalid");

        // The lease itself does not change: the event is the whole record, with the day and the means and no copy of the letter.
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.LetterSent,
            OccurredAt = _clock.GetUtcNow().UtcDateTime,
            Payload = $"{kind}:{means}:{Format(sentOn)}",
        });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Lease letter marked as sent. LeaseId={LeaseId} Kind={Kind} Means={Means}", lease.Id, kind, means);
    }

    // ---- reading the history ---------------------------------------------------------------------------------------

    /// <summary>The last time the landlord marked a letter of this kind as sent (the payload <c>{kind}:{means}:{day}</c>), or <c>null</c>.</summary>
    private static LeaseLetterSentDto? LastSent(LeaseContract lease, LeaseLetterKind kind)
    {
        foreach (var e in lease.Events.Where(e => e.EventType == LeaseEventType.LetterSent).OrderByDescending(e => e.OccurredAt))
        {
            var parts = e.Payload?.Split(':');
            if (parts is { Length: 3 }
                && parts[0] == kind.ToString()
                && Enum.TryParse<LeaseLetterMeans>(parts[1], out var means) && Enum.IsDefined(means)
                && TryDay(parts[2], out var day))
            {
                return new LeaseLetterSentDto(day, means);
            }
        }

        return null;
    }

    /// <summary>The withdrawal of the tenant already recorded (the payload <c>{reason}:{receivedOn}:{effectiveOn}:{months}</c>), or <c>null</c>.</summary>
    private static LeaseWithdrawalRecordDto? RecordedWithdrawal(LeaseContract lease)
    {
        foreach (var e in lease.Events.Where(e => e.EventType == LeaseEventType.WithdrawalRecorded).OrderByDescending(e => e.OccurredAt))
        {
            var parts = e.Payload?.Split(':');
            if (parts is { Length: 4 }
                && Enum.TryParse<TenantWithdrawalReason>(parts[0], out var reason) && Enum.IsDefined(reason)
                && TryDay(parts[1], out var receivedOn)
                && TryDay(parts[2], out var effectiveOn)
                && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var months))
            {
                return new LeaseWithdrawalRecordDto(receivedOn, reason, effectiveOn, months);
            }
        }

        return null;
    }

    /// <summary>
    /// The last renewal the landlord recorded (the payload <c>{Standard|Agreed}:{from}:{to}</c>) when it is the one of the period the lease is in
    /// now (<paramref name="currentEnd"/>), or <c>null</c>: once the lease has moved on to a later period or ended early, the record is history.
    /// </summary>
    private static LeaseRenewalRecordDto? RecordedRenewal(LeaseContract lease, DateOnly currentEnd)
    {
        foreach (var e in lease.Events.Where(e => e.EventType == LeaseEventType.RenewalRecorded).OrderByDescending(e => e.OccurredAt))
        {
            var parts = e.Payload?.Split(':');
            if (parts is { Length: 3 }
                && parts[0] is "Standard" or "Agreed"
                && TryDay(parts[1], out var from)
                && TryDay(parts[2], out var to))
            {
                return to == currentEnd ? new LeaseRenewalRecordDto(parts[0] == "Agreed", from, to) : null;
            }
        }

        return null;
    }

    // ---- loading and checking --------------------------------------------------------------------------------------

    /// <summary>The lease with its property and its parties (their residences are decrypted here), read only.</summary>
    private async Task<LeaseContract> LoadForLetterAsync(Guid leaseId, CancellationToken cancellationToken) =>
        await db.LeaseContracts
            .AsNoTracking()
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
        ?? throw NotFound(leaseId);

    private static void EnsureInForce(LeaseContract lease)
    {
        if (!LeaseRenewal.IsInForce(lease.Status))
            throw new DomainConflictException(LeaseLifecycleCodes.NotInForce, "LeaseNotInForce");
    }

    private static NotFoundException NotFound(Guid leaseId) =>
        new($"Lease {leaseId} not found.") { Code = "lease_not_found", MessageKey = "LeaseNotFound" };

    private static bool TryDay(string text, out DateOnly day) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime ToStoredDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
