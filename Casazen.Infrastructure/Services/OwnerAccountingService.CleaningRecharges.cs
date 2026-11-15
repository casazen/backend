using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
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
/// <item><b>The VAT</b> is never guessed. The amount is posted as the cost source gives it, whatever its VAT nature (the ledger keeps none: the owner invoice applies the VAT of a
/// recharge, ⚖ R4); when the source does not say it (<see cref="CleaningAmountVat.Unknown"/>) the posting raises the «Da fare» <see cref="ActivityType.CleaningVatToState"/> for the
/// agency, once for each stay or request.</item>
/// </list>
/// </remarks>
public sealed partial class OwnerAccountingService
{
    /// <summary>The key, in <c>EmailTexts</c> (Italian and English), of the words of a cleaning recharge line: «Pulizia del {0}».</summary>
    internal const string CleaningRechargeTextKey = "OwnerLedger_CleaningRecharge";

    /// <summary>The key, in <c>EmailTexts</c> (Italian and English), of the reason of the «Da fare» that asks the agency to state the VAT of a cleaning.</summary>
    internal const string CleaningVatReasonKey = "TodoReason_CleaningVatToState";

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

            if (open)
                plan.Cleanings.Add(new CleaningFact(cleaning.PropertyId, cleaning.BookingId, cleaning.SourceKind, cleaning.SourceId, cleaning.AmountVat != CleaningAmountVat.Unknown));
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
    /// The «Da fare» of the cleanings recharged at their cost, once their lines are posted: one <see cref="ActivityType.CleaningVatToState"/> for each stay (or request, for a cleaning
    /// with no stay) whose VAT the cost source does not say, raised once (an item open, or one a person closed, is not raised again) and closed by the system when the VAT is
    /// known. Nothing without the service of the «Da fare». No personal data in the item or in the log: ids and counts. The rows read here are the «Da fare» rows of the table
    /// (the ones with an action), as the other raisers of the module read theirs (<c>OwnerTaxChecks</c>): never the staff work, whose cleanings come only from the cost source.
    /// </summary>
    private async Task SyncCleaningVatTodosAsync(Guid orgId, Plan plan, CancellationToken ct)
    {
        if (activities is null || plan.Cleanings.Count == 0)
            return;

        var type = nameof(ActivityType.CleaningVatToState);
        var unknown = plan.Cleanings.Where(c => !c.VatKnown).DistinctBy(c => c.EntityId).ToList();
        var unknownIds = unknown.Select(c => c.EntityId).ToHashSet();

        // The VAT of the cleaning is known now (its amount was corrected): the item the system raised is closed, unless another cleaning of the same stay still has none.
        var knownIds = plan.Cleanings.Where(c => c.VatKnown && !unknownIds.Contains(c.EntityId)).Select(c => c.EntityId).Distinct().ToList();
        if (knownIds.Count > 0)
        {
            var open = await db.InternalActivities.AsNoTracking()
                .Where(a => a.OrgId == orgId && a.Action != null && a.CompletedAt == null && a.ActivityType == type && a.EntityId != null && knownIds.Contains(a.EntityId.Value))
                .Select(a => a.Id)
                .ToListAsync(ct);
            foreach (var id in open)
                await activities.CloseAsync(id, InternalActivity.ClosedBySystem, ct);
        }

        string? reason = null;
        var raised = 0;
        foreach (var cleaning in unknown)
        {
            var entityType = cleaning.EntityType;
            var entityId = cleaning.EntityId;
            var asked = await db.InternalActivities.AsNoTracking()
                .AnyAsync(
                    a => a.OrgId == orgId && a.Action != null && a.ActivityType == type && a.EntityType == entityType && a.EntityId == entityId
                         && (a.CompletedAt == null || a.ClosedBy == null || a.ClosedBy != InternalActivity.ClosedBySystem),
                    ct);
            if (asked)
                continue;

            reason ??= await BookingTodoItems.ReasonAsync(db, orgId, CleaningVatReasonKey, ct);
            try
            {
                await activities.CreateAsync(
                    new CreateTodoRequest
                    {
                        Type = ActivityType.CleaningVatToState,
                        Action = ActivityAction.ReviewBooking,
                        EntityType = entityType,
                        EntityId = entityId,
                        Urgency = ActivityUrgency.Medium,
                        PropertyId = cleaning.PropertyId,
                        Reason = reason,
                    },
                    ct);
                raised++;
            }
            catch (NotFoundException)
            {
                // The property is gone (deleted since): nobody to ask, and a retry would find the same.
                logger.LogInformation("Cleaning of property {PropertyId} of org {OrgId}: property not found, no «Da fare» raised for its VAT", cleaning.PropertyId, orgId);
            }
        }

        if (raised > 0)
            logger.LogInformation("Org {OrgId}: {Raised} «Da fare» raised to state the VAT of a cleaning recharged to an owner", orgId, raised);
    }

    /// <summary>
    /// A cleaning recharged at its cost to an owner whose month is open, as the «Da fare» of the posting needs it: where it is (the property and the stay, if it has one), which row it
    /// comes from, and whether the cost source says its VAT.
    /// </summary>
    private sealed record CleaningFact(Guid PropertyId, Guid? BookingId, CleaningSourceKind SourceKind, Guid SourceId, bool VatKnown)
    {
        /// <summary>The kind of entity the item is about: the stay when the cleaning has one (the host opens it from the item), else the request or the staff activity itself.</summary>
        public string EntityType => BookingId is not null ? InternalActivity.BookingEntityType : SourceKind.ToString();

        /// <summary>The id of that entity.</summary>
        public Guid EntityId => BookingId ?? SourceId;
    }
}
