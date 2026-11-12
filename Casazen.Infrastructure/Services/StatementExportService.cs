using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IStatementExportService"/>
/// <remarks>
/// Read-only: the statement, its evidence and the F24 are read with <c>AsNoTracking</c> and nothing is saved, so an export can never create a
/// withholding or change a total (AC-NF110: statement operations never create withholding). The logs carry ids and formats only.
/// </remarks>
public sealed class StatementExportService(
    AppDbContext db,
    IPdfDocumentRenderer pdfRenderer,
    IDocumentTexts texts,
    ILogger<StatementExportService> logger) : IStatementExportService
{
    private const string PdfContentType = "application/pdf";
    private const string CsvContentType = "text/csv; charset=utf-8";

    public async Task<StatementExportFile> ExportStatementAsync(
        Guid statementId,
        Guid orgId,
        StatementExportFormat format,
        string language,
        CancellationToken ct = default)
    {
        var statement = await db.OwnerStatements
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.PropertyOwner)
            .Include(s => s.Property)
            .Include(s => s.Lines)
                .ThenInclude(l => l.Booking)
            .FirstOrDefaultAsync(s => s.Id == statementId && s.OrgId == orgId, ct)
            ?? throw new NotFoundException($"Statement {statementId} was not found")
            {
                Code = StatementExportErrors.StatementNotFound,
                MessageKey = "StatementNotFound",
            };

        if (statement.Status is not (StatementStatus.Approved or StatementStatus.Published))
            throw new DomainRuleException(StatementExportErrors.StatementNotExportable, "StatementNotExportable");

        var data = await EvidenceOfAsync(statement, ct);
        var name = $"rendiconto-{statement.Year.ToString(CultureInfo.InvariantCulture)}-{statement.Month.ToString("00", CultureInfo.InvariantCulture)}-{statement.Id:N}";
        logger.LogInformation("Statement {StatementId} exported as {Format}", statement.Id, format);

        return format == StatementExportFormat.Csv
            ? new StatementExportFile(StatementDocuments.Csv(data, texts, language), CsvContentType, name + ".csv")
            : new StatementExportFile(pdfRenderer.Render(StatementDocuments.Pdf(data, texts, language)), PdfContentType, name + ".pdf");
    }

    public async Task<StatementExportFile> ExportF24Async(Guid orgId, int month, int year, string language, CancellationToken ct = default)
    {
        var record = await db.F24Records
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.OrgId == orgId && f.ReferenceMonth == month && f.ReferenceYear == year, ct)
            ?? throw new NotFoundException($"F24 {year}-{month} was not found")
            {
                Code = StatementExportErrors.F24NotFound,
                MessageKey = "F24NotFound",
            };

        var orgName = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.Name)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        logger.LogInformation("F24 {F24Id} exported as PDF", record.Id);
        var name = $"f24-{year.ToString(CultureInfo.InvariantCulture)}-{month.ToString("00", CultureInfo.InvariantCulture)}.pdf";
        return new StatementExportFile(pdfRenderer.Render(F24Documents.Pdf(record, orgName, texts, language)), PdfContentType, name);
    }

    /// <summary>
    /// What the files print besides the statement, as stored: the number of the owner's current statement version of the month once approved or
    /// published (E06/F05/US02/T02), and the wire transfers that paid the statement with their state. Nothing is computed here.
    /// </summary>
    private async Task<StatementExportData> EvidenceOfAsync(OwnerStatement statement, CancellationToken ct)
    {
        var version = await db.OwnerStatementVersions
            .AsNoTracking()
            .Where(v => v.OrgId == statement.OrgId && v.PropertyOwnerId == statement.PropertyOwnerId && v.PeriodYear == statement.Year
                && v.PeriodMonth == statement.Month
                && (v.Status == OwnerStatementVersionStatus.Approved || v.Status == OwnerStatementVersionStatus.Published))
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => (int?)v.VersionNumber)
            .FirstOrDefaultAsync(ct);

        var wires = await db.WireBatchStatements
            .AsNoTracking()
            .Where(w => w.OrgId == statement.OrgId && w.OwnerStatementId == statement.Id)
            .Select(w => new { w.WireBatchId, w.WireBatch.Status, w.WireBatch.PaidAt })
            .ToListAsync(ct);
        var payouts = wires
            .OrderBy(w => w.PaidAt)
            .Select(w => new StatementPayoutEvidence(
                "WB-" + w.WireBatchId.ToString("N")[..8].ToUpperInvariant(),
                w.PaidAt is { } paidAt ? RomeCalendar.DateInRome(paidAt) : null,
                statement.NetPayable,
                "wire_" + w.Status.ToString().ToLowerInvariant()))
            .ToList();

        var versionIds = await db.OwnerStatementVersions
            .AsNoTracking()
            .Where(v => v.OrgId == statement.OrgId && v.PropertyOwnerId == statement.PropertyOwnerId && v.PeriodYear == statement.Year
                && v.PeriodMonth == statement.Month)
            .Select(v => v.Id)
            .ToListAsync(ct);
        var ownerPayouts = await db.OwnerPayouts
            .AsNoTracking()
            .Where(p => p.OrgId == statement.OrgId && versionIds.Contains(p.OwnerStatementVersionId))
            .OrderBy(p => p.PaidOn)
            .ToListAsync(ct);
        var payoutIds = ownerPayouts.Select(p => p.Id).ToList();
        var decisions = await db.TaxObligationEvidence
            .AsNoTracking()
            .Where(e => e.OrgId == statement.OrgId && payoutIds.Contains(e.OwnerPayoutId))
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(ct);
        string ReferenceOf(OwnerPayout p) => p.Reference ?? "PO-" + p.Id.ToString("N")[..8].ToUpperInvariant();
        payouts.AddRange(ownerPayouts.Select(p => new StatementPayoutEvidence(ReferenceOf(p), p.PaidOn, p.NetAmount, "payout_" + p.Status.ToString().ToLowerInvariant())));
        var withholdings = decisions
            .Select(e => new StatementWithholdingEvidence(
                ReferenceOf(ownerPayouts.First(p => p.Id == e.OwnerPayoutId)),
                e.RuleCode,
                e.RuleVersion,
                e.Decision == TaxDecision.Applicable ? OwnerWithholdingRules.Applicable : "not_applicable",
                e.GrossBase ?? 0m,
                e.Amount ?? 0m))
            .ToList();

        return new StatementExportData(statement, version, payouts, withholdings);
    }
}
