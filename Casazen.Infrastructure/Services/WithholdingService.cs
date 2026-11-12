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
        var evidence = await db.TaxObligationEvidence.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.Decision == TaxDecision.Applicable && e.PeriodYear == year && e.PeriodMonth == month)
            .ToListAsync(cancellationToken);
        if (evidence.Count == 0)
            throw new DomainRuleException(OwnerPayoutErrors.NoObligations, "TaxNoObligations");

        // One F24 section per tax code: evidence of the month under different codes is not merged.
        var codes = evidence.Select(e => e.TaxCode).Distinct(StringComparer.Ordinal).ToList();
        if (codes.Count != 1 || string.IsNullOrWhiteSpace(codes[0]))
            throw new DomainRuleException(OwnerPayoutErrors.F24TaxCodeMixed, "F24TaxCodeMixed");

        var existing = await db.F24Records
            .FirstOrDefaultAsync(f => f.OrgId == orgId && f.ReferenceMonth == month && f.ReferenceYear == year, cancellationToken);
        if (existing is not null && existing.FilingState != TaxFilingState.Generated)
            throw new DomainConflictException(OwnerPayoutErrors.FileAlreadyFiled, "TaxFileAlreadyFiled");

        var record = existing ?? new F24Record { OrgId = orgId, ReferenceMonth = month, ReferenceYear = year, CreatedAt = time.GetUtcNow().UtcDateTime };
        record.TotalAmount = evidence.Sum(e => e.Amount ?? 0m);
        record.TaxCode = codes[0]!;
        var versions = string.Join(", ", evidence.Select(e => e.RuleCode + "@" + e.RuleVersion).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        record.RuleVersion = versions.Length <= 200 ? versions : versions[..200];
        record.DueDate = CalculateDueDate(month, year);
        if (existing is null)
            db.F24Records.Add(record);

        await db.SaveChangesAsync(cancellationToken);
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
