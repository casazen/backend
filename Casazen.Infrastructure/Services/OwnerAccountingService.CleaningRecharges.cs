using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The cleanings recharged to the owner at their cost (E14/F01/US02/T01, ⚖ I13, NF-114, NF-115). For an owner whose mandate asks for the actual cost
/// (<see cref="CleaningRechargeMode.ActualCost"/>) each cleaning completed in the month and recharged to the owner is one <see cref="OwnerLedgerEntryType.OwnerCharge"/> of source
/// <see cref="OwnerLedgerSourceType.CleaningRequest"/>, linked to its request, in place of the flat fee of the stay: a stay is never charged both.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Read only through the cost source.</b> The cleanings come from <see cref="ICleaningCostSource"/>, which never returns a cancelled or refused one: a request cancelled
/// after it was posted is simply no longer planned, so the posting reverses its line like any source that changed. The requests and the staff activities are never read here.</item>
/// <item><b>Whose and when.</b> The owners, the shares and the mandate are those of the day of the stay the cleaning is for (its check-out), so a stay and its cleanings are decided
/// by the same mandate; a cleaning that belongs to no stay uses the day it was completed. The line is dated the day the cost became firm (<see cref="CompletedCleaning.CompletedOn"/>),
/// which decides its month.</item>
/// <item><b>The amount</b> is the price of the request (what the cost source reads: the final amount, else the price agreed, else the estimate), or, for a staff cleaning that has none,
/// the flat rate of the mandate. A cleaning that has no price at all is never posted as a charge of zero: it holds the owner's statement back (<c>source_amount_missing</c>).</item>
/// <item><b>The VAT</b> is never guessed. The amount is posted as the cost source gives it, gross and not decomposed (D4: the ledger keeps no VAT; the owner invoice applies it,
/// ⚖ R4). The VAT a cleaning company applied is what the agency states (FIX/BE-CLEANING-VAT, <see cref="ICleaningVatService"/>): for a cleaning of a company that nobody stated
/// (no answer of the request, nothing remembered for the company) the posting raises the «Da fare» <see cref="ActivityType.CleaningVatToState"/> about the request, once; the
/// close of the owner's month proposes it once more when the answer was «Non lo so». Only where the answer changes the invoice of the agency (its rule charges VAT on a recharge,
/// the cleaning is in the rule's country). A staff cleaning has no VAT of a company and is never asked.</item>
/// </list>
/// </remarks>
public sealed partial class OwnerAccountingService
{
    /// <summary>The key, in <c>EmailTexts</c> (Italian and English), of the words of a cleaning recharge line: «Pulizia del {0}».</summary>
    internal const string CleaningRechargeTextKey = "OwnerLedger_CleaningRecharge";

    /// <summary>The key, in <c>EmailTexts</c> (Italian and English), of the reason of the «Da fare» that asks the agency whether the cleaning company applied VAT.</summary>
    internal const string CleaningVatReasonKey = "TodoReason_CleaningVatToState";

    /// <summary>The key, in <c>EmailTexts</c> (Italian and English), of the reason of the same «Da fare» proposed once more at the close of the month.</summary>
    internal const string CleaningVatAgainReasonKey = "TodoReason_CleaningVatToStateAgain";

    /// <summary>The states of a cleaning request whose invoice the company issued: completed, or paid.</summary>
    private static readonly ServiceRequestStatus[] CleaningDone = [ServiceRequestStatus.Completato, ServiceRequestStatus.Pagato];

    /// <summary>The prefix of the key of a line of a cleaning request: <c>clean:{requestId:N}</c>, once for each request and owner.</summary>
    internal const string CleaningKeyPrefix = "clean:";

    /// <summary>
    /// The cleanings completed in the month that a mandate asking for the actual cost may recharge: read only through <see cref="ICleaningCostSource"/>, for the properties of the owners
    /// that have such a mandate (any version of it: the stay of a cleaning may belong to the version before), and not at all when no mandate asks for it or the source is not given.
    /// </summary>
    private async Task<IReadOnlyList<CompletedCleaning>> ReadCleaningsAsync(Guid orgId, Period period, IReadOnlyList<OwnerMandate> mandates, CancellationToken ct)
    {
        if (cleaningCosts is null)
            return [];

        var owners = mandates
            .Where(m => OwnerMandateRules.CleaningRechargeModeOf(m) == CleaningRechargeMode.ActualCost)
            .Select(m => m.PropertyOwnerId)
            .Distinct()
            .ToList();
        if (owners.Count == 0)
            return [];

        var properties = await db.PropertyOwnerships.AsNoTracking()
            .Where(o => o.OrgId == orgId && owners.Contains(o.PropertyOwnerId))
            .Select(o => o.PropertyId)
            .Distinct()
            .ToListAsync(ct);
        return properties.Count == 0 ? [] : await cleaningCosts.GetCompletedAsync(orgId, properties, period.First, period.Last, ct);
    }

    /// <summary>
    /// The charges of the cleanings read for the month: for each one recharged to the owner, the share of each owner of the property whose mandate asks for the actual cost, in the
    /// currency of the property (or the settlement currency of the owner for the flat rate of a staff cleaning), split by the shares like every charge. A share whose mandate is the
    /// flat rate gets nothing here: it gets the flat fee of the stay. An owner whose month is closed is only compared (the plan holds the lines, the posting leaves them).
    /// </summary>
    private async Task PlanCleaningsAsync(PlanInputs inputs, IReadOnlyList<CompletedCleaning> cleanings, CancellationToken ct)
    {
        if (cleanings.Count == 0)
            return;

        var plan = inputs.Plan;
        var checkOuts = await CheckOutDaysAsync(inputs.OrgId, cleanings, ct);

        // The VAT of the companies the agency stated (FIX/BE-CLEANING-VAT): the posting never uses it (the lines stay gross), the «Da fare» of the question does.
        var companyCleanings = cleanings.Where(c => c.RechargeToOwner && c.Kind == CleaningKind.Supplier).Select(c => c.SourceId).ToList();
        var stated = await CleaningSupplierVatReader.ReadAsync(db, inputs.OrgId, companyCleanings, ct);
        CultureInfo? culture = null;
        foreach (var cleaning in cleanings)
        {
            // A staff cleaning the agency does not recharge is its own cost.
            if (!cleaning.RechargeToOwner)
                continue;

            // The owners, the shares and the mandate of the day of the stay the cleaning is for, so the flat fee of the stay and its cleanings are decided by the same mandate.
            var day = cleaning.BookingId is { } bookingId && checkOuts.TryGetValue(bookingId, out var checkOut) ? checkOut : cleaning.CompletedOn;
            if (!ActiveOwnerships(inputs.Ownerships, cleaning.PropertyId, day).Any(o => AsksActualCost(inputs, o, day)))
                continue;

            // The cleaning is shared by the owners of the property: a gap in the shares is told (and holds the owners back), never guessed.
            if (SharesOn(plan, inputs.Ownerships, cleaning.PropertyId, day, cleaning.SourceId) is not { } shares)
                continue;

            culture ??= await OrgCultureAsync(inputs.OrgId, ct);
            var reference = OwnerLedgerCleaningRecharges.ReferenceOf(cleaning.SourceId);
            var description = string.Format(culture, EmailTexts.Get(CleaningRechargeTextKey, culture), DocumentLanguages.Day(cleaning.CompletedOn));
            var supplierInvoice = Truncate(cleaning.SupplierInvoiceReference, OwnerLedgerEntry.AttachmentMaxLength);
            var open = false;
            for (var i = 0; i < shares.Count; i++)
            {
                var share = shares[i];
                var mandate = OwnerMandateRules.InForce(inputs.Mandates, share.OwnerId, share.OwnershipId, day);
                if (mandate is null)
                {
                    plan.MandateMissing(share.OwnerId, cleaning.PropertyId, cleaning.BookingId ?? cleaning.SourceId);
                    continue;
                }

                if (OwnerMandateRules.CleaningRechargeModeOf(mandate) != CleaningRechargeMode.ActualCost)
                    continue;

                open |= !plan.ClosedOwners.Contains(share.OwnerId);
                var context = await ContextAsync(inputs.OrgId, share.OwnerId, inputs.Period, plan, inputs.Contexts, ct);
                var (total, currency) = PriceOf(cleaning, mandate, context);
                if (total <= 0m)
                {
                    // No price at all is never a charge of zero: the owner's statement waits until the cleaning has one.
                    plan.Issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.SourceAmountMissing, share.OwnerId, cleaning.PropertyId, cleaning.SourceId));
                    continue;
                }

                var source = new Source(
                    OwnerLedgerEntryType.OwnerCharge,
                    OwnerLedgerSourceType.CleaningRequest,
                    cleaning.SourceId,
                    reference,
                    CleaningKeyPrefix + cleaning.SourceId.ToString("N"),
                    cleaning.PropertyId,
                    currency,
                    -total,
                    cleaning.CompletedOn,
                    cleaning.CompletedOn,
                    null,
                    cleaning.CompletedOn,
                    description,
                    supplierInvoice);
                inputs.Builder.Add(source, share, -Split(total, shares)[i], context);
            }

            // Only a cleaning of a company has a VAT to state: a staff cleaning is the org's own rate. «Non lo so» is an answer (not asked again at the posting) but states nothing.
            if (open && cleaning.Kind == CleaningKind.Supplier)
            {
                var answer = stated.GetValueOrDefault(cleaning.SourceId);
                plan.Cleanings.Add(new CleaningFact(
                    cleaning.PropertyId, cleaning.BookingId, cleaning.SourceId, answer is not null, answer is not null && answer.Answer != CleaningSupplierVat.Unknown));
            }
        }
    }

    /// <summary>True when the mandate in force on <paramref name="day"/> for the share held through <paramref name="ownership"/> recharges the cleaning at its cost.</summary>
    private static bool AsksActualCost(PlanInputs inputs, PropertyOwnership ownership, DateOnly day)
    {
        return OwnerMandateRules.InForce(inputs.Mandates, ownership.PropertyOwnerId, ownership.Id, day) is { } mandate
            && OwnerMandateRules.CleaningRechargeModeOf(mandate) == CleaningRechargeMode.ActualCost;
    }

    /// <summary>
    /// What the cleaning costs, and in which currency: its price in the currency of its property; for a staff cleaning with no price, the flat rate of the mandate in the settlement
    /// currency of the owner (the mandate says it in that currency). A total of 0 is a cleaning with no price at all (never a supplier's: a supplier without an amount has no price to guess).
    /// </summary>
    private static (decimal Total, string Currency) PriceOf(CompletedCleaning cleaning, OwnerMandate mandate, OwnerContext context)
    {
        var propertyCurrency = StayCurrency.Normalize(cleaning.Currency);
        if (cleaning.AmountCents > 0)
            return (cleaning.AmountCents / 100m, propertyCurrency);

        if (cleaning.Kind == CleaningKind.Staff && mandate.CleaningFlatRate is { } rate and > 0m)
            return (rate, context.Currency ?? propertyCurrency);

        return (0m, propertyCurrency);
    }

    /// <summary>The check-out day of the stays the cleanings are for (a stay that is gone, or is not the org's, is not in the answer).</summary>
    private async Task<Dictionary<Guid, DateOnly>> CheckOutDaysAsync(Guid orgId, IReadOnlyList<CompletedCleaning> cleanings, CancellationToken ct)
    {
        var ids = cleanings.Where(c => c.RechargeToOwner && c.BookingId is not null).Select(c => c.BookingId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var rows = await db.Bookings.AsNoTracking()
            .Where(b => b.OrgId == orgId && ids.Contains(b.Id))
            .Select(b => new { b.Id, b.CheckOutDate })
            .ToListAsync(ct);
        return rows.ToDictionary(b => b.Id, b => DateOnly.FromDateTime(b.CheckOutDate));
    }

    /// <summary>The culture of the words the org writes down (<c>Org.DefaultLanguage</c>, Italian without one): the line of a cleaning and the reason of its «Da fare».</summary>
    private async Task<CultureInfo> OrgCultureAsync(Guid orgId, CancellationToken ct)
    {
        var language = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.DefaultLanguage)
            .FirstOrDefaultAsync(ct);
        return OrgInvitationEmails.CultureOf(language);
    }

    /// <summary>
    /// The «Da fare» of the cleanings of a company recharged at their cost, once their lines are posted (FIX/BE-CLEANING-VAT): one <see cref="ActivityType.CleaningVatToState"/> about
    /// each request nobody answered, leading to the question (<see cref="ActivityAction.StateCleaningVat"/>), raised once (an item about the request that is open or that a person
    /// closed, or an item about its stay raised before FIX/BE-CLEANING-VAT that a person closed, is not raised again), and only where the answer changes the invoice of the agency
    /// (<see cref="CleaningVatRules.IsAskedUnder"/>: the rule of its owner invoices on the last day of the month charges VAT on a recharge and the cleaning is in the rule's
    /// country). The system closes the items about a request whose VAT is stated now (an answer other than «Non lo so», which states nothing: the item proposed again at the close
    /// stays open) or whose answer would change nothing, and supersedes the open items raised before FIX/BE-CLEANING-VAT (action <see cref="ActivityAction.ReviewBooking"/>, about
    /// the stay or the request of a cleaning of the month: they lead to the booking, never to the question) with the items about the requests. Nothing without the service of the
    /// «Da fare». No personal data in the item or in the log: ids and counts. The rows read here are the «Da fare» rows of the table (the ones with an action), never the staff work.
    /// </summary>
    private async Task SyncCleaningVatTodosAsync(Guid orgId, Period period, Plan plan, CancellationToken ct)
    {
        if (activities is null || plan.Cleanings.Count == 0)
            return;

        var type = nameof(ActivityType.CleaningVatToState);
        var cleanings = plan.Cleanings.DistinctBy(c => c.RequestId).ToList();
        var rule = await CleaningSupplierVatReader.OwnerInvoiceRuleAsync(db, taxRules, orgId, period.Last, ct);
        var countries = await CleaningSupplierVatReader.CountriesAsync(db, orgId, cleanings.Select(c => c.PropertyId).ToList(), ct);
        var asked = cleanings.Where(c => CleaningVatRules.IsAskedUnder(rule, countries.GetValueOrDefault(c.PropertyId))).ToList();
        var askedIds = asked.Select(c => c.RequestId).ToHashSet();

        // Closed by the system: the item about a request whose VAT is stated now or whose answer would change nothing, and the open items raised before FIX/BE-CLEANING-VAT
        // about a request or a stay of the month (superseded: the requests that still wait get their own item below).
        var closing = cleanings.Where(c => c.Stated || !askedIds.Contains(c.RequestId)).Select(c => c.RequestId).ToList();
        var requests = cleanings.Select(c => c.RequestId).ToList();
        var stays = cleanings.Where(c => c.BookingId is not null).Select(c => c.BookingId!.Value).Distinct().ToList();
        var legacy = ActivityAction.ReviewBooking;
        var open = await db.InternalActivities.AsNoTracking()
            .Where(a => a.OrgId == orgId && a.Action != null && a.CompletedAt == null && a.ActivityType == type && a.EntityId != null
                        && ((a.EntityType == CleaningVatRules.TodoEntityType
                             && (closing.Contains(a.EntityId.Value) || (a.Action == legacy && requests.Contains(a.EntityId.Value))))
                            || (a.EntityType == InternalActivity.BookingEntityType && a.Action == legacy && stays.Contains(a.EntityId.Value))))
            .Select(a => a.Id)
            .ToListAsync(ct);
        foreach (var id in open)
            await activities.CloseAsync(id, InternalActivity.ClosedBySystem, ct);

        string? reason = null;
        var raised = 0;
        foreach (var cleaning in asked.Where(c => !c.Answered))
        {
            if (await WasAskedAsync(orgId, cleaning.RequestId, cleaning.BookingId, ct))
                continue;

            reason ??= await BookingTodoItems.ReasonAsync(db, orgId, CleaningVatReasonKey, ct);
            if (await AskCleaningVatAsync(orgId, cleaning.PropertyId, cleaning.RequestId, reason, ct))
                raised++;
        }

        if (raised > 0)
            logger.LogInformation("Org {OrgId}: {Raised} «Da fare» raised to state the VAT of a cleaning company recharged to an owner", orgId, raised);
    }

    /// <summary>
    /// The question was asked for the request already: an item about it that is open or that a person closed (one the system closed, a superseded one too, is asked again), or an
    /// item about its stay raised before FIX/BE-CLEANING-VAT (<see cref="ActivityAction.ReviewBooking"/>) that a person closed (an open one was superseded just before; an item of
    /// another type or action is never one).
    /// </summary>
    private Task<bool> WasAskedAsync(Guid orgId, Guid requestId, Guid? stay, CancellationToken ct)
    {
        var type = nameof(ActivityType.CleaningVatToState);
        var legacy = ActivityAction.ReviewBooking;
        return db.InternalActivities.AsNoTracking()
            .AnyAsync(
                a => a.OrgId == orgId && a.Action != null && a.ActivityType == type
                     && ((a.EntityType == CleaningVatRules.TodoEntityType && a.EntityId == requestId)
                         || (stay != null && a.EntityType == InternalActivity.BookingEntityType && a.Action == legacy && a.EntityId == stay))
                     && (a.CompletedAt == null || a.ClosedBy == null || a.ClosedBy != InternalActivity.ClosedBySystem),
                ct);
    }

    /// <summary>
    /// Raises the «Da fare» of the question about a cleaning request, leading to the question; <c>false</c> when its property is gone (deleted since: nobody to ask, and a retry
    /// would find the same).
    /// </summary>
    private async Task<bool> AskCleaningVatAsync(Guid orgId, Guid propertyId, Guid requestId, string reason, CancellationToken ct)
    {
        try
        {
            await activities!.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.CleaningVatToState,
                    Action = ActivityAction.StateCleaningVat,
                    EntityType = CleaningVatRules.TodoEntityType,
                    EntityId = requestId,
                    Urgency = ActivityUrgency.Medium,
                    PropertyId = propertyId,
                    Reason = reason,
                },
                ct);
            return true;
        }
        catch (NotFoundException)
        {
            logger.LogInformation("Cleaning request {RequestId} of org {OrgId}: property {PropertyId} not found, no «Da fare» raised for its VAT", requestId, orgId, propertyId);
            return false;
        }
    }

    /// <summary>
    /// At the close of an owner's month (FIX/BE-CLEANING-VAT): the cleanings of a company recharged to the owner in the month whose VAT is still «Non lo so», or stated by nobody,
    /// get their «Da fare» once more, once for each request (<see cref="ServiceRequest.SupplierVatAskedAgainAt"/>), unless an item about them is still open, and only where the
    /// answer changes the invoice of the agency (<see cref="CleaningVatRules.IsAskedUnder"/>, the rule of the last day of the closed month). «Non lo so» never blocks the close: this
    /// runs after it is saved. A request changed meanwhile by another operation is left as it is (logged); nothing without the service of the «Da fare».
    /// </summary>
    private async Task AskCleaningVatAgainAsync(Guid orgId, Guid ownerId, Period period, CancellationToken ct)
    {
        if (activities is null)
            return;

        var sources = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.PropertyOwnerId == ownerId && e.PeriodYear == period.Year && e.PeriodMonth == period.Month
                        && e.SourceType == OwnerLedgerSourceType.CleaningRequest && e.SourceId != null)
            .Select(e => e.SourceId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (sources.Count == 0)
            return;

        // The requests of a company among them (a staff activity is not one), completed or paid, not asked again yet.
        var requests = await db.ServiceRequests
            .Where(r => r.OrgId == orgId && sources.Contains(r.Id) && r.RentalContext != ServiceRequestRentalContext.Showcase && r.PropertyId != null
                        && r.Category == ServiceCategories.Cleaning && CleaningDone.Contains(r.Status) && r.SupplierVatAskedAgainAt == null)
            .ToListAsync(ct);
        if (requests.Count == 0)
            return;

        // Only where the answer changes the invoice of the agency: the rule of its owner invoices on the last day of the closed month and the country of each cleaning.
        var rule = await CleaningSupplierVatReader.OwnerInvoiceRuleAsync(db, taxRules, orgId, period.Last, ct);
        if (rule is null || !CleaningVatRules.ChargesVatOnRecharge(rule))
            return;

        var countries = await CleaningSupplierVatReader.CountriesAsync(db, orgId, requests.Select(r => r.PropertyId!.Value).ToList(), ct);
        var stated = await CleaningSupplierVatReader.ReadAsync(db, orgId, requests.Select(r => r.Id).ToList(), ct);
        string? reason = null;
        var asked = 0;
        foreach (var request in requests.Where(r => CleaningVatRules.IsAskedUnder(rule, countries.GetValueOrDefault(r.PropertyId!.Value))
                     && (!stated.TryGetValue(r.Id, out var vat) || vat.Answer == CleaningSupplierVat.Unknown)))
        {
            if (await HasOpenCleaningVatTodoAsync(orgId, request.Id, request.BookingId, ct))
                continue;

            request.SupplierVatAskedAgainAt = time.GetUtcNow().UtcDateTime;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(request).State = EntityState.Detached;
                logger.LogInformation("Cleaning request {RequestId} of org {OrgId}: changed while the close asked its VAT again, left as it is", request.Id, orgId);
                continue;
            }

            reason ??= await BookingTodoItems.ReasonAsync(db, orgId, CleaningVatAgainReasonKey, ct);
            if (await AskCleaningVatAsync(orgId, request.PropertyId!.Value, request.Id, reason, ct))
                asked++;
        }

        if (asked > 0)
            logger.LogInformation("Owner {OwnerId} of org {OrgId}, {Year}-{Month} closed: {Asked} «Da fare» of the VAT of a cleaning company proposed once more", ownerId, orgId, period.Year, period.Month, asked);
    }

    /// <summary>An item of the question is open about the request, or about its stay (raised before FIX/BE-CLEANING-VAT).</summary>
    private Task<bool> HasOpenCleaningVatTodoAsync(Guid orgId, Guid requestId, Guid? stay, CancellationToken ct)
    {
        var type = nameof(ActivityType.CleaningVatToState);
        return db.InternalActivities.AsNoTracking()
            .AnyAsync(
                a => a.OrgId == orgId && a.Action != null && a.ActivityType == type && a.CompletedAt == null
                     && ((a.EntityType == CleaningVatRules.TodoEntityType && a.EntityId == requestId)
                         || (stay != null && a.EntityType == InternalActivity.BookingEntityType && a.EntityId == stay)),
                ct);
    }

    /// <summary>
    /// A cleaning of a company recharged at its cost to an owner whose month is open, as the «Da fare» of the posting needs it: where it is (the property and the stay, if it has
    /// one), its request, whether it has an answer (the request's own, «Non lo so» too, or the one remembered for the company: it is not asked again at the posting) and whether
    /// that answer states the VAT (anything but «Non lo so»: only then does the system close an item about it).
    /// </summary>
    private sealed record CleaningFact(Guid PropertyId, Guid? BookingId, Guid RequestId, bool Answered, bool Stated);
}
