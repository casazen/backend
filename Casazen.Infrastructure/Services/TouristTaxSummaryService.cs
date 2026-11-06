using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ITouristTaxSummaryService" />
/// <remarks>
/// <para><b>Owed</b> is the tourist tax report of the fiscal area as it is (<see cref="IFiscalReportingService.GetTouristTaxReportAsync"/>):
/// the amounts the engine recorded on the confirmed stays with check-in in the period, per comune, and the stays that have none counted
/// apart. Nothing is recomputed and a missing amount is never a zero. Only the properties in Italy count (<see cref="TouristTaxComune.IsItalianCountry"/>,
/// the rule of the rates): the report groups the stays by the name of the comune alone, and a house in Switzerland must not fall in the row of
/// an Italian comune of the same name, nor be paid in euros. <b>Paid</b> is the sum of the payments
/// (<see cref="TouristTaxRemittance"/>) of the comune whose period lies <i>inside</i> the period asked, so a payment is counted by the
/// period it declares, not by the day it was made, and the figures of a period are the same whenever they are read.</para>
/// <para><b>Cents.</b> Every amount is turned into whole cents once (<see cref="PaymentCashRules.ToCents"/>); owed, paid and balance are
/// worked out on the cents and turned back to euros at the end, so the balance is always exactly the difference of the two.</para>
/// <para><b>Reach.</b> A payment belongs to the org and to no property. A caller who reaches every property of the org sees, records, opens
/// and deletes the payments of the org. One who reaches only some properties gets the summary of what is owed on those properties and
/// nothing about the payments (<see cref="TouristTaxSummary.PaymentsHidden"/>): the payments of a comune cover properties it does not
/// reach, and a balance or a status worked out against them would be false. It cannot record, delete or open one (403). The properties are
/// looked up with <c>IgnoreQueryFilters([SoftDeleteQueryFilter])</c>, as the report does: the history of a deleted property stays reachable.</para>
/// </remarks>
public sealed class TouristTaxSummaryService(
    AppDbContext db,
    IFiscalReportingService fiscalReporting,
    IFileStorage storage,
    ILogger<TouristTaxSummaryService> logger,
    TimeProvider? timeProvider = null) : ITouristTaxSummaryService
{
    private const int ReadChunkBytes = 81920;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<TouristTaxSummary> GetSummaryAsync(
        HostScope scope,
        FiscalReportPeriod period,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(period);
        if (!TouristTaxSummaryRules.IsSummaryPeriod(period))
            throw new FiscalValidationException("fiscal_report_period_invalid", "FiscalReportPeriodInvalid");

        // Owed: the report of the fiscal area, as it is (it limits the stays to the properties of the scope itself).
        var report = await fiscalReporting.GetTouristTaxReportAsync(scope, period, cancellationToken);
        var reach = await ReachAsync(scope, cancellationToken);

        // Paid: the payments belong to the org and to no property, so only a caller who reaches the whole org sees them. One who reaches only
        // some properties gets what is owed on those and nothing about the payments: they cover properties it does not reach, so a balance
        // or a status against them would be false.
        var paymentsHidden = !scope.IsOrgWide;
        List<TouristTaxRemittance> payments = paymentsHidden ? [] : await PaymentsInAsync(scope, period, cancellationToken);

        // Owed: the stay lines of the report, the amounts the engine recorded as they are, without those of a property in another country. The
        // report groups the stays by the name of the comune alone, so a house in Switzerland (CHF-MONEY) would fall in the row of an Italian
        // comune of the same name, or in a row of its own with an owed amount of zero, and euros could be paid for it.
        var comuni = new Dictionary<string, ComuneBuilder>(StringComparer.Ordinal);
        foreach (var stay in report.Stays.Where(s => !reach.ForeignPropertyIds.Contains(s.PropertyId)))
        {
            var comune = BuilderOf(comuni, TouristTaxSummaryRules.ComuneKeyOf(stay.Comune), stay.Comune);
            comune.Stays++;
            comune.Nights += stay.Nights;
            comune.Guests += stay.Guests;
            comune.Owed += stay.Amount;
            if (stay.Amount <= 0)
                comune.StaysWithoutAmount++;
        }

        // Paid: the payments inside the period. A comune with payments and no stays in the period is a row of its own.
        foreach (var payment in payments)
        {
            var comune = BuilderOf(comuni, payment.ComuneKey, payment.Comune);
            comune.PaidCents += PaymentCashRules.ToCents(payment.PaidAmount);
            comune.Remittances.Add(ToLine(payment));
        }

        var summaries = comuni
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Value.ToSummary(paymentsHidden))
            .ToList();
        var builders = comuni.Values.ToList();
        var owedCents = builders.Sum(c => c.OwedCents);
        var paidCents = builders.Sum(c => c.PaidCents);

        return new TouristTaxSummary(
            period,
            summaries,
            new TouristTaxSummaryTotals(
                builders.Sum(c => c.Stays),
                builders.Sum(c => c.Nights),
                builders.Sum(c => c.Guests),
                TouristTaxSummaryRules.FromCents(owedCents),
                builders.Sum(c => c.StaysWithoutAmount),
                paymentsHidden ? null : TouristTaxSummaryRules.FromCents(paidCents),
                paymentsHidden ? null : TouristTaxSummaryRules.FromCents(owedCents - paidCents)),
            paymentsHidden);
    }

    public async Task<TouristTaxRemittanceLine> RecordRemittanceAsync(
        HostScope scope,
        RecordRemittanceRequest request,
        Stream? receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RecordedByUserId);
        RequireWholeOrg(scope);

        // What needs nothing from the database or the file first.
        var comune = TouristTaxSummaryRules.CleanText(request.Comune);
        var comuneKey = TouristTaxSummaryRules.ComuneKeyOf(comune);
        if (comune is null || comune.Length > TouristTaxRemittance.ComuneMaxLength || comuneKey.Length == 0)
            throw new DomainRuleException(TouristTaxErrorCodes.ComuneUnknown, "TouristTaxComuneUnknown");

        if (!TouristTaxSummaryRules.IsValidAmount(request.PaidAmount))
            throw new DomainRuleException(TouristTaxErrorCodes.AmountInvalid, "TouristTaxAmountInvalid");

        if (!TouristTaxSummaryRules.IsValidRemittancePeriod(request.PeriodFrom, request.PeriodTo))
            throw new DomainRuleException(TouristTaxErrorCodes.PeriodInvalid, "TouristTaxPeriodInvalid");

        if (request.PaidOn > _clock.TodayInRomeAsDateOnly())
            throw new DomainRuleException(TouristTaxErrorCodes.DateInFuture, "TouristTaxDateInFuture");

        var reference = TouristTaxSummaryRules.CleanText(request.Reference);
        var notes = TouristTaxSummaryRules.CleanText(request.Notes);
        if (reference?.Length > TouristTaxRemittance.PaymentReferenceMaxLength || notes?.Length > TouristTaxRemittance.NotesMaxLength)
        {
            throw new DomainRuleException(
                TouristTaxErrorCodes.TextTooLong,
                "TouristTaxTextTooLong",
                TouristTaxRemittance.PaymentReferenceMaxLength,
                TouristTaxRemittance.NotesMaxLength);
        }

        // The file is read to the end and judged on its content before anything is stored.
        await using var file = receipt is null ? null : await ReadReceiptAsync(receipt, cancellationToken);
        if (receipt is not null && file is null)
            throw ReceiptInvalid();

        // The comune has to be one of the properties in Italy: a payment for a comune the org has nothing in, or only a house abroad, is a mistake.
        if (!(await ReachAsync(scope, cancellationToken)).ComuneKeys.Contains(comuneKey))
            throw new DomainRuleException(TouristTaxErrorCodes.ComuneUnknown, "TouristTaxComuneUnknown");

        var id = Guid.NewGuid();
        var now = UtcNow();
        var remittance = new TouristTaxRemittance
        {
            Id = id,
            OrgId = scope.OrgId,
            Comune = comune,
            ComuneKey = comuneKey,
            PeriodFrom = request.PeriodFrom,
            PeriodTo = request.PeriodTo,
            PaidAmount = decimal.Round(request.PaidAmount, 2, MidpointRounding.AwayFromZero),
            PaidOn = request.PaidOn,
            PaymentReference = reference,
            Notes = notes,
            RecordedByUserId = request.RecordedByUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // The receipt first, in the private bucket: a failed upload changes nothing, and a failed save removes the file.
        string? key = null;
        if (file is not null)
        {
            key = StorageKeys.TouristTaxReceipt(scope.OrgId, id, StorageKeys.NewFileName(file.Kind.Extension));
            await storage.PutAsync(StorageBucket.Private, key, file.Content, file.Kind.ContentType, cancellationToken);
            remittance.ReceiptStorageKey = key;
            remittance.ReceiptFileName = TouristTaxSummaryRules.ReceiptDisplayName(request.ReceiptFileName, file.Kind);
            remittance.ReceiptContentType = file.Kind.ContentType;
            remittance.ReceiptSizeBytes = file.Content.Length;
        }

        try
        {
            db.TouristTaxRemittances.Add(remittance);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (key is not null)
                await TryDeleteFileAsync(key);
            throw;
        }

        logger.LogInformation(
            "Tourist tax payment recorded. RemittanceId={RemittanceId} OrgId={OrgId} HasReceipt={HasReceipt}",
            id,
            scope.OrgId,
            key is not null);
        return ToLine(remittance);
    }

    public async Task DeleteRemittanceAsync(HostScope scope, Guid remittanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireWholeOrg(scope);

        var remittance = await LoadAsync(scope, remittanceId, cancellationToken);
        var key = remittance.ReceiptStorageKey;

        // The row first: a file that could not be removed is an orphan nobody can reach, a row pointing to a missing file is a broken payment.
        db.TouristTaxRemittances.Remove(remittance);
        await db.SaveChangesAsync(cancellationToken);
        if (key is not null)
            await TryDeleteFileAsync(key);

        logger.LogInformation("Tourist tax payment deleted. RemittanceId={RemittanceId} OrgId={OrgId}", remittanceId, scope.OrgId);
    }

    public async Task<TouristTaxReceiptFile> OpenReceiptAsync(HostScope scope, Guid remittanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireWholeOrg(scope);

        var remittance = await LoadAsync(scope, remittanceId, cancellationToken);
        var key = remittance.ReceiptStorageKey;
        if (key is null || !StorageKeys.IsValid(key))
            throw ReceiptNotFound();

        var content = await storage.OpenReadAsync(StorageBucket.Private, key, cancellationToken);
        if (content is null)
        {
            logger.LogWarning("Stored tourist tax receipt missing. RemittanceId={RemittanceId} OrgId={OrgId}", remittanceId, scope.OrgId);
            throw ReceiptNotFound();
        }

        return new TouristTaxReceiptFile(
            content,
            remittance.ReceiptFileName ?? Path.GetFileName(key),
            remittance.ReceiptContentType ?? StorageKeys.ContentTypeFor(key));
    }

    // ---------------------------------------------------------------- reads

    /// <summary>
    /// The payments of the org whose period lies inside <paramref name="period"/>, oldest first. All of the org's: only for a caller who
    /// reaches the whole org (a payment belongs to the org, not to a property).
    /// </summary>
    private async Task<List<TouristTaxRemittance>> PaymentsInAsync(HostScope scope, FiscalReportPeriod period, CancellationToken cancellationToken)
    {
        return await db.TouristTaxRemittances.AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId && r.PeriodFrom >= period.From && r.PeriodTo <= period.To)
            .OrderBy(r => r.PeriodFrom)
            .ThenBy(r => r.PeriodTo)
            .ThenBy(r => r.PaidOn)
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The properties the scope reaches, the deleted ones too (as the report), narrowed by <c>InScope</c> for a caller who reaches only some:
    /// the comuni of those in Italy, as keys (<see cref="TouristTaxSummaryRules.ComuneKeyOf"/>; a property with no city has no comune), and the
    /// ids of those in another country. The rule of the country is the one of the tourist tax rates
    /// (<see cref="TouristTaxComune.IsItalianCountry"/>): a house abroad is never taken for an Italian comune of the same name.
    /// </summary>
    private async Task<Reach> ReachAsync(HostScope scope, CancellationToken cancellationToken)
    {
        var properties = await db.Properties.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.OrgId == scope.OrgId)
            .InScope(scope)
            .Select(p => new { p.Id, p.City, p.Country })
            .ToListAsync(cancellationToken);

        var comuneKeys = properties
            .Where(p => TouristTaxComune.IsItalianCountry(p.Country))
            .Select(p => TouristTaxSummaryRules.ComuneKeyOf(p.City))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var foreignPropertyIds = properties
            .Where(p => !TouristTaxComune.IsItalianCountry(p.Country))
            .Select(p => p.Id)
            .ToHashSet();
        return new Reach(comuneKeys, foreignPropertyIds);
    }

    /// <summary>A payment of the caller's org; anything else, another org's payment included, is a 404.</summary>
    private async Task<TouristTaxRemittance> LoadAsync(HostScope scope, Guid remittanceId, CancellationToken cancellationToken)
    {
        var remittance = await db.TouristTaxRemittances
            .FirstOrDefaultAsync(r => r.Id == remittanceId && r.OrgId == scope.OrgId, cancellationToken);
        return remittance ?? throw RemittanceNotFound();
    }

    /// <summary>
    /// A payment belongs to the org and to no property, so recording one, deleting one and opening its receipt need a scope that reaches the
    /// whole org: 403 for a caller who reaches only some properties, before anything is read or stored.
    /// </summary>
    private static void RequireWholeOrg(HostScope scope)
    {
        if (!scope.IsOrgWide)
            throw new DomainForbiddenException(TouristTaxErrorCodes.PaymentsNeedWholeOrg, "TouristTaxPaymentsNeedWholeOrg");
    }

    // ---------------------------------------------------------------- the receipt

    /// <summary>
    /// The whole content when it is a PDF, a JPEG or a PNG (by its first bytes) of at most
    /// <see cref="TouristTaxRemittanceLimits.MaxReceiptBytes"/>, positioned at the start; null otherwise. Nothing is trusted about the size:
    /// the content is counted while it is read.
    /// </summary>
    private static async Task<ReceiptContent?> ReadReceiptAsync(Stream content, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > TouristTaxRemittanceLimits.MaxReceiptBytes)
            {
                await buffer.DisposeAsync();
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        var kind = buffer.Length == 0
            ? null
            : TouristTaxReceiptRules.Sniff(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (kind is null)
        {
            await buffer.DisposeAsync();
            return null;
        }

        buffer.Position = 0;
        return new ReceiptContent(buffer, kind);
    }

    private async Task TryDeleteFileAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(StorageBucket.Private, key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete a tourist tax receipt from the private bucket");
        }
    }

    // ---------------------------------------------------------------- shapes

    private static TouristTaxRemittanceLine ToLine(TouristTaxRemittance remittance) => new(
        remittance.Id,
        remittance.Comune,
        remittance.PeriodFrom,
        remittance.PeriodTo,
        remittance.PaidAmount,
        remittance.PaidOn,
        remittance.PaymentReference,
        remittance.Notes,
        remittance.ReceiptStorageKey is not null);

    private static ComuneBuilder BuilderOf(Dictionary<string, ComuneBuilder> comuni, string key, string name)
    {
        if (!comuni.TryGetValue(key, out var builder))
        {
            builder = new ComuneBuilder(name);
            comuni[key] = builder;
        }

        return builder;
    }

    private static DomainRuleException ReceiptInvalid() =>
        new(TouristTaxErrorCodes.ReceiptInvalid, "TouristTaxReceiptInvalid", TouristTaxRemittanceLimits.MaxReceiptBytes / (1024 * 1024));

    private static NotFoundException RemittanceNotFound() =>
        new("Tourist tax payment not found.")
        {
            Code = TouristTaxErrorCodes.RemittanceNotFound,
            MessageKey = "TouristTaxRemittanceNotFound",
        };

    private static NotFoundException ReceiptNotFound() =>
        new("Tourist tax receipt not found.")
        {
            Code = TouristTaxErrorCodes.ReceiptNotFound,
            MessageKey = "TouristTaxReceiptNotFound",
        };

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>What a scope reaches: the comuni of its properties in Italy (as keys) and the ids of its properties in another country.</summary>
    private sealed record Reach(HashSet<string> ComuneKeys, HashSet<Guid> ForeignPropertyIds);

    /// <summary>A receipt that was read and checked: its bytes, positioned at the start, and what it is.</summary>
    private sealed class ReceiptContent(MemoryStream content, TouristTaxReceiptKind kind) : IAsyncDisposable
    {
        public MemoryStream Content { get; } = content;

        public TouristTaxReceiptKind Kind { get; } = kind;

        public ValueTask DisposeAsync() => Content.DisposeAsync();
    }

    /// <summary>The cents of a comune while the summary is built.</summary>
    private sealed class ComuneBuilder(string name)
    {
        public string Name { get; } = name;

        public int Stays { get; set; }

        public int Nights { get; set; }

        public int Guests { get; set; }

        public int StaysWithoutAmount { get; set; }

        /// <summary>What the stays owe: the sum of the amounts the engine recorded on them.</summary>
        public decimal Owed { get; set; }

        /// <summary><see cref="Owed"/> in whole cents, turned once.</summary>
        public long OwedCents => PaymentCashRules.ToCents(Owed);

        public long PaidCents { get; set; }

        public List<TouristTaxRemittanceLine> Remittances { get; } = [];

        /// <summary>The comune as the summary shows it; with the payments hidden, only the owed side (nothing about what was paid).</summary>
        public ComuneTaxSummary ToSummary(bool paymentsHidden)
        {
            return new ComuneTaxSummary(
                Name,
                Stays,
                Nights,
                Guests,
                TouristTaxSummaryRules.FromCents(OwedCents),
                StaysWithoutAmount,
                paymentsHidden ? null : TouristTaxSummaryRules.FromCents(PaidCents),
                paymentsHidden ? null : TouristTaxSummaryRules.FromCents(OwedCents - PaidCents),
                paymentsHidden ? null : TouristTaxSummaryRules.StatusOf(OwedCents, PaidCents),
                paymentsHidden ? null : Remittances);
        }
    }
}
