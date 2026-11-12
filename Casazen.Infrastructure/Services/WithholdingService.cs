using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The F24 data of the withholding (ADR-018 amended, E06/F05/US02/T03, AC-NF110-4, AC-NF110-5): built only from the applicable withholding evidence of
/// the confirmed payouts paid in the month (and their reversals), with the tax code and the rule versions of that evidence. No rate or tax code is
/// written here, and no statement operation creates withholding any more (the decision is taken at the payout, <see cref="OwnerPayoutService"/>).
/// </summary>
public sealed class WithholdingService(AppDbContext db, TimeProvider time) : IWithholdingService
{
    /// <inheritdoc />
    public async Task<F24Record> GenerateF24Async(
        Guid orgId,
        int month,
        int year,
        CancellationToken cancellationToken = default)
    {
        // One at a time per org (the lock of the owner accounting): two requests for the same month never make two records, and a record is rebuilt from the
        // evidence as it is when the lock is held.
        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.OwnerAccounting, orgId.ToString("N")));

        // Only the obligations of the Italian withholding are filed on an F24 (the evidence of a Swiss or any other jurisdiction never is), in euro.
        var evidence = await OwnerPayoutService.F24EvidenceQuery(db, orgId)
            .Where(e => e.PeriodYear == year && e.PeriodMonth == month)
            .ToListAsync(cancellationToken);
        var existing = await db.F24Records
            .FirstOrDefaultAsync(f => f.OrgId == orgId && f.ReferenceMonth == month && f.ReferenceYear == year, cancellationToken);
        if (existing is not null && existing.FilingState != TaxFilingState.Generated)
            throw new DomainConflictException(OwnerPayoutErrors.FileAlreadyFiled, "TaxFileAlreadyFiled");

        // A month whose obligations net to nothing (a payout and its reversal, or only a reversal) has nothing to file: never a zero or negative F24, and a
        // record only generated is dropped.
        var total = evidence.Sum(e => e.Amount ?? 0m);
        if (evidence.Count == 0 || total <= 0m)
        {
            if (existing is not null)
            {
                db.F24Records.Remove(existing);
                await db.SaveChangesAsync(cancellationToken);
                if (tx is not null)
                    await tx.CommitAsync(cancellationToken);
            }

            throw new DomainRuleException(OwnerPayoutErrors.NoObligations, "TaxNoObligations");
        }

        // One F24 section per tax code: evidence of the month under different codes is not merged.
        var codes = evidence.Select(e => e.TaxCode).Distinct(StringComparer.Ordinal).ToList();
        if (codes.Count != 1 || string.IsNullOrWhiteSpace(codes[0]))
            throw new DomainRuleException(OwnerPayoutErrors.F24TaxCodeMixed, "F24TaxCodeMixed");

        var record = existing ?? new F24Record { OrgId = orgId, ReferenceMonth = month, ReferenceYear = year, CreatedAt = time.GetUtcNow().UtcDateTime };
        record.TotalAmount = total;
        record.TaxCode = codes[0]!;
        var versions = string.Join(", ", evidence.Select(e => e.RuleCode + "@" + e.RuleVersion).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        record.RuleVersion = versions.Length <= 200 ? versions : versions[..200];
        record.DueDate = CalculateDueDate(month, year);
        if (existing is null)
            db.F24Records.Add(record);

        await db.SaveChangesAsync(cancellationToken);
        if (tx is not null)
            await tx.CommitAsync(cancellationToken);
        return record;
    }

    /// <summary>The F24 due date: the 16th of the month after the reference month (ADR-018-R8).</summary>
    private static DateTime CalculateDueDate(int month, int year)
    {
        var nextMonth = month == 12 ? 1 : month + 1;
        var nextYear = month == 12 ? year + 1 : year;
        return new DateTime(nextYear, nextMonth, 16, 0, 0, 0, DateTimeKind.Utc);
    }
}
