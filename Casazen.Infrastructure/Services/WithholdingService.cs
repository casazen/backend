using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The F24 data of the withholding (ADR-018 amended, E06/F05/US02/T03, AC-NF110-4, AC-NF110-5): built only from the applicable withholding evidence of
/// the confirmed payouts paid in the month (and their reversals), with the tax code and the rule versions of that evidence. No rate or tax code is
/// written here, and no statement operation creates withholding any more (the decision is taken at the payout, <see cref="OwnerPayoutService"/>). The data of a
/// month is exported with its breakdown per owner and payout (<see cref="GetF24DataAsync"/>, E18/F01/US01/T02): the same evidence, the same euro.
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

    /// <inheritdoc />
    /// <remarks>
    /// The lines are the evidence of the month grouped by tax code (a month with two codes has two lines, never one merged); the breakdown is one row for each
    /// decision and each reversal, with its owner and its payout, by owner, then day and time. Every query names the org; the owner is named as the wire names it
    /// (the legal name, else the first and last name). Nothing is written.
    /// </remarks>
    public async Task<F24MonthData> GetF24DataAsync(Guid orgId, int year, int month, CancellationToken cancellationToken = default)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
            throw new DomainRuleException(OwnerAccountingErrors.PeriodInvalid, "OwnerPeriodInvalid");

        var evidence = await OwnerPayoutService.F24EvidenceQuery(db, orgId)
            .Where(e => e.PeriodYear == year && e.PeriodMonth == month)
            .ToListAsync(cancellationToken);
        if (evidence.Count == 0)
            throw new DomainRuleException(OwnerPayoutErrors.NoObligations, "TaxNoObligations");

        var payoutIds = evidence.Select(e => e.OwnerPayoutId).Distinct().ToList();
        var payouts = await db.OwnerPayouts.AsNoTracking()
            .Where(p => p.OrgId == orgId && payoutIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Reference, p.Currency, p.GrossAmount, p.WithholdingAmount, p.NetAmount })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var ownerIds = evidence.Select(e => e.PropertyOwnerId).Distinct().ToList();
        var owners = await db.PropertyOwners.AsNoTracking()
            .Where(o => o.OrgId == orgId && ownerIds.Contains(o.Id))
            .Select(o => new { o.Id, o.FirstName, o.LastName, o.LegalName })
            .ToDictionaryAsync(o => o.Id, o => string.IsNullOrWhiteSpace(o.LegalName) ? $"{o.FirstName} {o.LastName}".Trim() : o.LegalName, cancellationToken);

        var due = F24Dates.DueDateOf(year, month);
        var lines = evidence
            .GroupBy(e => e.TaxCode ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new F24TaxLine(g.Key, month, year, g.Sum(e => e.Amount ?? 0m), due, g.Count()))
            .ToList();

        var breakdown = evidence
            .Select(e =>
            {
                // A reversal carries the opposite amounts of its decision, and so do the figures of its payout: every column adds up to what the month is.
                var sign = e.ReversesEvidenceId is null ? 1m : -1m;
                var payout = payouts.GetValueOrDefault(e.OwnerPayoutId);
                return new F24PayoutLine(
                    e.PropertyOwnerId,
                    owners.GetValueOrDefault(e.PropertyOwnerId) ?? string.Empty,
                    e.OwnerPayoutId,
                    payout?.Reference,
                    e.PaidOn,
                    e.ReversesEvidenceId is not null,
                    e.TaxCode ?? string.Empty,
                    e.RuleCode + "@" + e.RuleVersion,
                    e.GrossBase ?? 0m,
                    e.RatePercent,
                    e.Amount ?? 0m,
                    payout?.Currency ?? string.Empty,
                    sign * (payout?.GrossAmount ?? 0m),
                    sign * (payout?.WithholdingAmount ?? 0m),
                    sign * (payout?.NetAmount ?? 0m));
            })
            .OrderBy(l => l.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.OwnerId)
            .ThenBy(l => l.PaidOn)
            .ThenBy(l => l.PayoutId)
            .ThenBy(l => l.Reversal)
            .ToList();

        return new F24MonthData(year, month, lines, breakdown);
    }

    /// <inheritdoc />
    /// <remarks>Reads the row of the year of the org, decrypts and parses its payload; the stored data is what the accountant reviewed, so nothing here builds or changes it.</remarks>
    public Task<StoredCuDataset> GetCuDatasetAsync(Guid orgId, int year, CancellationToken cancellationToken = default) =>
        ReadCuDatasetAsync(db, orgId, year, cancellationToken);

    /// <summary>The stored annual data of the year with its records: the one reader of the exports and of the draft CU of an owner.</summary>
    internal static async Task<StoredCuDataset> ReadCuDatasetAsync(AppDbContext db, Guid orgId, int year, CancellationToken cancellationToken)
    {
        RequireYear(year);

        var dataset = await db.TaxReportingDatasets.AsNoTracking()
            .FirstOrDefaultAsync(d => d.OrgId == orgId && d.Kind == TaxReportingDataset.CertificazioneUnica && d.Year == year, cancellationToken)
            ?? throw new NotFoundException($"Tax dataset of {year} was not found") { Code = OwnerPayoutErrors.DatasetNotFound, MessageKey = "TaxDatasetNotFound" };
        var records = CuDatasetPayload.Parse(dataset.PayloadJson);
        if (!CuDatasetPayload.IsDetailed(records))
            throw new DomainRuleException(TaxReportingErrors.CuDatasetOutdated, "CuDatasetOutdated");

        return new StoredCuDataset(dataset, records);
    }

    /// <inheritdoc />
    /// <remarks>
    /// One line for each month and tax code of the evidence of the year (a month with two codes has two lines, never one merged), with the F24 record of the month when it has that code.
    /// An F24 that was never generated, or one not marked as paid, has its columns empty: the 770 is told what was withheld, and never that something was paid that nobody recorded.
    /// </remarks>
    public async Task<Withholding770Data> Get770DataAsync(Guid orgId, int year, CancellationToken cancellationToken = default)
    {
        RequireYear(year);

        var evidence = await OwnerPayoutService.F24EvidenceQuery(db, orgId)
            .Where(e => e.PeriodYear == year)
            .ToListAsync(cancellationToken);
        if (evidence.Count == 0)
            throw new DomainRuleException(OwnerPayoutErrors.NoObligations, "TaxNoObligations");

        var records = await db.F24Records.AsNoTracking()
            .Where(f => f.OrgId == orgId && f.ReferenceYear == year)
            .ToListAsync(cancellationToken);
        var lines = evidence
            .GroupBy(e => (e.PeriodMonth, TaxCode: e.TaxCode ?? string.Empty))
            .OrderBy(g => g.Key.PeriodMonth)
            .ThenBy(g => g.Key.TaxCode, StringComparer.Ordinal)
            .Select(g =>
            {
                var f24 = records.FirstOrDefault(f => f.ReferenceMonth == g.Key.PeriodMonth && string.Equals(f.TaxCode, g.Key.TaxCode, StringComparison.Ordinal));
                return new Withholding770Line(
                    g.Key.PeriodMonth,
                    g.Key.TaxCode,
                    g.Sum(e => e.GrossBase ?? 0m),
                    g.Sum(e => e.Amount ?? 0m),
                    g.Count(),
                    f24?.TotalAmount,
                    f24 is null ? null : DateOnly.FromDateTime(f24.DueDate),
                    f24?.PaidAt is { } paid ? RomeCalendar.DateInRome(paid) : null,
                    f24?.FilingState,
                    f24?.HandoffReference,
                    f24?.ReceiptReference);
            })
            .ToList();
        return new Withholding770Data(year, lines);
    }

    /// <inheritdoc />
    public async Task<ContractsCommunication> GetContractsCommunicationAsync(Guid orgId, int year, CancellationToken cancellationToken = default)
    {
        RequireYear(year);

        return await ContractsCommunicationBuilder.BuildAsync(db, orgId, year, cancellationToken);
    }

    private static void RequireYear(int year)
    {
        if (year is < 2000 or > 2100)
            throw new DomainRuleException(OwnerAccountingErrors.PeriodInvalid, "OwnerPeriodInvalid");
    }

    /// <summary>The F24 due date: the 16th of the month after the reference month (ADR-018-R8).</summary>
    private static DateTime CalculateDueDate(int month, int year) =>
        F24Dates.DueDateOf(year, month).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
