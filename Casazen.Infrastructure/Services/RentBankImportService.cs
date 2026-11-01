using Casazen.Core.Authorization;
using Casazen.Core.BankStatements;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IRentBankImportService"/>
/// <remarks>
/// <para>The file is never kept: it is read from the bytes of the request, twice (to propose, then to confirm), and what stays is, for each
/// confirmed credit, a <see cref="RentBankMovement"/> with the fingerprint of the movement and the hashes of its payer. The amount, the
/// date and the payer of a confirmed credit are read again from the file by the server, so the client only says which credit pays which
/// installment. Logs carry counts and ids: never a name, an IBAN, a reason of payment or the name of the file.</para>
/// <para>An installment is declared paid by <see cref="IRentBillingService.MarkPaidOfflineAsync"/>, the path of a payment the landlord
/// declares by hand, so every rule of the ledger (the lease lock, an online payment in flight, the Stripe PaymentIntent that is canceled,
/// the date) stays in one place. Each confirmation is its own change: a refusal of one never stops the others.</para>
/// <para>The credit is claimed before the installment is paid: its <see cref="RentBankMovement"/> is saved first and the unique index of the
/// fingerprint lets only one of two requests that race for the same credit go on to pay. If the payment is refused the claim is released; if
/// the outcome is unknown (the request is canceled, the database fails) the ledger says whether the installment was paid and the claim stays
/// only if it was. A crash between the two leaves, at worst, a credit that is hidden as imported and a rent that is still to collect, which
/// the landlord sees and declares by hand: never one credit paying two rents.</para>
/// </remarks>
public sealed class RentBankImportService(
    AppDbContext db,
    IRentBillingService rentBilling,
    ILogger<RentBankImportService> logger,
    TimeProvider? timeProvider = null,
    IConfiguration? configuration = null) : IRentBankImportService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // The key of the payer hashes comes from the secret of the signed links; without it nothing is kept of who paid.
    private readonly byte[]? _payerKey = BankPayerHash.DeriveKey(configuration?[MarketingUnsubscribeLinks.SecretVariable]);

    public async Task<RentBankImportPreview> PreviewAsync(
        HostScope scope, byte[] file, BankColumnMapping? mapping, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var statement = BankStatementParser.Parse(file, mapping);
        var credits = ToCredits(scope.OrgId, statement);

        // What was imported before is left out: the same file twice, or two files that overlap, change nothing.
        var known = await KnownFingerprintsAsync(scope.OrgId, credits.Select(c => c.Fingerprint).ToList(), cancellationToken);
        var fresh = credits.Where(c => !known.Contains(c.Fingerprint)).ToList();

        var today = _clock.TodayInRomeAsDateOnly();
        var matching = fresh.Count == 0
            ? new RentBankMatching([], [])
            : await MatchAsync(scope, fresh, today, cancellationToken);

        var names = await TenantNamesAsync(matching, cancellationToken);
        var suggestions = matching.Suggestions
            .Select(s => new RentBankSuggestionView(
                s.Credit.Fingerprint,
                s.Credit.Movement.ValueDate,
                s.Credit.Movement.Amount,
                s.Credit.Movement.Currency,
                s.Credit.Movement.ShortDescription(),
                s.Credit.Movement.CounterpartyName,
                s.Confidence,
                s.Reasons,
                View(s.Installment, names),
                s.Alternatives.Select(a => View(a, names)).ToList()))
            .ToList();
        var unmatched = matching.Unmatched
            .OrderByDescending(c => c.Movement.ValueDate)
            .ThenBy(c => c.Fingerprint, StringComparer.Ordinal)
            .Take(RentBankImportLimits.MaxUnmatchedShown)
            .Select(c => new RentBankCreditView(
                c.Fingerprint,
                c.Movement.ValueDate,
                c.Movement.Amount,
                c.Movement.Currency,
                c.Movement.ShortDescription(),
                c.Movement.CounterpartyName))
            .ToList();

        var summary = new RentBankImportSummary(
            statement.MovementCount,
            credits.Count,
            known.Count,
            suggestions.Count,
            suggestions.Count(s => s.Confidence == RentBankConfidence.High),
            matching.Unmatched.Count);
        logger.LogInformation(
            "Bank statement read as {Format}: {Movements} movements, {Credits} credits, {Known} imported before, {Suggested} suggested ({Confident} sure), {Unmatched} without an installment",
            statement.Format,
            summary.Movements,
            summary.Credits,
            summary.AlreadyImported,
            summary.Suggested,
            summary.Confident,
            summary.Unmatched);
        return new RentBankImportPreview(statement.Format, summary, suggestions, unmatched);
    }

    public async Task<RentBankConfirmation> ConfirmAsync(
        HostScope scope,
        byte[] file,
        BankColumnMapping? mapping,
        IReadOnlyList<RentBankSelection> selections,
        string userId,
        Func<Guid, CancellationToken, Task<bool>> mayChange,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(mayChange);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (selections.Count == 0 || selections.Count > BankStatementLimits.MaxSelections)
            throw new DomainRuleException(BankStatementErrorCodes.SelectionsInvalid, "BankImportSelectionsInvalid");

        var statement = BankStatementParser.Parse(file, mapping);
        var credits = ToCredits(scope.OrgId, statement).ToDictionary(c => c.Fingerprint, StringComparer.Ordinal);
        var known = await KnownFingerprintsAsync(scope.OrgId, selections.Select(s => s.Fingerprint).ToList(), cancellationToken);

        // The installments the landlord chose, read inside the scope: one of another org or of a property out of reach is simply not here.
        var installmentIds = selections.Select(s => s.InstallmentId).Distinct().ToList();
        var installments = await db.RentLedgerEntries
            .AsNoTracking()
            .WithinScope(scope)
            .Where(e => installmentIds.Contains(e.Id))
            .Select(e => new
            {
                e.Id,
                e.LeaseContractId,
                e.LeaseContract.PropertyId,
                e.AmountDue,
                PartiesErased = e.LeaseContract.PartiesAnonymizedAt != null || e.LeaseContract.ErasureRequested,
            })
            .ToDictionaryAsync(e => e.Id, cancellationToken);

        var today = _clock.TodayInRomeAsDateOnly();
        var mayByProperty = new Dictionary<Guid, bool>();
        var confirmed = new List<RentBankConfirmed>();
        var skipped = new List<RentBankSkipped>();
        foreach (var selection in selections)
        {
            RentBankSkipped Skip(string code) => new(selection.Fingerprint, selection.InstallmentId, code);

            if (!credits.TryGetValue(selection.Fingerprint, out var credit))
            {
                skipped.Add(Skip(BankStatementErrorCodes.MovementNotFound));
                continue;
            }

            if (known.Contains(credit.Fingerprint))
            {
                skipped.Add(Skip(BankStatementErrorCodes.MovementAlreadyImported));
                continue;
            }

            // Another org's installment, or one of a property the caller may not handle, answers like one that does not exist.
            if (!installments.TryGetValue(selection.InstallmentId, out var installment))
            {
                skipped.Add(Skip(RentBillingErrorCodes.InstallmentNotFound));
                continue;
            }

            if (!mayByProperty.TryGetValue(installment.PropertyId, out var may))
            {
                may = await mayChange(installment.PropertyId, cancellationToken);
                mayByProperty[installment.PropertyId] = may;
            }

            if (!may)
            {
                skipped.Add(Skip(RentBillingErrorCodes.InstallmentNotFound));
                continue;
            }

            // The amount to the cent and the currency of a rent: what the matcher offers, checked again here for a choice made by hand.
            if (credit.Movement.Amount != installment.AmountDue || credit.Movement.Currency != RentBankMatcher.Currency)
            {
                skipped.Add(Skip(BankStatementErrorCodes.AmountMismatch));
                continue;
            }

            // The credit is claimed before the installment is paid (see the remarks of the class). Hashes of the payer are kept only for
            // a lease whose tenants have not asked to be erased and have not been anonymized.
            var claim = NewMovement(scope.OrgId, installment.LeaseContractId, installment.Id, credit, userId, keepPayer: !installment.PartiesErased);
            if (!await TryClaimAsync(claim, cancellationToken))
            {
                known.Add(credit.Fingerprint);
                skipped.Add(Skip(BankStatementErrorCodes.MovementAlreadyImported));
                continue;
            }

            // A value date after today (a bank may date a credit ahead) is declared paid today: the ledger refuses a future day.
            var paidOn = credit.Movement.ValueDate > today ? today : credit.Movement.ValueDate;
            try
            {
                await rentBilling.MarkPaidOfflineAsync(
                    installment.LeaseContractId,
                    installment.Id,
                    new MarkRentPaidOfflineRequest(paidOn, RentBankImportLimits.OfflineNote),
                    userId,
                    cancellationToken);
            }
            catch (DomainException ex)
            {
                await ReleaseAsync(claim);
                skipped.Add(Skip(ex.Code));
                continue;
            }
            catch (NotFoundException ex)
            {
                await ReleaseAsync(claim);
                skipped.Add(Skip(ex.Code ?? RentBillingErrorCodes.InstallmentNotFound));
                continue;
            }
            catch
            {
                // Neither a refusal of the ledger nor a success: the request was canceled, or the database or Stripe failed. Whether the
                // installment was paid is the ledger's to say, and the claim stays only if it was.
                if (!await WasPaidByTheImportAsync(installment.Id))
                    await ReleaseAsync(claim);
                throw;
            }

            known.Add(credit.Fingerprint);
            confirmed.Add(new RentBankConfirmed(credit.Fingerprint, installment.Id));
        }

        logger.LogInformation(
            "Bank statement confirmed: {Confirmed} of {Requested} installments declared paid, {Skipped} skipped",
            confirmed.Count,
            selections.Count,
            skipped.Count);
        return new RentBankConfirmation(selections.Count, confirmed, skipped);
    }

    // ---------------------------------------------------------------- reading

    private List<RentBankCredit> ToCredits(Guid orgId, ParsedBankStatement statement)
    {
        var fingerprints = BankMovementFingerprint.ForAll(orgId, statement.Credits);
        return statement.Credits
            .Select((movement, index) => new RentBankCredit(
                fingerprints[index],
                movement,
                BankPayerHash.Iban(orgId, movement.CounterpartyIban, _payerKey),
                BankPayerHash.Name(orgId, movement.CounterpartyName, _payerKey)))
            .ToList();
    }

    private async Task<HashSet<string>> KnownFingerprintsAsync(Guid orgId, List<string> fingerprints, CancellationToken cancellationToken)
    {
        if (fingerprints.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var rows = await db.RentBankMovements
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && fingerprints.Contains(m.Fingerprint))
            .Select(m => m.Fingerprint)
            .ToListAsync(cancellationToken);
        return new HashSet<string>(rows, StringComparer.Ordinal);
    }

    private async Task<RentBankMatching> MatchAsync(
        HostScope scope, List<RentBankCredit> credits, DateOnly today, CancellationToken cancellationToken)
    {
        // Only the installments a credit of this file could settle: the amount is one of the amounts, the due date is in the window around
        // the dates of the credits, still to collect, and of a property the caller reaches (in SQL).
        var amounts = credits.Select(c => c.Movement.Amount).Distinct().ToList();
        var days = credits.Select(c => c.Movement.ValueDate > today ? today : c.Movement.ValueDate).ToList();
        var from = days.Min().AddDays(-RentBankMatcher.DaysLate);
        var to = days.Max().AddDays(RentBankMatcher.DaysEarly);
        var rows = await db.RentLedgerEntries
            .AsNoTracking()
            .WithinScope(scope)
            .Where(e => (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed)
                && amounts.Contains(e.AmountDue)
                && e.DueDate >= from
                && e.DueDate <= to)
            .Select(e => new
            {
                e.Id,
                e.LeaseContractId,
                e.LeaseContract.PropertyId,
                PropertyName = e.LeaseContract.Property.Name,
                PropertyAddress = e.LeaseContract.Property.Address,
                e.PeriodStart,
                e.PeriodEnd,
                e.DueDate,
                e.AmountDue,
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return new RentBankMatching([], credits);

        var leaseIds = rows.Select(r => r.LeaseContractId).Distinct().ToList();
        var tenants = (await db.Parties
                .AsNoTracking()
                .Where(p => leaseIds.Contains(p.LeaseContractId) && p.Role == PartyRole.Tenant && p.AnonymizedAt == null)
                .OrderBy(p => p.Position)
                .Select(p => new { p.LeaseContractId, p.FirstName, p.LastName })
                .ToListAsync(cancellationToken))
            .ToLookup(p => p.LeaseContractId, p => new RentBankTenant(p.FirstName, p.LastName));

        var candidates = rows
            .Select(r => new RentBankCandidate(
                r.Id,
                r.LeaseContractId,
                r.PropertyId,
                r.PropertyName,
                r.PropertyAddress,
                r.PeriodStart,
                r.PeriodEnd,
                r.DueDate,
                r.AmountDue,
                tenants[r.LeaseContractId].ToList()))
            .ToList();

        // Who paid these leases before, recognized by the hashes of the IBAN and of the name (never the values).
        var ibanHashes = credits.Select(c => c.IbanHash).OfType<string>().Distinct().ToList();
        var nameHashes = credits.Select(c => c.NameHash).OfType<string>().Distinct().ToList();
        var payers = ibanHashes.Count == 0 && nameHashes.Count == 0
            ? new List<RentBankPayer>()
            : await db.RentBankMovements
                .AsNoTracking()
                .Where(m => m.OrgId == scope.OrgId
                    && leaseIds.Contains(m.LeaseContractId)
                    && ((m.PayerIbanHash != null && ibanHashes.Contains(m.PayerIbanHash))
                        || (m.PayerNameHash != null && nameHashes.Contains(m.PayerNameHash))))
                .Select(m => new RentBankPayer(m.LeaseContractId, m.PayerIbanHash, m.PayerNameHash))
                .ToListAsync(cancellationToken);

        return RentBankMatcher.Match(credits, candidates, payers, today);
    }

    private async Task<Dictionary<Guid, string?>> TenantNamesAsync(RentBankMatching matching, CancellationToken cancellationToken)
    {
        var leaseIds = matching.Suggestions
            .SelectMany(s => s.Alternatives.Append(s.Installment))
            .Select(c => c.LeaseId)
            .Distinct()
            .ToList();
        if (leaseIds.Count == 0)
            return new Dictionary<Guid, string?>();

        var parties = await db.Parties
            .AsNoTracking()
            .Where(p => leaseIds.Contains(p.LeaseContractId) && p.Role == PartyRole.Tenant)
            .OrderBy(p => p.Position)
            .Select(p => new { p.LeaseContractId, p.FirstName, p.LastName, Anonymized = p.AnonymizedAt != null })
            .ToListAsync(cancellationToken);

        // The first tenant of the lease, by name only (D29); none once that tenant was anonymized.
        return parties
            .GroupBy(p => p.LeaseContractId)
            .ToDictionary(
                group => group.Key,
                group => group.First() is { Anonymized: false } first ? $"{first.FirstName} {first.LastName}".Trim() : null);
    }

    private static RentBankInstallment View(RentBankCandidate candidate, Dictionary<Guid, string?> names) => new(
        candidate.InstallmentId,
        candidate.LeaseId,
        candidate.PropertyId,
        candidate.PropertyName,
        names.GetValueOrDefault(candidate.LeaseId),
        candidate.PeriodStart,
        candidate.PeriodEnd,
        candidate.DueDate,
        candidate.Amount,
        RentCharges.Currency.ToUpperInvariant());

    // ---------------------------------------------------------------- claiming

    private RentBankMovement NewMovement(
        Guid orgId, Guid leaseId, Guid installmentId, RentBankCredit credit, string userId, bool keepPayer)
    {
        return new RentBankMovement
        {
            OrgId = orgId,
            LeaseContractId = leaseId,
            RentLedgerEntryId = installmentId,
            Fingerprint = credit.Fingerprint,
            PayerIbanHash = keepPayer ? credit.IbanHash : null,
            PayerNameHash = keepPayer ? credit.NameHash : null,
            ValueDate = credit.Movement.ValueDate,
            Amount = credit.Movement.Amount,
            ConfirmedByUserId = userId,
            ConfirmedAt = _clock.GetUtcNow().UtcDateTime,
        };
    }

    /// <summary>Saves the claim on the credit; false when another request saved one for the same credit first (the unique index of the fingerprint).</summary>
    private async Task<bool> TryClaimAsync(RentBankMovement claim, CancellationToken cancellationToken)
    {
        db.RentBankMovements.Add(claim);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex))
        {
            db.Entry(claim).State = EntityState.Detached;
            logger.LogInformation("Bank movement of installment {InstallmentId} was claimed by another request first", claim.RentLedgerEntryId);
            return false;
        }
    }

    /// <summary>
    /// Takes the claim back when the installment stayed unpaid. It does not use the token of the request (a canceled request must still
    /// leave the credit free), and what a failed payment left staged in the context is dropped first so that it is not saved with the release.
    /// </summary>
    private async Task ReleaseAsync(RentBankMovement claim)
    {
        try
        {
            foreach (var staged in db.ChangeTracker.Entries()
                         .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                         .ToList())
            {
                staged.State = EntityState.Detached;
            }

            db.RentBankMovements.Remove(claim);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            db.Entry(claim).State = EntityState.Detached;
            logger.LogWarning(ex, "Bank movement of installment {InstallmentId} could not be released; the credit stays hidden until it is", claim.RentLedgerEntryId);
        }
    }

    /// <summary>
    /// Did the import itself declare this installment paid? Asked of the ledger, never of the context, which may hold a half-done change.
    /// When it cannot be told the answer is yes: a credit hidden as imported over a rent still to collect is the safe side.
    /// </summary>
    private async Task<bool> WasPaidByTheImportAsync(Guid installmentId)
    {
        try
        {
            return await db.RentLedgerEntries
                .AsNoTracking()
                .AnyAsync(
                    e => e.Id == installmentId
                        && e.Status == RentLedgerStatus.Paid
                        && e.PaidVia == RentPaymentChannel.Offline
                        && e.OfflinePaymentNote == RentBankImportLimits.OfflineNote,
                    CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not tell whether installment {InstallmentId} was paid by the bank import; its credit stays claimed", installmentId);
            return true;
        }
    }
}
