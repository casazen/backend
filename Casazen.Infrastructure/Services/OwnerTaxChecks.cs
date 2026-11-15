using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerTaxChecks"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The units of an owner</b> are the distinct Italian properties in short-rent mode (not deleted) where the owner has an ownership in force on the day (started
/// on or before it, not over: the definition of the owner readiness), plus the ones it declared outside the agency. A co-owner counts the property once, whatever its share:
/// the limit is counted for each co-owner. The count is the whole org's, never narrowed to the reach of a caller.</item>
/// <item><b>The stay of a company tenant</b> is judged on the owners of the property on the check-out day (started on or before it, not ended on it: the definition of the
/// owner ledger and of the document plan, so it is the owner the ledger credits the stay to). Only the kind of the recipient (private person or company) is read from its
/// invoice data, never a name, a fiscal code or an address.</item>
/// <item><b>The «Da fare»</b> is raised once for each booking (never again when a person closed it) with a fixed sentence in the language of the org; it is closed by the decision,
/// by the cancellation or the no-show of the booking (<c>ActivityAutoCloseHandler</c>), and by the system when the stay is no case any more.</item>
/// <item>Every query names the org; it reads through the tenant filter of its caller. The logs carry ids and counts, never the reason of a decision.</item>
/// </list>
/// </remarks>
public sealed class OwnerTaxChecks(
    AppDbContext db,
    IInternalActivityService activities,
    IFeatureFlags featureFlags,
    IOptions<ShortStayFiscalOptions> fiscalOptions,
    TimeProvider time,
    ILogger<OwnerTaxChecks> logger) : IOwnerTaxChecks
{
    /// <summary>The key of the sentence of the «Da fare» of a company tenant in <c>EmailTexts</c> (Italian and English): the two readings in plain words.</summary>
    public const string CompanyTenantReasonKey = "TodoReason_CompanyTenantDecision";

    /// <summary>How many of the most recent stays of a company tenant a list examines before it keeps the ones that need a decision.</summary>
    private const int ExaminedStays = 2_000;

    private ShortStayFiscalOptions Rules => fiscalOptions.Value;

    public bool WarningsEnabled => featureFlags.IsEnabled(FeatureFlags.TaxReportingExports);

    public async Task<IReadOnlyDictionary<Guid, OwnerUnitsCheck>> CheckUnitsAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> ownerIds,
        DateOnly asOf,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownerIds);
        var ids = ownerIds.Distinct().ToList();
        var checks = new Dictionary<Guid, OwnerUnitsCheck>();
        if (ids.Count == 0)
            return checks;

        var owners = await db.PropertyOwners
            .AsNoTracking()
            .Where(o => o.OrgId == orgId && ids.Contains(o.Id))
            .Select(o => new { o.Id, o.EntityKind, o.TaxRegime, o.ExternalShortLetUnits })
            .ToListAsync(ct);
        if (owners.Count == 0)
            return checks;

        // The properties the agency manages for each owner: an Italian property in short-rent mode (a long lease is no short let, and the regime is Italian), through an
        // ownership in force on the day. The join to the property carries its soft-delete filter: a deleted property is no unit.
        var periods = await db.PropertyOwnerships
            .AsNoTracking()
            .Where(po => po.OrgId == orgId
                         && ids.Contains(po.PropertyOwnerId)
                         && po.Property.Country == FiscalDocumentRules.Italy
                         && po.Property.RentalMode == RentalMode.Short)
            .Select(po => new { po.PropertyOwnerId, po.PropertyId, po.OwnershipStart, po.OwnershipEnd })
            .ToListAsync(ct);
        var managed = periods
            .Where(p => IsLiveOn(p.OwnershipStart, p.OwnershipEnd, asOf))
            .GroupBy(p => p.PropertyOwnerId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.PropertyId).Distinct().Count());

        foreach (var owner in owners)
        {
            managed.TryGetValue(owner.Id, out var units);
            checks[owner.Id] = OwnerTaxRules.CheckUnits(owner.Id, owner.EntityKind, owner.TaxRegime, units, owner.ExternalShortLetUnits, asOf, Rules);
        }

        return checks;
    }

    public async Task<OwnerUnitsCheck> GetUnitsCheckAsync(HostScope scope, Guid ownerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await RequireOwnerInReachAsync(scope, ownerId, ct);
        return await CheckOfAsync(scope.OrgId, ownerId, ct);
    }

    public async Task<OwnerUnitsCheck> DeclareExternalUnitsAsync(HostScope scope, Guid ownerId, int? units, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (units is not { } declared || declared < 0 || declared > PropertyOwner.ExternalShortLetUnitsMax)
        {
            throw new DomainRuleException(OwnerTaxErrors.ExternalUnitsInvalid, "OwnerExternalUnitsInvalid")
            {
                Extensions = new Dictionary<string, object?> { ["field"] = "units" },
            };
        }

        await RequireOwnerInReachAsync(scope, ownerId, ct);
        var owner = await db.PropertyOwners.FirstAsync(o => o.OrgId == scope.OrgId && o.Id == ownerId, ct);
        if (owner.ExternalShortLetUnits != declared)
        {
            owner.ExternalShortLetUnits = declared;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Owner {OwnerId} of org {OrgId} declares {Units} apartments let short outside the agency (by {UserId})",
                ownerId,
                scope.OrgId,
                declared,
                userId);
        }

        return await CheckOfAsync(scope.OrgId, ownerId, ct);
    }

    public async Task<ShortLetTaxDecision> DecideAsync(HostScope scope, DecideShortLetTaxCommand command, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (command.BookingId == Guid.Empty)
            throw DecisionInvalid("bookingId");
        if (command.Regime is not { } regime || !Enum.IsDefined(regime))
            throw DecisionInvalid("regime");

        var reason = command.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > ShortLetTaxDecision.ReasonMaxLength || reason.Any(char.IsControl))
            throw DecisionInvalid("reason");

        var stay = (await FactsAsync(scope.OrgId, [command.BookingId], scope, ct)).SingleOrDefault()
                   ?? throw new NotFoundException($"Booking {command.BookingId} was not found") { Code = OwnerTaxErrors.BookingNotFound, MessageKey = "BookingNotFound" };

        // A regime is never recorded for a stay that needs none: it would switch the withholding off for a stay nobody questioned.
        if (!stay.IsCase)
            throw new DomainRuleException(OwnerTaxErrors.DecisionNotApplicable, "OwnerTaxDecisionNotApplicable");

        var current = OwnerTaxRules.CurrentDecision(
            await db.ShortLetTaxDecisions.AsNoTracking().Where(d => d.OrgId == scope.OrgId && d.BookingId == stay.BookingId).ToListAsync(ct));
        if (current is not null && current.Regime == regime && string.Equals(current.Reason, reason, StringComparison.Ordinal))
        {
            // The same decision again (a retry, a second click): nothing is written twice; the «Da fare» is closed all the same, should the first call have stopped before.
            await CloseTodoAsync(scope.OrgId, stay.BookingId, userId, ct);
            return current;
        }

        var decision = new ShortLetTaxDecision
        {
            OrgId = scope.OrgId,
            BookingId = stay.BookingId,
            Regime = regime,
            Reason = reason,
            DecidedAt = UtcDateTime.TruncateToMicroseconds(time.GetUtcNow().UtcDateTime),
            DecidedByUserId = userId,
        };
        db.ShortLetTaxDecisions.Add(decision);
        await db.SaveChangesAsync(ct);
        await CloseTodoAsync(scope.OrgId, stay.BookingId, userId, ct);

        // The reason is the agency's note: the log names the decision, the booking and who decided it, never what was written.
        logger.LogInformation(
            "Short-let tax decision {DecisionId} of booking {BookingId} of org {OrgId}: {Regime} (by {UserId})",
            decision.Id,
            stay.BookingId,
            scope.OrgId,
            regime,
            userId);
        return decision;
    }

    public async Task<IReadOnlyList<CompanyTenantCase>> ListCompanyTenantCasesAsync(HostScope scope, CompanyTenantCaseState? state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (state is { } named && !Enum.IsDefined(named))
            return [];

        var statuses = AlloggiatiStatusRules.ActiveBookingStatuses;
        var companyBookings = db.BookingInvoiceData
            .AsNoTracking()
            .Where(d => d.OrgId == scope.OrgId && d.RecipientKind == InvoiceRecipientKind.Company)
            .Select(d => d.BookingId);

        // The stays of a company in an Italian property, active, in the reach of the caller, the most recent first; the kind of the owners is judged on what is loaded.
        var rows = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId
                        && statuses.Contains(b.Status)
                        && b.NoShowAt == null
                        && b.Property.Country == FiscalDocumentRules.Italy
                        && companyBookings.Contains(b.Id))
            .InScope(scope)
            .OrderByDescending(b => b.CheckOutDate)
            .ThenBy(b => b.Id)
            .Take(ExaminedStays)
            .Select(b => new BookingRow(b.Id, b.BookingCode, b.PropertyId, b.Property.Name, b.Property.Country, b.Status, b.NoShowAt, b.CheckInDate, b.CheckOutDate))
            .ToListAsync(ct);

        var cases = (await EvaluateAsync(scope.OrgId, rows, ct)).Where(s => s.IsCase).ToList();
        var decisions = await DecisionsAsync(scope.OrgId, cases.Select(s => s.BookingId).ToList(), ct);

        return
        [
            .. cases
                .Select(s => (Stay: s, Decision: decisions.GetValueOrDefault(s.BookingId)))
                .Where(c => state is null || (state == CompanyTenantCaseState.Pending) == (c.Decision is null))
                .Take(OwnerTaxLimits.MaxCases)
                .Select(c => new CompanyTenantCase(
                    c.Stay.BookingId,
                    c.Stay.BookingCode,
                    c.Stay.PropertyId,
                    c.Stay.PropertyName,
                    c.Stay.CheckIn,
                    c.Stay.CheckOut,
                    c.Decision?.Regime,
                    c.Decision?.DecidedAt,
                    c.Decision?.DecidedByUserId)),
        ];
    }

    public async Task<bool> RaiseCompanyTenantDecisionAsync(Guid orgId, Guid bookingId, CancellationToken ct = default)
    {
        // The warnings of the UI: while the flag is off nothing is raised (the facts of the withholding do not wait for it).
        if (!WarningsEnabled || bookingId == Guid.Empty)
            return false;

        var stay = (await FactsAsync(orgId, [bookingId], null, ct)).SingleOrDefault();
        if (stay is null)
            return false;

        // The stay is no case (any more): the invoice data became a private person's, or the dates, the status or the owners changed. There is nothing to decide, and a decision
        // would be refused (422 owner_tax_decision_not_applicable): the item raised before is closed by the system, as the cancellation and the no-show close it.
        if (!stay.IsCase)
        {
            await CloseTodoAsync(orgId, bookingId, InternalActivity.ClosedBySystem, ct);
            return false;
        }

        // Decided already: nothing to ask. Asked before and still open, or closed by a person: once for each booking, never again for a redelivered event. An item the system
        // closed because the stay was no case is neither: the stay is one again and nobody decided.
        if (await db.ShortLetTaxDecisions.AsNoTracking().AnyAsync(d => d.OrgId == orgId && d.BookingId == bookingId, ct)
            || await IsOpenOrAnsweredByAPersonAsync(orgId, bookingId, ct))
        {
            return false;
        }

        var request = new CreateTodoRequest
        {
            Type = ActivityType.CompanyTenantDecision,
            Action = ActivityAction.ReviewBooking,
            EntityType = InternalActivity.BookingEntityType,
            EntityId = bookingId,
            Urgency = ActivityUrgency.Medium,
            PropertyId = stay.PropertyId,
            Reason = await BookingTodoItems.ReasonAsync(db, orgId, CompanyTenantReasonKey, ct),
        };

        try
        {
            await activities.CreateAsync(request, ct);
        }
        catch (NotFoundException)
        {
            // The property is gone (deleted since): nobody to ask, and a retry would find the same.
            logger.LogInformation("Booking {BookingId} of org {OrgId}: property not found, no «Da fare» raised for its company tenant", bookingId, orgId);
            return false;
        }

        logger.LogInformation("Booking {BookingId} of org {OrgId}: the «Da fare» of its company tenant was raised", bookingId, orgId);
        return true;
    }

    public async Task<IReadOnlyList<StayShortLetTreatment>> TreatmentOfStaysAsync(
        Guid orgId,
        Guid ownerId,
        IReadOnlyCollection<Guid> bookingIds,
        DateOnly asOf,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bookingIds);
        var ids = bookingIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var presumedBusiness = (await CheckUnitsAsync(orgId, [ownerId], asOf, ct)).TryGetValue(ownerId, out var check) && check.PresumedBusiness;
        var stays = (await FactsAsync(orgId, ids, null, ct)).ToDictionary(s => s.BookingId);
        var decisions = await DecisionsAsync(orgId, [.. stays.Keys], ct);

        // In the order they were asked; a booking of another org, or one that does not exist, is left out.
        return
        [
            .. ids
                .Where(stays.ContainsKey)
                .Select(id => OwnerTaxRules.TreatmentOf(id, presumedBusiness, stays[id].IsCase, decisions.GetValueOrDefault(id))),
        ];
    }

    // ---------------------------------------------------------------- the owner

    private async Task<OwnerUnitsCheck> CheckOfAsync(Guid orgId, Guid ownerId, CancellationToken ct)
    {
        var checks = await CheckUnitsAsync(orgId, [ownerId], time.TodayInRomeAsDateOnly(), ct);
        return checks.TryGetValue(ownerId, out var check) ? check : throw OwnerNotFound(ownerId);
    }

    /// <summary>
    /// The owner is of the org of the scope and inside its reach: any owner for an org-wide scope; for a partial one only an owner that holds at least one ownership period and
    /// every one of them on a property the scope reaches (no declaration is never in reach). Anything else is the 404 of an owner that does not exist.
    /// </summary>
    private async Task RequireOwnerInReachAsync(HostScope scope, Guid ownerId, CancellationToken ct)
    {
        if (!await db.PropertyOwners.AsNoTracking().AnyAsync(o => o.OrgId == scope.OrgId && o.Id == ownerId, ct))
            throw OwnerNotFound(ownerId);

        if (scope.IsOrgWide)
            return;

        var reachable = db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).Select(p => p.Id);
        var inside = await db.PropertyOwnerships
            .AsNoTracking()
            .Where(po => po.OrgId == scope.OrgId && po.PropertyOwnerId == ownerId)
            .Select(po => reachable.Contains(po.PropertyId))
            .ToListAsync(ct);
        if (inside.Count == 0 || inside.Any(isInside => !isInside))
            throw OwnerNotFound(ownerId);
    }

    /// <summary>The ownership is in force on <paramref name="day"/>: it started on or before it and is not over (its last day is that day or later).</summary>
    private static bool IsLiveOn(DateTime start, DateTime? end, DateOnly day) =>
        RomeCalendar.DateInRome(start) <= day && (end is not { } last || RomeCalendar.DateInRome(last) >= day);

    // ---------------------------------------------------------------- the stays

    /// <summary>
    /// The stays among <paramref name="bookingIds"/> of the org (and of the reach of <paramref name="scope"/>, when given) with whether each is one of a company tenant.
    /// </summary>
    private async Task<List<StayFacts>> FactsAsync(Guid orgId, IReadOnlyCollection<Guid> bookingIds, HostScope? scope, CancellationToken ct)
    {
        var ids = bookingIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var query = db.Bookings.AsNoTracking().Where(b => b.OrgId == orgId && ids.Contains(b.Id));
        if (scope is not null)
            query = query.InScope(scope);

        var rows = await query
            .Select(b => new BookingRow(b.Id, b.BookingCode, b.PropertyId, b.Property.Name, b.Property.Country, b.Status, b.NoShowAt, b.CheckInDate, b.CheckOutDate))
            .ToListAsync(ct);
        return await EvaluateAsync(orgId, rows, ct);
    }

    private async Task<List<StayFacts>> EvaluateAsync(Guid orgId, IReadOnlyList<BookingRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        var bookingIds = rows.Select(r => r.Id).ToList();
        var companyBookings = (await db.BookingInvoiceData
                .AsNoTracking()
                .Where(d => d.OrgId == orgId && bookingIds.Contains(d.BookingId) && d.RecipientKind == InvoiceRecipientKind.Company)
                .Select(d => d.BookingId)
                .ToListAsync(ct))
            .ToHashSet();

        var propertyIds = rows.Select(r => r.PropertyId).Distinct().ToList();
        var ownerships = await db.PropertyOwnerships
            .AsNoTracking()
            .Where(po => po.OrgId == orgId && propertyIds.Contains(po.PropertyId))
            .Select(po => new { po.PropertyId, po.PropertyOwnerId, po.OwnershipStart, po.OwnershipEnd })
            .ToListAsync(ct);
        var ownerIds = ownerships.Select(o => o.PropertyOwnerId).Distinct().ToList();
        var owners = new Dictionary<Guid, (OwnerEntityKind? Kind, OwnerTaxRegime Regime)>();
        if (ownerIds.Count > 0)
        {
            var found = await db.PropertyOwners
                .AsNoTracking()
                .Where(o => o.OrgId == orgId && ownerIds.Contains(o.Id))
                .Select(o => new { o.Id, o.EntityKind, o.TaxRegime })
                .ToListAsync(ct);
            foreach (var owner in found)
                owners[owner.Id] = (owner.EntityKind, owner.TaxRegime);
        }

        var stays = new List<StayFacts>(rows.Count);
        foreach (var row in rows)
        {
            var day = DateOnly.FromDateTime(row.CheckOut);
            var onTheDay = ownerships
                .Where(o => o.PropertyId == row.PropertyId
                            && DateOnly.FromDateTime(o.OwnershipStart) <= day
                            && (o.OwnershipEnd is not { } end || DateOnly.FromDateTime(end) > day))
                .Select(o => o.PropertyOwnerId)
                .Distinct()
                .Where(owners.ContainsKey)
                .Select(id => owners[id])
                .ToList();

            var isCase = OwnerTaxRules.IsCompanyTenantCase(
                AlloggiatiStatusRules.ActiveBookingStatuses.Contains(row.Status) && row.NoShowAt is null,
                string.Equals(row.Country?.Trim(), FiscalDocumentRules.Italy, StringComparison.OrdinalIgnoreCase),
                OwnerTaxRules.Nights(row.CheckIn, row.CheckOut),
                Rules.MaxStayNights,
                companyBookings.Contains(row.Id),
                onTheDay);
            stays.Add(new StayFacts(row.Id, row.BookingCode, row.PropertyId, row.PropertyName, DateOnly.FromDateTime(row.CheckIn), day, isCase));
        }

        return stays;
    }

    /// <summary>The decision in force for each of the bookings that has one.</summary>
    private async Task<Dictionary<Guid, ShortLetTaxDecision>> DecisionsAsync(Guid orgId, List<Guid> bookingIds, CancellationToken ct)
    {
        if (bookingIds.Count == 0)
            return new Dictionary<Guid, ShortLetTaxDecision>();

        var rows = await db.ShortLetTaxDecisions
            .AsNoTracking()
            .Where(d => d.OrgId == orgId && bookingIds.Contains(d.BookingId))
            .ToListAsync(ct);
        return rows.GroupBy(d => d.BookingId).ToDictionary(g => g.Key, g => OwnerTaxRules.CurrentDecision(g)!);
    }

    /// <summary>Closes the open «Da fare» of the company tenant of the booking (closed by the person who decided); nothing when there is none.</summary>
    private async Task CloseTodoAsync(Guid orgId, Guid bookingId, string closedBy, CancellationToken ct)
    {
        var type = nameof(ActivityType.CompanyTenantDecision);
        var open = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.Action != null
                        && a.CompletedAt == null
                        && a.ActivityType == type
                        && a.EntityType == InternalActivity.BookingEntityType
                        && a.EntityId == bookingId)
            .Select(a => a.Id)
            .ToListAsync(ct);

        foreach (var id in open)
            await activities.CloseAsync(id, closedBy, ct);
    }

    /// <summary>
    /// A «Da fare» of the company tenant of the booking is open, or a person closed one (the decision closes it too): the stay was asked about, and it is not asked again. One the
    /// system closed (the stay was no case, was cancelled or was a no-show) does not count: it says nothing about the person.
    /// </summary>
    private Task<bool> IsOpenOrAnsweredByAPersonAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        var type = nameof(ActivityType.CompanyTenantDecision);
        return db.InternalActivities
            .AsNoTracking()
            .AnyAsync(
                a => a.OrgId == orgId
                     && a.Action != null
                     && a.ActivityType == type
                     && a.EntityType == InternalActivity.BookingEntityType
                     && a.EntityId == bookingId
                     && (a.CompletedAt == null || a.ClosedBy == null || a.ClosedBy != InternalActivity.ClosedBySystem),
                ct);
    }

    private static NotFoundException OwnerNotFound(Guid ownerId) =>
        new($"Owner {ownerId} was not found") { Code = OwnerFiscalErrors.OwnerNotFound, MessageKey = "OwnerNotFound" };

    private static DomainRuleException DecisionInvalid(string field) =>
        new(OwnerTaxErrors.DecisionInvalid, "OwnerTaxDecisionInvalid") { Extensions = new Dictionary<string, object?> { ["field"] = field } };

    /// <summary>A booking as the checks read it: the code and the property to name it by, the dates and what says whether it is active.</summary>
    private sealed record BookingRow(
        Guid Id,
        string BookingCode,
        Guid PropertyId,
        string PropertyName,
        string? Country,
        BookingStatus Status,
        DateTime? NoShowAt,
        DateTime CheckIn,
        DateTime CheckOut);

    /// <summary>A stay with whether it is one of a company tenant (<see cref="OwnerTaxRules.IsCompanyTenantCase"/>).</summary>
    private sealed record StayFacts(Guid BookingId, string BookingCode, Guid PropertyId, string PropertyName, DateOnly CheckIn, DateOnly CheckOut, bool IsCase);
}
