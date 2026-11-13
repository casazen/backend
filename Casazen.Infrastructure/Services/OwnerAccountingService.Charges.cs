using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>The charges and expenses, the exchange rates and the manual corrections.</summary>
public sealed partial class OwnerAccountingService
{
    /// <summary>The largest amount a charge, a rate or a correction may carry.</summary>
    private const decimal MaxAmount = 100_000_000m;

    public async Task<OwnerCharge> CreateChargeAsync(Guid orgId, OwnerChargeCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var description = Truncate(command.Description, int.MaxValue);
        var currency = command.Currency?.Trim().ToUpperInvariant() ?? string.Empty;
        RequireCharge(Enum.IsDefined(command.Kind), "kind");
        RequireCharge(Enum.IsDefined(command.Recurrence), "recurrence");
        RequireCharge(description is not null && description.Length <= OwnerCharge.DescriptionMaxLength, "description");
        RequireCharge(IsMoney(command.Amount) && command.Amount > 0m, "amount");
        RequireCharge(CurrencyPattern().IsMatch(currency), "currency");
        RequireCharge(command.EffectiveFrom != default, "effectiveFrom");
        RequireCharge(command.EffectiveTo is null || command.EffectiveTo >= command.EffectiveFrom, "effectiveTo");
        RequireCharge(command.AttachmentReference is null || command.AttachmentReference.Length <= OwnerCharge.AttachmentMaxLength, "attachmentReference");
        await EnsurePropertyAsync(orgId, command.PropertyId, ct);
        if (command.OwnerId is { } owner)
            await EnsureOwnerAsync(orgId, owner, ct);

        var effectiveTo = command.Recurrence == OwnerChargeRecurrence.OneTime ? null : command.EffectiveTo;
        var duplicate = await db.OwnerCharges.AsNoTracking().AnyAsync(
            c => c.OrgId == orgId && c.PropertyId == command.PropertyId && c.PropertyOwnerId == command.OwnerId && c.Kind == command.Kind
                && c.Recurrence == command.Recurrence && c.Amount == command.Amount && c.Currency == currency && c.EffectiveFrom == command.EffectiveFrom
                && c.Description == description && c.Status != OwnerChargeStatus.Cancelled,
            ct);
        if (duplicate)
            throw new DomainConflictException(OwnerAccountingErrors.ChargeDuplicate, "OwnerChargeDuplicate");

        var charge = new OwnerCharge
        {
            OrgId = orgId,
            PropertyId = command.PropertyId,
            PropertyOwnerId = command.OwnerId,
            Kind = command.Kind,
            Recurrence = command.Recurrence,
            Description = description!,
            Amount = command.Amount,
            Currency = currency,
            EffectiveFrom = command.EffectiveFrom,
            EffectiveTo = effectiveTo,
            AttachmentReference = Truncate(command.AttachmentReference, OwnerCharge.AttachmentMaxLength),
            CreatedAt = time.GetUtcNow().UtcDateTime,
            CreatedByUserId = userId,
        };
        db.OwnerCharges.Add(charge);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Owner charge {ChargeId} of property {PropertyId} created by {UserId}", charge.Id, charge.PropertyId, userId);
        return charge;
    }

    public async Task<OwnerCharge> ApproveChargeAsync(Guid orgId, Guid chargeId, string? userId, CancellationToken ct = default)
    {
        var charge = await ChargeAsync(orgId, chargeId, ct);
        if (charge.Status != OwnerChargeStatus.Pending)
            throw new DomainConflictException(OwnerAccountingErrors.ChargeNotPending, "OwnerChargeNotPending");
        charge.Status = OwnerChargeStatus.Approved;
        charge.ApprovedAt = time.GetUtcNow().UtcDateTime;
        charge.ApprovedByUserId = userId;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Owner charge {ChargeId} approved by {UserId}", charge.Id, userId);
        return charge;
    }

    public async Task<OwnerCharge> CancelChargeAsync(Guid orgId, Guid chargeId, string? userId, CancellationToken ct = default)
    {
        await using var tx = await LockAsync(orgId, ct);
        var charge = await ChargeAsync(orgId, chargeId, ct);
        if (charge.Status == OwnerChargeStatus.Cancelled)
            throw new DomainConflictException(OwnerAccountingErrors.ChargeNotPending, "OwnerChargeNotPending");

        // What the charge posted is reversed in its open months; a closed month keeps its lines (a correction of a later month fixes it).
        var now = time.GetUtcNow().UtcDateTime;
        var posted = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.SourceType == OwnerLedgerSourceType.OwnerCharge && e.SourceId == chargeId && e.ReversesEntryId == null)
            .ToListAsync(ct);
        var reversed = await ReversedIdsAsync(orgId, posted.Select(e => e.Id).ToList(), ct);
        var closed = await ClosedMonthsAsync(orgId, posted.Select(e => e.PropertyOwnerId).Distinct().ToList(), ct);
        foreach (var entry in posted.Where(e => !reversed.Contains(e.Id) && !closed.Contains((e.PropertyOwnerId, e.PeriodYear, e.PeriodMonth))))
        {
            db.OwnerLedgerEntries.Add(Reversal(entry, null, now, userId));
            await MarkDirtyAsync(orgId, new Period(entry.PeriodYear, entry.PeriodMonth), [entry.PropertyOwnerId], now, ct);
        }

        charge.Status = OwnerChargeStatus.Cancelled;
        charge.CancelledAt = now;
        charge.CancelledByUserId = userId;
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);
        logger.LogInformation("Owner charge {ChargeId} cancelled by {UserId}", charge.Id, userId);
        return charge;
    }

    public async Task<IReadOnlyList<OwnerCharge>> ListChargesAsync(Guid orgId, Guid? propertyId, CancellationToken ct = default) =>
        await db.OwnerCharges.AsNoTracking()
            .Where(c => c.OrgId == orgId && (propertyId == null || c.PropertyId == propertyId))
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

    public async Task<OwnerFxRate> AddFxRateAsync(Guid orgId, OwnerFxRateCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var from = command.BaseCurrency?.Trim().ToUpperInvariant() ?? string.Empty;
        var to = command.QuoteCurrency?.Trim().ToUpperInvariant() ?? string.Empty;
        var source = Truncate(command.Source, int.MaxValue);
        RequireRate(CurrencyPattern().IsMatch(from), "baseCurrency");
        RequireRate(CurrencyPattern().IsMatch(to) && !string.Equals(from, to, StringComparison.Ordinal), "quoteCurrency");
        RequireRate(command.RateDate != default, "rateDate");
        RequireRate(command.Rate > 0m && command.Rate < MaxAmount && Math.Round(command.Rate, 8) == command.Rate, "rate");
        RequireRate(source is not null && source.Length <= OwnerFxRate.SourceMaxLength, "source");
        if (await db.OwnerFxRates.AsNoTracking().AnyAsync(r => r.OrgId == orgId && r.BaseCurrency == from && r.QuoteCurrency == to && r.RateDate == command.RateDate, ct))
            throw new DomainConflictException(OwnerAccountingErrors.FxRateExists, "OwnerFxRateExists");

        var rate = new OwnerFxRate
        {
            OrgId = orgId,
            BaseCurrency = from,
            QuoteCurrency = to,
            RateDate = command.RateDate,
            Rate = command.Rate,
            Source = source!,
            CreatedAt = time.GetUtcNow().UtcDateTime,
            CreatedByUserId = userId,
        };
        db.OwnerFxRates.Add(rate);
        await db.SaveChangesAsync(ct);
        return rate;
    }

    public async Task<IReadOnlyList<OwnerFxRate>> ListFxRatesAsync(Guid orgId, CancellationToken ct = default) =>
        await db.OwnerFxRates.AsNoTracking()
            .Where(r => r.OrgId == orgId)
            .OrderByDescending(r => r.RateDate)
            .ThenBy(r => r.BaseCurrency)
            .ThenBy(r => r.QuoteCurrency)
            .ToListAsync(ct);

    public async Task<OwnerLedgerEntry> PostCorrectionAsync(Guid orgId, OwnerCorrectionCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var description = Truncate(command.Description, int.MaxValue);
        var currency = command.Currency?.Trim().ToUpperInvariant() ?? string.Empty;
        RequireCorrection(IsMoney(command.Amount) && command.Amount != 0m, "amount");
        RequireCorrection(CurrencyPattern().IsMatch(currency), "currency");
        RequireCorrection(command.Date != default, "date");
        RequireCorrection(description is not null && description.Length <= OwnerLedgerEntry.TextMaxLength, "description");
        RequireCorrection(command.AttachmentReference is null || command.AttachmentReference.Length <= OwnerLedgerEntry.AttachmentMaxLength, "attachmentReference");
        await EnsureOwnerAsync(orgId, command.OwnerId, ct);
        if (command.PropertyId is { } propertyId)
            await EnsurePropertyAsync(orgId, propertyId, ct);

        await using var tx = await LockAsync(orgId, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var period = Period.Of(command.Date.Year, command.Date.Month);
        var closed = await ClosedMonthsAsync(orgId, [command.OwnerId], ct);
        if (closed.Contains((command.OwnerId, period.Year, period.Month)))
            throw new DomainConflictException(OwnerAccountingErrors.PeriodClosed, "OwnerPeriodClosed");

        // Only a manual line is replaced by hand: a line of a source changes with its source (the posting reverses and replaces it).
        OwnerLedgerEntry? replaced = null;
        if (command.ReplacesEntryId is { } replacesId)
        {
            replaced = await db.OwnerLedgerEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == replacesId && e.OrgId == orgId, ct)
                ?? throw new NotFoundException($"Owner ledger entry {replacesId} was not found") { Code = OwnerAccountingErrors.EntryNotFound, MessageKey = "OwnerLedgerEntryNotFound" };
            RequireCorrection(replaced.SourceType == OwnerLedgerSourceType.Manual && replaced.ReversesEntryId is null && replaced.PropertyOwnerId == command.OwnerId, "replacesEntryId");
            if ((await ReversedIdsAsync(orgId, [replaced.Id], ct)).Count > 0)
                throw new DomainConflictException(OwnerAccountingErrors.EntryAlreadyReversed, "OwnerLedgerEntryAlreadyReversed");
            var into = closed.Contains((replaced.PropertyOwnerId, replaced.PeriodYear, replaced.PeriodMonth)) ? period : (Period?)null;
            var reversal = Reversal(replaced, into, now, userId, description);
            db.OwnerLedgerEntries.Add(reversal);
            await MarkDirtyAsync(orgId, new Period(reversal.PeriodYear, reversal.PeriodMonth), [command.OwnerId], now, ct);
        }

        var correction = new OwnerLedgerEntry
        {
            OrgId = orgId,
            PropertyOwnerId = command.OwnerId,
            PropertyId = command.PropertyId,
            EntryType = OwnerLedgerEntryType.Correction,
            SourceType = OwnerLedgerSourceType.Manual,
            SourceId = replaced?.Id,
            AttachmentReference = Truncate(command.AttachmentReference, OwnerLedgerEntry.AttachmentMaxLength),
            IdempotencyKey = "manual:" + Guid.NewGuid().ToString("N"),
            ServiceDate = command.Date,
            RecognitionDate = command.Date,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            SharePercent = 100m,
            OriginalCurrency = currency,
            SourceAmount = command.Amount,
            OriginalAmount = command.Amount,
            StatementCurrency = currency,
            RuleVersion = replaced?.RuleVersion,
            Amount = command.Amount,
            Description = description,
            PostedAt = now,
            PostedByUserId = userId,
        };
        db.OwnerLedgerEntries.Add(correction);
        await MarkDirtyAsync(orgId, period, [command.OwnerId], now, ct);
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);
        logger.LogInformation("Owner ledger correction {EntryId} of owner {OwnerId} posted by {UserId}", correction.Id, command.OwnerId, userId);
        return correction;
    }

    /// <summary>The closed months of the owners, as (owner, year, month).</summary>
    private async Task<HashSet<(Guid, int, int)>> ClosedMonthsAsync(Guid orgId, List<Guid> owners, CancellationToken ct)
    {
        var rows = await db.OwnerClosePeriods.AsNoTracking()
            .Where(p => p.OrgId == orgId && owners.Contains(p.PropertyOwnerId) && p.Status == OwnerClosePeriodStatus.Closed)
            .Select(p => new { p.PropertyOwnerId, p.PeriodYear, p.PeriodMonth })
            .ToListAsync(ct);
        return rows.Select(r => (r.PropertyOwnerId, r.PeriodYear, r.PeriodMonth)).ToHashSet();
    }

    private async Task<OwnerCharge> ChargeAsync(Guid orgId, Guid chargeId, CancellationToken ct) =>
        await db.OwnerCharges.FirstOrDefaultAsync(c => c.Id == chargeId && c.OrgId == orgId, ct)
        ?? throw new NotFoundException($"Owner charge {chargeId} was not found") { Code = OwnerAccountingErrors.ChargeNotFound, MessageKey = "OwnerChargeNotFound" };

    private async Task EnsurePropertyAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        if (!await db.Properties.AsNoTracking().AnyAsync(p => p.Id == propertyId && p.OrgId == orgId, ct))
            throw new NotFoundException($"Property {propertyId} was not found") { Code = "property_not_found", MessageKey = "PropertyNotFound" };
    }

    /// <summary>At most two decimals and below the largest amount.</summary>
    private static bool IsMoney(decimal amount) => Math.Abs(amount) < MaxAmount && Math.Round(amount, 2) == amount;

    private static void RequireCharge(bool valid, string field) => Require(valid, OwnerAccountingErrors.ChargeInvalid, "OwnerChargeInvalid", field);

    private static void RequireRate(bool valid, string field) => Require(valid, OwnerAccountingErrors.FxRateInvalid, "OwnerFxRateInvalid", field);

    private static void RequireCorrection(bool valid, string field) => Require(valid, OwnerAccountingErrors.CorrectionInvalid, "OwnerCorrectionInvalid", field);

    private static void Require(bool valid, string code, string key, string field)
    {
        if (!valid)
            throw new DomainRuleException(code, key) { Extensions = new Dictionary<string, object?> { ["field"] = field } };
    }

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();
}
