using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerPayoutService"/>
/// <remarks>
/// A confirmed payout posts its lines to the owner ledger in the month of the payment (the net paid and the withholding kept, both debits that settle
/// what was owed) and makes that month dirty when it has a version. It runs under the lock of the owner accounting of the org
/// (<see cref="PostgresAdvisoryLocks.Scope.OwnerAccounting"/>), so a retry that races its first call finds it.
/// </remarks>
public sealed class OwnerPayoutService(
    AppDbContext db,
    IOwnerFiscalProfileService fiscal,
    TimeProvider time,
    ILogger<OwnerPayoutService> logger) : IOwnerPayoutService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<OwnerPayoutResult> ConfirmPayoutAsync(Guid orgId, OwnerPayoutCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = command.IdempotencyKey?.Trim() ?? string.Empty;
        var currency = command.Currency?.Trim().ToUpperInvariant() ?? string.Empty;
        Require(key.Length is > 0 and <= OwnerPayout.KeyMaxLength, "idempotencyKey");
        Require(command.GrossAmount > 0m && command.GrossAmount < 100_000_000m && Math.Round(command.GrossAmount, 2) == command.GrossAmount, "grossAmount");
        Require(currency.Length == 3 && currency.All(char.IsAsciiLetterUpper), "currency");
        Require(command.PaidOn != default, "paidOn");
        Require(command.Reference is null || command.Reference.Length <= OwnerPayout.ReferenceMaxLength, "reference");
        Require(command.Components is null || command.Components.All(c => c.Key.Length is > 0 and <= 32 && c.Value >= 0m && Math.Round(c.Value, 2) == c.Value), "components");

        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.OwnerAccounting, orgId.ToString("N")));
        var existing = await db.OwnerPayouts.AsNoTracking().FirstOrDefaultAsync(p => p.OrgId == orgId && p.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            if (existing.PropertyOwnerId != command.OwnerId || existing.OwnerStatementVersionId != command.StatementVersionId
                || existing.GrossAmount != command.GrossAmount || existing.PaidOn != command.PaidOn || existing.Currency != currency)
                throw new DomainConflictException(OwnerPayoutErrors.PayoutKeyReused, "OwnerPayoutKeyReused");
            logger.LogInformation("Owner payout {PayoutId} replayed for key reuse by {UserId}", existing.Id, userId);
            return new OwnerPayoutResult(existing, await EvidenceOfAsync(existing.Id, ct), Replayed: true);
        }

        var version = await db.OwnerStatementVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == command.StatementVersionId && v.OrgId == orgId && v.PropertyOwnerId == command.OwnerId, ct)
            ?? throw new NotFoundException($"Owner statement version {command.StatementVersionId} was not found")
            {
                Code = OwnerAccountingErrors.VersionNotFound,
                MessageKey = "OwnerStatementVersionNotFound",
            };
        if (version.Status is not (OwnerStatementVersionStatus.Approved or OwnerStatementVersionStatus.Published))
            throw new DomainRuleException(OwnerAccountingErrors.VersionNotApproved, "OwnerStatementNotApproved");
        Require(string.Equals(version.Currency, currency, StringComparison.Ordinal), "currency");
        if (await IsClosedAsync(orgId, command.OwnerId, command.PaidOn.Year, command.PaidOn.Month, ct))
            throw new DomainConflictException(OwnerAccountingErrors.PeriodClosed, "OwnerPeriodClosed");

        // The approved rule in force on the day of the payment decides; an unknown fact it needs blocks (never a guess).
        var resolution = await fiscal.ResolveAsync(orgId, command.OwnerId, command.PaidOn, ct);
        var decision = OwnerWithholdingRules.Decide(resolution.Parameters, command.AgencyCollected, command.QualifyingShortLet, command.Components);
        var withheld = decision.Amount ?? 0m;
        Require(withheld <= command.GrossAmount, "grossAmount");

        var now = time.GetUtcNow().UtcDateTime;
        var stays = command.StayIds is { Count: > 0 } ids ? JsonSerializer.Serialize(ids.Distinct().ToList(), Json) : null;
        var payout = new OwnerPayout
        {
            OrgId = orgId,
            PropertyOwnerId = command.OwnerId,
            OwnerStatementVersionId = version.Id,
            IdempotencyKey = key,
            Reference = command.Reference?.Trim(),
            PaidOn = command.PaidOn,
            Currency = currency,
            GrossAmount = command.GrossAmount,
            WithholdingAmount = withheld,
            NetAmount = command.GrossAmount - withheld,
            AgencyCollected = command.AgencyCollected,
            QualifyingShortLet = command.QualifyingShortLet,
            ComponentsJson = command.Components is null ? null : JsonSerializer.Serialize(command.Components, Json),
            StayIdsJson = stays,
            ConfirmedAt = now,
            ConfirmedByUserId = userId,
        };
        var evidence = new TaxObligationEvidence
        {
            OrgId = orgId,
            OwnerPayoutId = payout.Id,
            PropertyOwnerId = command.OwnerId,
            OwnerStatementVersionId = version.Id,
            IdempotencyKey = "payout:" + payout.Id.ToString("N"),
            Decision = decision.Decision,
            ReasonCode = decision.Reason,
            Jurisdiction = resolution.Policy.Jurisdiction,
            RuleCode = resolution.Policy.RuleCode,
            RuleVersion = resolution.Policy.Version,
            GrossBase = decision.GrossBase,
            RatePercent = decision.RatePercent,
            Rounding = decision.Rounding,
            Amount = decision.Amount,
            TaxCode = decision.TaxCode,
            PaidOn = command.PaidOn,
            PeriodYear = command.PaidOn.Year,
            PeriodMonth = command.PaidOn.Month,
            StayIdsJson = stays,
            FactsJson = JsonSerializer.Serialize(
                new { command.AgencyCollected, command.QualifyingShortLet, command.Components, command.GrossAmount, Parameters = resolution.Parameters }, Json),
            CreatedAt = now,
            CreatedByUserId = userId,
        };
        db.OwnerPayouts.Add(payout);
        db.TaxObligationEvidence.Add(evidence);

        var ruleVersion = resolution.Policy.RuleCode + "@" + resolution.Policy.Version;
        db.OwnerLedgerEntries.Add(Line(payout, OwnerLedgerEntryType.OwnerPayment, -payout.NetAmount, version.Currency, ruleVersion, null, now, userId));
        if (withheld > 0m)
            db.OwnerLedgerEntries.Add(Line(payout, OwnerLedgerEntryType.Withholding, -withheld, version.Currency, ruleVersion, decision.TaxCode, now, userId));
        await MarkDirtyAsync(orgId, command.OwnerId, command.PaidOn.Year, command.PaidOn.Month, now, ct);

        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);
        logger.LogInformation(
            "Owner payout {PayoutId} of owner {OwnerId} confirmed by {UserId}: decision {Decision} ({Reason}) on rule {RuleVersion}",
            payout.Id, command.OwnerId, userId, decision.Decision, decision.Reason, ruleVersion);
        return new OwnerPayoutResult(payout, [evidence], Replayed: false);
    }

    public async Task<OwnerPayoutResult> ReversePayoutAsync(Guid orgId, Guid payoutId, string? reason, string? userId, CancellationToken ct = default)
    {
        var why = reason?.Trim();
        Require(!string.IsNullOrEmpty(why) && why.Length <= OwnerPayout.ReasonMaxLength, "reason");
        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.OwnerAccounting, orgId.ToString("N")));
        var payout = await db.OwnerPayouts.FirstOrDefaultAsync(p => p.Id == payoutId && p.OrgId == orgId, ct) ?? throw PayoutNotFound(payoutId);
        if (payout.Status == OwnerPayoutStatus.Reversed)
            throw new DomainConflictException(OwnerPayoutErrors.PayoutReversed, "OwnerPayoutReversed");

        var now = time.GetUtcNow().UtcDateTime;
        var today = time.TodayInRomeAsDateOnly();
        var evidence = await EvidenceOfAsync(payout.Id, ct);
        foreach (var decision in evidence.Where(e => e.ReversesEvidenceId is null && evidence.All(r => r.ReversesEvidenceId != e.Id)))
            db.TaxObligationEvidence.Add(ReversalOf(decision, today, now, userId));

        // The ledger lines are reversed in their month while it is open, else in the month of today.
        var lines = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.SourceType == OwnerLedgerSourceType.OwnerPayout && e.SourceId == payout.Id && e.ReversesEntryId == null)
            .ToListAsync(ct);
        foreach (var line in lines)
        {
            var closed = await IsClosedAsync(orgId, line.PropertyOwnerId, line.PeriodYear, line.PeriodMonth, ct);
            var reversal = OwnerAccountingService.ReversalOf(line, closed ? today : null, now, userId, why);
            db.OwnerLedgerEntries.Add(reversal);
            await MarkDirtyAsync(orgId, line.PropertyOwnerId, reversal.PeriodYear, reversal.PeriodMonth, now, ct);
        }

        payout.Status = OwnerPayoutStatus.Reversed;
        payout.ReversedAt = now;
        payout.ReversedByUserId = userId;
        payout.ReversalReason = why;
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);
        logger.LogInformation("Owner payout {PayoutId} reversed by {UserId}", payout.Id, userId);
        return new OwnerPayoutResult(payout, await EvidenceOfAsync(payout.Id, ct), Replayed: false);
    }

    public async Task<OwnerPayoutResult> GetPayoutAsync(Guid orgId, Guid payoutId, CancellationToken ct = default)
    {
        var payout = await db.OwnerPayouts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == payoutId && p.OrgId == orgId, ct) ?? throw PayoutNotFound(payoutId);
        return new OwnerPayoutResult(payout, await EvidenceOfAsync(payout.Id, ct), Replayed: false);
    }

    public async Task<IReadOnlyList<OwnerPayout>> ListPayoutsAsync(Guid orgId, Guid? ownerId, CancellationToken ct = default) =>
        await db.OwnerPayouts.AsNoTracking()
            .Where(p => p.OrgId == orgId && (ownerId == null || p.PropertyOwnerId == ownerId))
            .OrderByDescending(p => p.PaidOn)
            .ThenByDescending(p => p.ConfirmedAt)
            .ToListAsync(ct);

    public async Task<TaxReportingDataset> GenerateAnnualDatasetAsync(Guid orgId, int year, string? userId, CancellationToken ct = default)
    {
        Require(year is >= 2000 and <= 2100, "year");
        var evidence = await ApplicableAsync(orgId, year, null, ct);
        if (evidence.Count == 0)
            throw new DomainRuleException(OwnerPayoutErrors.NoObligations, "TaxNoObligations");

        var dataset = await db.TaxReportingDatasets
            .FirstOrDefaultAsync(d => d.OrgId == orgId && d.Kind == TaxReportingDataset.CertificazioneUnica && d.Year == year, ct);
        if (dataset is not null && dataset.FilingState != TaxFilingState.Generated)
            throw new DomainConflictException(OwnerPayoutErrors.FileAlreadyFiled, "TaxFileAlreadyFiled");
        if (dataset is null)
        {
            dataset = new TaxReportingDataset { OrgId = orgId, Kind = TaxReportingDataset.CertificazioneUnica, Year = year };
            db.TaxReportingDatasets.Add(dataset);
        }

        var perOwner = evidence
            .GroupBy(e => e.PropertyOwnerId)
            .OrderBy(g => g.Key)
            .Select(g => new { OwnerId = g.Key, GrossBase = g.Sum(e => e.GrossBase ?? 0m), Withheld = g.Sum(e => e.Amount ?? 0m), Evidence = g.Count() })
            .ToList();
        dataset.TotalGrossBase = perOwner.Sum(o => o.GrossBase);
        dataset.TotalWithheld = perOwner.Sum(o => o.Withheld);
        dataset.EvidenceCount = evidence.Count;
        dataset.EvidenceSha256 = HashOf(evidence);
        dataset.PayloadJson = JsonSerializer.Serialize(perOwner, Json);
        dataset.GeneratedAt = time.GetUtcNow().UtcDateTime;
        dataset.GeneratedByUserId = userId;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Annual tax dataset {DatasetId} of {Year} generated by {UserId} from {Count} evidence rows", dataset.Id, year, userId, evidence.Count);
        return dataset;
    }

    public async Task<TaxReportingDataset?> GetAnnualDatasetAsync(Guid orgId, int year, CancellationToken ct = default) =>
        await db.TaxReportingDatasets.AsNoTracking()
            .FirstOrDefaultAsync(d => d.OrgId == orgId && d.Kind == TaxReportingDataset.CertificazioneUnica && d.Year == year, ct);

    public async Task<F24Record> SetF24FilingStateAsync(Guid orgId, Guid f24Id, TaxFilingCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var record = await db.F24Records.FirstOrDefaultAsync(f => f.Id == f24Id && f.OrgId == orgId, ct)
            ?? throw new NotFoundException($"F24 {f24Id} was not found") { Code = StatementExportErrors.F24NotFound, MessageKey = "F24NotFound" };
        var (handoff, receipt, rejection) = Move(record.FilingState, command);
        record.FilingState = command.State;
        record.HandoffReference = handoff ?? record.HandoffReference;
        record.ReceiptReference = receipt ?? record.ReceiptReference;
        record.RejectionReason = rejection ?? record.RejectionReason;
        record.FilingUpdatedAt = time.GetUtcNow().UtcDateTime;
        record.FilingUpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("F24 {F24Id} moved to {State} by {UserId}", record.Id, command.State, userId);
        return record;
    }

    public async Task<TaxReportingDataset> SetDatasetFilingStateAsync(Guid orgId, Guid datasetId, TaxFilingCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var dataset = await db.TaxReportingDatasets.FirstOrDefaultAsync(d => d.Id == datasetId && d.OrgId == orgId, ct)
            ?? throw new NotFoundException($"Tax dataset {datasetId} was not found") { Code = OwnerPayoutErrors.DatasetNotFound, MessageKey = "TaxDatasetNotFound" };
        var (handoff, receipt, rejection) = Move(dataset.FilingState, command);
        dataset.FilingState = command.State;
        dataset.HandoffReference = handoff ?? dataset.HandoffReference;
        dataset.ReceiptReference = receipt ?? dataset.ReceiptReference;
        dataset.RejectionReason = rejection ?? dataset.RejectionReason;
        dataset.FilingUpdatedAt = time.GetUtcNow().UtcDateTime;
        dataset.FilingUpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Tax dataset {DatasetId} moved to {State} by {UserId}", dataset.Id, command.State, userId);
        return dataset;
    }

    public async Task<TaxReconciliation> ReconcileAsync(Guid orgId, int year, CancellationToken ct = default)
    {
        Require(year is >= 2000 and <= 2100, "year");
        var evidence = await ApplicableAsync(orgId, year, null, ct);
        var f24s = await db.F24Records.AsNoTracking().Where(f => f.OrgId == orgId && f.ReferenceYear == year).ToListAsync(ct);
        var annual = await GetAnnualDatasetAsync(orgId, year, ct);
        var months = Enumerable.Range(1, 12)
            .Select(m => new
            {
                Month = m,
                Obligations = evidence.Where(e => e.PeriodMonth == m).Sum(e => e.Amount ?? 0m),
                F24 = f24s.FirstOrDefault(f => f.ReferenceMonth == m),
            })
            .Where(m => m.Obligations != 0m || m.F24 is not null)
            .Select(m => new TaxPeriodReconciliation(m.Month, m.Obligations, m.F24?.TotalAmount, m.F24?.FilingState))
            .ToList();
        return new TaxReconciliation(year, evidence.Sum(e => e.Amount ?? 0m), f24s.Sum(f => f.TotalAmount), annual?.TotalWithheld, months);
    }

    /// <summary>The applicable evidence (decisions and their reversals) of the year, or of one month of it.</summary>
    internal async Task<List<TaxObligationEvidence>> ApplicableAsync(Guid orgId, int year, int? month, CancellationToken ct) =>
        await db.TaxObligationEvidence.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.Decision == TaxDecision.Applicable && e.PeriodYear == year && (month == null || e.PeriodMonth == month))
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

    /// <summary>SHA-256 of the ids and amounts of the evidence.</summary>
    internal static string HashOf(IEnumerable<TaxObligationEvidence> evidence)
    {
        var text = string.Join('\n', evidence.Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Id:N}:{e.Amount ?? 0m:0.00}")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The checks of a move: the transition, and the reference it needs (handoff, receipt, reason).</summary>
    private static (string? Handoff, string? Receipt, string? Rejection) Move(TaxFilingState current, TaxFilingCommand command)
    {
        if (!TaxFiling.CanMove(current, command.State))
            throw new DomainConflictException(OwnerPayoutErrors.FilingTransitionInvalid, "TaxFilingTransitionInvalid");
        var handoff = Reference(command.HandoffReference);
        var receipt = Reference(command.ReceiptReference);
        var reason = Reference(command.Reason);
        var missing = command.State switch
        {
            TaxFilingState.Submitted => handoff is null ? "handoffReference" : null,
            TaxFilingState.Accepted => receipt is null ? "receiptReference" : null,
            TaxFilingState.Rejected => reason is null ? "reason" : null,
            _ => null,
        };
        if (missing is not null)
        {
            throw new DomainRuleException(OwnerPayoutErrors.FilingReferenceRequired, "TaxFilingReferenceRequired")
            {
                Extensions = new Dictionary<string, object?> { ["field"] = missing },
            };
        }

        return (command.State == TaxFilingState.Submitted ? handoff : null, command.State == TaxFilingState.Accepted ? receipt : null,
            command.State == TaxFilingState.Rejected ? reason : null);
    }

    private static string? Reference(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    private async Task<List<TaxObligationEvidence>> EvidenceOfAsync(Guid payoutId, CancellationToken ct) =>
        await db.TaxObligationEvidence.AsNoTracking()
            .Where(e => e.OwnerPayoutId == payoutId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

    private Task<bool> IsClosedAsync(Guid orgId, Guid ownerId, int year, int month, CancellationToken ct) =>
        db.OwnerClosePeriods.AsNoTracking().AnyAsync(
            p => p.OrgId == orgId && p.PropertyOwnerId == ownerId && p.PeriodYear == year && p.PeriodMonth == month && p.Status == OwnerClosePeriodStatus.Closed,
            ct);

    /// <summary>Marks dirty the owner's month when it has a current version (a line came after its cut-off).</summary>
    private async Task MarkDirtyAsync(Guid orgId, Guid ownerId, int year, int month, DateTime now, CancellationToken ct)
    {
        var hasVersion = await db.OwnerStatementVersions.AsNoTracking().AnyAsync(
            v => v.OrgId == orgId && v.PropertyOwnerId == ownerId && v.PeriodYear == year && v.PeriodMonth == month
                && v.Status != OwnerStatementVersionStatus.Superseded,
            ct);
        if (!hasVersion)
            return;
        var row = db.OwnerClosePeriods.Local.FirstOrDefault(p => p.OrgId == orgId && p.PropertyOwnerId == ownerId && p.PeriodYear == year && p.PeriodMonth == month)
            ?? await db.OwnerClosePeriods.FirstOrDefaultAsync(
                p => p.OrgId == orgId && p.PropertyOwnerId == ownerId && p.PeriodYear == year && p.PeriodMonth == month, ct);
        if (row is null)
        {
            row = new OwnerClosePeriod { OrgId = orgId, PropertyOwnerId = ownerId, PeriodYear = year, PeriodMonth = month, CreatedAt = now };
            db.OwnerClosePeriods.Add(row);
        }

        row.IsDirty = true;
        row.DirtySince ??= now;
        row.UpdatedAt = now;
    }

    /// <summary>A ledger line of the payout, in the month of the payment.</summary>
    private static OwnerLedgerEntry Line(
        OwnerPayout payout,
        OwnerLedgerEntryType type,
        decimal amount,
        string statementCurrency,
        string ruleVersion,
        string? taxCode,
        DateTime now,
        string? userId) => new()
        {
            OrgId = payout.OrgId,
            PropertyOwnerId = payout.PropertyOwnerId,
            EntryType = type,
            SourceType = OwnerLedgerSourceType.OwnerPayout,
            SourceId = payout.Id,
            SourceReference = payout.Reference,
            IdempotencyKey = string.Create(CultureInfo.InvariantCulture, $"payout:{payout.Id:N}:{(int)type}"),
            ServiceDate = payout.PaidOn,
            RecognitionDate = payout.PaidOn,
            PaymentDate = payout.PaidOn,
            PeriodYear = payout.PaidOn.Year,
            PeriodMonth = payout.PaidOn.Month,
            SharePercent = 100m,
            OriginalCurrency = payout.Currency,
            SourceAmount = -payout.GrossAmount,
            OriginalAmount = amount,
            StatementCurrency = statementCurrency,
            TaxCode = taxCode,
            RuleVersion = ruleVersion.Length <= OwnerLedgerEntry.RuleVersionMaxLength ? ruleVersion : ruleVersion[..OwnerLedgerEntry.RuleVersionMaxLength],
            Amount = amount,
            PostedAt = now,
            PostedByUserId = userId,
        };

    /// <summary>The reversal of a decision: the same facts, the opposite amounts, in the month of the reversal.</summary>
    private static TaxObligationEvidence ReversalOf(TaxObligationEvidence original, DateOnly today, DateTime now, string? userId) => new()
    {
        OrgId = original.OrgId,
        OwnerPayoutId = original.OwnerPayoutId,
        PropertyOwnerId = original.PropertyOwnerId,
        OwnerStatementVersionId = original.OwnerStatementVersionId,
        IdempotencyKey = "rev:" + original.Id.ToString("N"),
        Decision = original.Decision,
        ReasonCode = original.ReasonCode,
        Jurisdiction = original.Jurisdiction,
        RuleCode = original.RuleCode,
        RuleVersion = original.RuleVersion,
        GrossBase = -original.GrossBase,
        RatePercent = original.RatePercent,
        Rounding = original.Rounding,
        Amount = -original.Amount,
        TaxCode = original.TaxCode,
        PaidOn = original.PaidOn,
        PeriodYear = today.Year,
        PeriodMonth = today.Month,
        StayIdsJson = original.StayIdsJson,
        FactsJson = original.FactsJson,
        ReversesEvidenceId = original.Id,
        CreatedAt = now,
        CreatedByUserId = userId,
    };

    private static NotFoundException PayoutNotFound(Guid payoutId) =>
        new($"Owner payout {payoutId} was not found") { Code = OwnerPayoutErrors.PayoutNotFound, MessageKey = "OwnerPayoutNotFound" };

    private static void Require(bool valid, string field)
    {
        if (!valid)
            throw new DomainRuleException(OwnerPayoutErrors.PayoutInvalid, "OwnerPayoutInvalid") { Extensions = new Dictionary<string, object?> { ["field"] = field } };
    }
}
