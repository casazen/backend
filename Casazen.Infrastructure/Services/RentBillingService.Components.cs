using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public sealed partial class RentBillingService
{
    public async Task<RentLedgerView> ConfirmCondominiumTermsAsync(
        Guid leaseId,
        ConfirmLeaseCondominiumTermsRequest request,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ValidateTerms(request);
        if (userId.Length > LeaseCondominiumTerms.ConfirmedByUserIdMaxLength)
            throw TermsInvalid();

        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        if (!CanConfigure(lease.Status))
            throw new DomainRuleException(RentBillingErrorCodes.LeaseNotSigned, "RentLeaseNotSigned");

        await using var transaction = await LockLeaseAsync(leaseId, cancellationToken);
        if (request.SourceDocumentId is { } documentId)
        {
            var documentExists = await db.LeaseDocuments
                .AnyAsync(d => d.Id == documentId && d.LeaseContractId == leaseId, cancellationToken);
            if (!documentExists)
            {
                throw new DomainRuleException(
                    RentBillingErrorCodes.CondominiumSourceNotFound, "RentCondominiumSourceNotFound");
            }
        }

        var now = UtcNow;
        var terms = await db.LeaseCondominiumTerms
            .SingleOrDefaultAsync(t => t.LeaseContractId == leaseId, cancellationToken);
        var previousTerms = ToTerms(terms);
        if (terms is null)
        {
            terms = new LeaseCondominiumTerms
            {
                OrgId = lease.OrgId,
                LeaseContractId = lease.Id,
                CreatedAt = now,
            };
            db.LeaseCondominiumTerms.Add(terms);
        }

        terms.AdvanceMode = request.AdvanceMode;
        terms.AdvanceAmount = request.AdvanceAmount;
        terms.Cadence = request.Cadence;
        terms.SettlementRequired = request.SettlementRequired;
        terms.ProrationRule = string.IsNullOrWhiteSpace(request.ProrationRule) ? null : request.ProrationRule.Trim();
        terms.EffectiveFrom = request.EffectiveFrom;
        terms.EffectiveTo = request.EffectiveTo;
        terms.SourceDocumentId = request.SourceDocumentId;
        terms.ExtractionConfidence = request.ExtractionConfidence;
        terms.ConfirmedAt = now;
        terms.ConfirmedByUserId = userId;
        terms.UpdatedAt = now;

        var schedule = await db.RentSchedules.SingleOrDefaultAsync(s => s.LeaseContractId == leaseId, cancellationToken);
        if (schedule is { IsActive: true }
            && RentInstallmentPlan.IsValidBillingDay(schedule.BillingDayOfMonth)
            && schedule.Amount > 0)
        {
            var entries = await db.RentLedgerEntries
                .Include(e => e.Components)
                .Where(e => e.LeaseContractId == leaseId)
                .ToListAsync(cancellationToken);
            var previousAdvance = previousTerms is null ? 0m : PreviousUniformAdvance(previousTerms, schedule.Cadence);
            var amount = AmountAfterTerms(schedule.Amount, lease.MonthlyRent, schedule.Cadence, previousAdvance, terms);
            if (amount <= 0 || amount > MaxInstallmentAmount || decimal.Round(amount, 2) != amount)
                throw new DomainRuleException(RentBillingErrorCodes.AmountInvalid, "RentAmountInvalid");

            var leaseStart = RomeCalendar.DateInRome(lease.StartDate);
            var leaseEnd = RomeCalendar.DateInRome(lease.CurrentTermEndDate);
            if (leaseEnd >= leaseStart && amount != schedule.Amount)
            {
                schedule.Amount = amount;
                schedule.UpdatedAt = now;
                var plan = RentInstallmentPlan.Build(
                    leaseStart, leaseEnd, schedule.Cadence, schedule.BillingDayOfMonth, amount, schedule.TrackFrom);
                if (plan.Installments.Count > 0)
                    schedule.NextRunDate = plan.Installments[0].DueDate;
                Regenerate(lease, schedule, entries, plan, schedule.TrackFrom, now, terms);
            }
            else
            {
                foreach (var entry in entries.Where(IsRegenerable))
                {
                    ApplyBreakdown(entry, lease.MonthlyRent, schedule.Cadence, terms);
                    entry.UpdatedAt = now;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Condominium terms of lease {LeaseId} confirmed: {AdvanceMode}",
            leaseId,
            request.AdvanceMode);
        return await GetLedgerAsync(leaseId, cancellationToken);
    }

    private static decimal DefaultInstallmentAmount(decimal monthlyRent, RentCadence cadence, LeaseCondominiumTerms? terms) =>
        RentInstallmentPlan.DefaultAmount(monthlyRent, cadence) + ChargeableUniformAdvance(terms, cadence);

    private static decimal AmountAfterTerms(
        decimal current, decimal monthlyRent, RentCadence cadence, decimal previousAdvance, LeaseCondominiumTerms terms)
    {
        var rent = RentInstallmentPlan.DefaultAmount(monthlyRent, cadence);
        var nextAdvance = ChargeableUniformAdvance(terms, cadence);
        return RentLedgerLine.InstallmentTotalAfterTerms(current, rent, previousAdvance, nextAdvance);
    }

    /// <summary>The advance the plan already charged. A clause that does not scale onto the rent cadence charged nothing.</summary>
    private static decimal PreviousUniformAdvance(RentCondominiumTerms terms, RentCadence rentCadence)
    {
        try
        {
            return terms.UniformAdvanceFor(rentCadence);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            return 0m;
        }
    }

    private static decimal ChargeableUniformAdvance(LeaseCondominiumTerms? terms, RentCadence rentCadence)
    {
        if (ToTerms(terms) is not { } snapshot)
            return 0m;

        try
        {
            return snapshot.UniformAdvanceFor(rentCadence);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            throw TermsInvalid();
        }
    }

    private static RentCondominiumTerms? ToTerms(LeaseCondominiumTerms? terms) =>
        terms is null
            ? null
            : new RentCondominiumTerms(terms.AdvanceMode, terms.AdvanceAmount, terms.Cadence, terms.ConfirmedAt is not null);

    private void ApplyBreakdown(RentLedgerEntry entry, decimal monthlyRent, RentCadence cadence, LeaseCondominiumTerms? terms)
    {
        var drafts = RentLedgerLine.ForStoredTotal(
            entry.AmountDue,
            RentInstallmentPlan.DefaultAmount(monthlyRent, cadence),
            ChargeableUniformAdvance(terms, cadence));
        ReplaceComponents(entry, drafts);
    }

    private void ReplaceComponents(RentLedgerEntry entry, IReadOnlyList<RentComponentDraft> drafts)
    {
        RentLedgerLine.EnsureAmountDue(entry.AmountDue, drafts);
        // Delete the previous parts. Clearing the collection would null the required foreign key and the save would fail
        // the next time the plan is written (the installment already has components).
        if (entry.Components.Count > 0)
            db.RentLedgerComponents.RemoveRange(entry.Components.ToList());

        foreach (var draft in drafts)
        {
            // The constructor already sets an id. On an installment EF is tracking, that id is written as an update of a
            // missing row and the confirm fails. An empty id is a new part and has to be inserted.
            var component = new RentLedgerComponent
            {
                Id = Guid.Empty,
                OrgId = entry.OrgId,
                RentLedgerEntry = entry,
                Kind = draft.Kind,
                Amount = draft.Amount,
                CompetenceStart = entry.PeriodStart,
                CompetenceEnd = entry.PeriodEnd,
                Description = Truncate(draft.Description, 500),
            };
            entry.Components.Add(component);
            if (db.Entry(entry).State != EntityState.Detached)
                db.Entry(component).State = EntityState.Added;
        }
    }

    private static LeaseCondominiumTermsView? ToTermsView(LeaseCondominiumTerms? terms) =>
        terms is null
            ? null
            : new LeaseCondominiumTermsView(
                terms.Id,
                terms.AdvanceMode,
                terms.AdvanceAmount,
                terms.Cadence,
                terms.SettlementRequired,
                terms.ProrationRule,
                terms.EffectiveFrom,
                terms.EffectiveTo,
                terms.SourceDocumentId,
                terms.ExtractionConfidence,
                terms.ConfirmedAt,
                terms.ConfirmedByUserId);

    private static void ValidateTerms(ConfirmLeaseCondominiumTermsRequest request)
    {
        if (!Enum.IsDefined(request.AdvanceMode))
            throw TermsInvalid();
        if (request.ProrationRule is { Length: > LeaseCondominiumTerms.ProrationRuleMaxLength })
            throw TermsInvalid();
        if (request.EffectiveFrom is { } from && request.EffectiveTo is { } to && to < from)
            throw TermsInvalid();
        if (request.ExtractionConfidence is { } confidence
            && (confidence < 0m || confidence > 1m || decimal.Round(confidence, 4) != confidence))
            throw TermsInvalid();
        if (request.AdvanceAmount is { } amount
            && (amount <= 0m || amount > MaxInstallmentAmount || decimal.Round(amount, 2) != amount))
            throw TermsInvalid();
        if (request.Cadence is { } cadence && !Enum.IsDefined(cadence))
            throw TermsInvalid();

        switch (request.AdvanceMode)
        {
            case CondominiumAdvanceMode.FixedContractAmount
                when request.AdvanceAmount is null || request.Cadence is null:
            case CondominiumAdvanceMode.BudgetAllocation when request.Cadence is null:
                throw TermsInvalid();
        }
    }

    private static DomainRuleException TermsInvalid() =>
        new(RentBillingErrorCodes.CondominiumTermsInvalid, "RentCondominiumTermsInvalid");
}
