using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IFiscalNumberingService"/>
/// <remarks>
/// The counter row (<see cref="FiscalNumberSequence"/>) is read and incremented under <c>pg_advisory_xact_lock(FiscalNumbering, "{orgId:N}:{series}")</c>,
/// taken in the caller's transaction (<see cref="PostgresAdvisoryLocks.BeginLockedTransactionAsync"/> joins it): a second issue of the same org and
/// series waits for the first to commit or roll back and then reads what it left (READ COMMITTED), so numbers are never given twice and a rolled-back
/// one is given again. On PostgreSQL a call without a transaction is a programming error (the number would be committed without its document).
/// </remarks>
public sealed class FiscalNumberingService(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<FiscalNumberingService> logger) : IFiscalNumberingService
{
    public async Task<FiscalNumber> ReserveNextAsync(Guid orgId, string series, CancellationToken ct = default)
    {
        var normalized = series?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > FiscalNumberFormat.SeriesMaxLength)
            throw new DomainRuleException(FiscalNumberingErrors.SeriesTooLong, "FiscalSeriesTooLong", FiscalNumberFormat.SeriesMaxLength);

        if (PostgresAdvisoryLocks.IsSupported(db) && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A fiscal number is reserved in the transaction that saves its document: open it first.");

        // Joins the caller's transaction (null is returned and nobody commits here).
        await using var joined = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.FiscalNumbering, $"{orgId:N}:{normalized}"));

        var counter = await db.FiscalNumberSequences
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.Series == normalized, ct);
        if (counter is null)
        {
            var floor = await HighestUsedAsync(orgId, normalized, ct);
            counter = new FiscalNumberSequence { OrgId = orgId, Series = normalized, LastNumber = floor };
            db.FiscalNumberSequences.Add(counter);
            if (floor > 0)
                logger.LogInformation("Org {OrgId}: series {Series} starts above the highest number already used ({Floor})", orgId, normalized, floor);
        }

        counter.LastNumber++;
        counter.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        var display = FiscalNumberFormat.Display(normalized, counter.LastNumber);
        return new FiscalNumber(normalized, counter.LastNumber, display);
    }

    /// <summary>
    /// The highest fiscal number already used in the org and series: the numbered documents and the legacy agency invoices (whose number is text),
    /// so the counter never gives one of them again. It enables nothing: no owner invoice is created from it.
    /// </summary>
    private async Task<int> HighestUsedAsync(Guid orgId, string series, CancellationToken ct)
    {
        var documents = await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(d => d.OrgId == orgId && d.Series == series && d.SequenceNumber != null)
            .MaxAsync(d => d.SequenceNumber, ct) ?? 0;

        var legacy = await db.AgencyInvoices
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(i => i.OrgId == orgId && i.Series == series)
            .Select(i => i.InvoiceNumber)
            .ToListAsync(ct);
        var legacyMax = legacy
            .Select(number => int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();

        return Math.Max(documents, legacyMax);
    }
}
