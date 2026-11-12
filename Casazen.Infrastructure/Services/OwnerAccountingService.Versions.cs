using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>The statement versions, their approval and publication, the close, the legal hold and the evidence.</summary>
public sealed partial class OwnerAccountingService
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<OwnerStatementVersion> GenerateVersionAsync(Guid orgId, Guid ownerId, int year, int month, string? userId, CancellationToken ct = default)
    {
        var period = Period.Of(year, month);
        await EnsureOwnerAsync(orgId, ownerId, ct);
        await using var tx = await LockAsync(orgId, ct);
        var now = time.GetUtcNow().UtcDateTime;

        var row = await PeriodRowAsync(orgId, ownerId, period, now, ct);
        if (row.Status == OwnerClosePeriodStatus.Closed)
            throw new DomainConflictException(OwnerAccountingErrors.PeriodClosed, "OwnerPeriodClosed");

        var versions = await db.OwnerStatementVersions
            .Where(v => v.OrgId == orgId && v.PropertyOwnerId == ownerId && v.PeriodYear == year && v.PeriodMonth == month)
            .ToListAsync(ct);
        var previous = versions.Where(v => v.Status != OwnerStatementVersionStatus.Superseded).MaxBy(v => v.VersionNumber);

        var entries = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.PropertyOwnerId == ownerId && e.PostedAt <= now
                && (e.PeriodYear < year || (e.PeriodYear == year && e.PeriodMonth <= month)))
            .ToListAsync(ct);
        var ofMonth = entries.Where(e => e.PeriodYear == year && e.PeriodMonth == month).ToList();
        var hash = Hash(ofMonth);
        if (previous is { Status: OwnerStatementVersionStatus.Approved or OwnerStatementVersionStatus.Published } && !row.IsDirty
            && string.Equals(previous.EntriesSha256, hash, StringComparison.Ordinal))
            throw new DomainConflictException(OwnerAccountingErrors.VersionUnchanged, "OwnerStatementUnchanged");

        var plan = await PlanAsync(orgId, period, ct);
        var live = await LiveEntriesAsync(orgId, period, ct);
        var issues = LedgerIssues(plan, live, ownerId);
        var opening = entries.Where(e => new Period(e.PeriodYear, e.PeriodMonth).IsBefore(year, month)).Sum(e => e.Amount);
        var closing = opening + ofMonth.Sum(e => e.Amount);
        if (closing < 0m)
            issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.NegativeBalance, ownerId, Blocking: false));
        var currencies = ofMonth.Select(e => e.StatementCurrency).Distinct(StringComparer.Ordinal).ToList();

        var version = new OwnerStatementVersion
        {
            OrgId = orgId,
            PropertyOwnerId = ownerId,
            PeriodYear = year,
            PeriodMonth = month,
            VersionNumber = versions.Count == 0 ? 1 : versions.Max(v => v.VersionNumber) + 1,
            Status = issues.Any(i => i.Blocking) ? OwnerStatementVersionStatus.Flagged : OwnerStatementVersionStatus.Draft,
            Currency = currencies.Count > 0 ? currencies[0] : previous?.Currency ?? StayCurrency.Normalize(null),
            CutoffAt = now,
            OpeningBalance = opening,
            Revenue = SumOf(ofMonth, OwnerLedgerEntryType.BookingRevenue),
            ChannelFees = SumOf(ofMonth, OwnerLedgerEntryType.ChannelFee),
            Commission = SumOf(ofMonth, OwnerLedgerEntryType.AgencyCommission),
            Charges = SumOf(ofMonth, OwnerLedgerEntryType.OwnerCharge, OwnerLedgerEntryType.OwnerExpense),
            Payments = SumOf(ofMonth, OwnerLedgerEntryType.OwnerPayment, OwnerLedgerEntryType.AgencyPayment),
            Corrections = SumOf(ofMonth, OwnerLedgerEntryType.Correction),
            ClosingBalance = closing,
            EntryCount = ofMonth.Count,
            EntriesSha256 = hash,
            IssuesJson = issues.Count == 0 ? null : JsonSerializer.Serialize(issues, SnapshotJson),
            SupersedesVersionId = previous?.Id,
            CreatedAt = now,
            CreatedByUserId = userId,
        };
        db.OwnerStatementVersions.Add(version);
        if (previous is not null)
        {
            previous.Status = OwnerStatementVersionStatus.Superseded;
            previous.SupersededByVersionId = version.Id;
            previous.SupersededAt = now;
        }

        row.IsDirty = false;
        row.DirtySince = null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);

        logger.LogInformation(
            "Owner statement version {VersionId} ({Number}, {Status}) of owner {OwnerId} for {Year}-{Month} generated by {UserId}",
            version.Id, version.VersionNumber, version.Status, ownerId, year, month, userId);
        return version;
    }

    public async Task<IReadOnlyList<OwnerStatementVersion>> ListVersionsAsync(Guid orgId, Guid ownerId, int year, int month, CancellationToken ct = default)
    {
        Period.Of(year, month);
        await EnsureOwnerAsync(orgId, ownerId, ct);
        return await db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.OrgId == orgId && v.PropertyOwnerId == ownerId && v.PeriodYear == year && v.PeriodMonth == month)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct);
    }

    public async Task<OwnerStatementVersionDetail> GetVersionAsync(Guid orgId, Guid versionId, CancellationToken ct = default)
    {
        var version = await db.OwnerStatementVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.OrgId == orgId, ct)
            ?? throw VersionNotFound(versionId);
        return new OwnerStatementVersionDetail(version, await EntriesOfAsync(version, ct));
    }

    public async Task<OwnerStatementVersion> ApproveVersionAsync(Guid orgId, Guid versionId, string? userId, CancellationToken ct = default)
    {
        var version = await db.OwnerStatementVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.OrgId == orgId, ct) ?? throw VersionNotFound(versionId);
        if (version.Status == OwnerStatementVersionStatus.Flagged)
            throw new DomainRuleException(OwnerAccountingErrors.VersionFlagged, "OwnerStatementFlagged");
        if (version.Status != OwnerStatementVersionStatus.Draft)
            throw new DomainConflictException(OwnerAccountingErrors.VersionNotDraft, "OwnerStatementNotDraft");
        await EnsureCleanAsync(version, ct);

        version.Status = OwnerStatementVersionStatus.Approved;
        version.ApprovedAt = time.GetUtcNow().UtcDateTime;
        version.ApprovedByUserId = userId;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Owner statement version {VersionId} approved by {UserId}", version.Id, userId);
        return version;
    }

    public async Task<OwnerStatementVersion> PublishVersionAsync(Guid orgId, Guid versionId, string? userId, CancellationToken ct = default)
    {
        var version = await db.OwnerStatementVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.OrgId == orgId, ct) ?? throw VersionNotFound(versionId);
        if (version.Status != OwnerStatementVersionStatus.Approved)
            throw new DomainRuleException(OwnerAccountingErrors.VersionNotApproved, "OwnerStatementNotApproved");
        await EnsureCleanAsync(version, ct);

        var entries = await EntriesOfAsync(version, ct);
        var snapshot = JsonSerializer.Serialize(Snapshot.Of(version, entries), SnapshotJson);
        version.PublishedSnapshot = snapshot;
        version.PublishedSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
        version.Status = OwnerStatementVersionStatus.Published;
        version.PublishedAt = time.GetUtcNow().UtcDateTime;
        version.PublishedByUserId = userId;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Owner statement version {VersionId} published by {UserId}", version.Id, userId);
        return version;
    }

    public async Task<OwnerClosePeriod> CloseAsync(
        Guid orgId,
        Guid ownerId,
        int year,
        int month,
        OwnerCloseCommand command,
        bool mayWaive,
        string? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var period = Period.Of(year, month);
        await EnsureOwnerAsync(orgId, ownerId, ct);
        await using var tx = await LockAsync(orgId, ct);
        var now = time.GetUtcNow().UtcDateTime;

        var row = await PeriodRowAsync(orgId, ownerId, period, now, ct);
        if (row.Status == OwnerClosePeriodStatus.Closed)
            throw new DomainConflictException(OwnerAccountingErrors.PeriodClosed, "OwnerPeriodClosed");
        if (row.IsDirty)
            throw new DomainConflictException(OwnerAccountingErrors.PeriodDirty, "OwnerPeriodDirty");

        var version = await db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.OrgId == orgId && v.PropertyOwnerId == ownerId && v.PeriodYear == year && v.PeriodMonth == month
                && v.Status != OwnerStatementVersionStatus.Superseded)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct);
        if (version is null || version.Status is not (OwnerStatementVersionStatus.Approved or OwnerStatementVersionStatus.Published))
            throw new DomainRuleException(OwnerAccountingErrors.VersionNotApproved, "OwnerStatementNotApproved");
        await EnsureCleanAsync(version, ct);

        // The approved closing balance against what the paid wires of the month's statements transferred, in the currency of the version.
        var paid = await db.WireBatchStatements.AsNoTracking()
            .Where(w => w.OrgId == orgId && w.WireBatch.Status == WireBatchStatus.Paid && w.WireBatch.Currency == version.Currency
                && w.OwnerStatement.PropertyOwnerId == ownerId && w.OwnerStatement.Year == year && w.OwnerStatement.Month == month)
            .SumAsync(w => w.OwnerStatement.NetPayable, ct);
        var due = version.ClosingBalance;
        var difference = due - paid;
        if (difference != 0m || due < 0m)
        {
            var reason = Truncate(command.WaiverReason, OwnerClosePeriod.ReasonMaxLength);
            if (reason is null)
            {
                throw new DomainRuleException(OwnerAccountingErrors.CloseUnreconciled, "OwnerCloseUnreconciled")
                {
                    Extensions = new Dictionary<string, object?> { ["due"] = due, ["paid"] = paid, ["difference"] = difference },
                };
            }

            if (!mayWaive)
                throw new DomainForbiddenException(OwnerAccountingErrors.CloseWaiverForbidden, "OwnerCloseWaiverForbidden");
            row.WaiverReason = reason;
            row.WaivedByUserId = userId;
            row.WaivedAt = now;
        }

        // The retention follows the approved rule set of the owner (the version could not be approved without one).
        var resolution = await fiscal.ResolveAsync(orgId, ownerId, period.Last, ct);
        var years = OwnerRetention.YearsFor(resolution.Policy.Jurisdiction, resolution.Parameters.RetentionYears);
        row.Status = OwnerClosePeriodStatus.Closed;
        row.ClosedAt = now;
        row.ClosedByUserId = userId;
        row.ClosedVersionId = version.Id;
        row.DueAmount = due;
        row.PaidAmount = paid;
        row.Difference = difference;
        row.RetentionJurisdiction = resolution.Policy.Jurisdiction;
        row.RetentionYears = years;
        row.RetainUntil = OwnerRetention.RetainUntil(DateOnly.FromDateTime(now), years);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);

        logger.LogInformation(
            "Owner month {Year}-{Month} of owner {OwnerId} closed by {UserId} on version {VersionId} (waived: {Waived})",
            year, month, ownerId, userId, version.Id, row.WaiverReason is not null);
        return row;
    }

    public async Task<OwnerClosePeriod> SetLegalHoldAsync(
        Guid orgId,
        Guid ownerId,
        int year,
        int month,
        bool hold,
        string? reason,
        string? userId,
        CancellationToken ct = default)
    {
        var period = Period.Of(year, month);
        await EnsureOwnerAsync(orgId, ownerId, ct);
        var why = Truncate(reason, OwnerClosePeriod.ReasonMaxLength);
        if (hold && why is null)
            throw new DomainRuleException(OwnerAccountingErrors.LegalHoldReasonRequired, "OwnerLegalHoldReasonRequired");

        var now = time.GetUtcNow().UtcDateTime;
        var row = await PeriodRowAsync(orgId, ownerId, period, now, ct);
        row.LegalHold = hold;
        row.LegalHoldReason = hold ? why : null;
        row.LegalHoldByUserId = userId;
        row.LegalHoldAt = now;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Legal hold of owner {OwnerId} for {Year}-{Month} set to {Hold} by {UserId}", ownerId, year, month, hold, userId);
        return row;
    }

    public async Task<OwnerAccountingEvidence> ExportEvidenceAsync(Guid orgId, Guid ownerId, int year, int month, CancellationToken ct = default)
    {
        Period.Of(year, month);
        await EnsureOwnerAsync(orgId, ownerId, ct);
        var row = await db.OwnerClosePeriods.AsNoTracking()
            .FirstOrDefaultAsync(p => p.OrgId == orgId && p.PropertyOwnerId == ownerId && p.PeriodYear == year && p.PeriodMonth == month, ct);
        var versions = await db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.OrgId == orgId && v.PropertyOwnerId == ownerId && v.PeriodYear == year && v.PeriodMonth == month)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync(ct);
        var entries = (await db.OwnerLedgerEntries.AsNoTracking()
                .Where(e => e.OrgId == orgId && e.PropertyOwnerId == ownerId && e.PeriodYear == year && e.PeriodMonth == month)
                .ToListAsync(ct))
            .OrderBy(e => e.PostedAt)
            .ThenBy(e => e.Id)
            .ToList();
        var chargeIds = entries.Where(e => e.SourceType == OwnerLedgerSourceType.OwnerCharge && e.SourceId is not null).Select(e => e.SourceId!.Value).Distinct().ToList();
        var charges = await db.OwnerCharges.AsNoTracking().Where(c => c.OrgId == orgId && chargeIds.Contains(c.Id)).ToListAsync(ct);
        logger.LogInformation("Evidence of owner {OwnerId} for {Year}-{Month} exported", ownerId, year, month);
        return new OwnerAccountingEvidence(ownerId, year, month, row, versions, entries, charges);
    }

    /// <summary>The lines a version was made of: its owner's lines of the month posted up to its cut-off.</summary>
    private async Task<List<OwnerLedgerEntry>> EntriesOfAsync(OwnerStatementVersion version, CancellationToken ct) =>
        (await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == version.OrgId && e.PropertyOwnerId == version.PropertyOwnerId && e.PeriodYear == version.PeriodYear
                && e.PeriodMonth == version.PeriodMonth && e.PostedAt <= version.CutoffAt)
            .ToListAsync(ct))
        .OrderBy(e => e.PostedAt)
        .ThenBy(e => e.Id)
        .ToList();

    /// <summary>A version is acted on only while it is the picture of the ledger: the month is not dirty and its lines still hash to it.</summary>
    private async Task EnsureCleanAsync(OwnerStatementVersion version, CancellationToken ct)
    {
        var dirty = await db.OwnerClosePeriods.AsNoTracking().AnyAsync(
            p => p.OrgId == version.OrgId && p.PropertyOwnerId == version.PropertyOwnerId && p.PeriodYear == version.PeriodYear
                && p.PeriodMonth == version.PeriodMonth && p.IsDirty,
            ct);
        var later = await db.OwnerLedgerEntries.AsNoTracking().AnyAsync(
            e => e.OrgId == version.OrgId && e.PropertyOwnerId == version.PropertyOwnerId && e.PeriodYear == version.PeriodYear
                && e.PeriodMonth == version.PeriodMonth && e.PostedAt > version.CutoffAt,
            ct);
        if (dirty || later)
            throw new DomainConflictException(OwnerAccountingErrors.PeriodDirty, "OwnerPeriodDirty");
        if (!string.Equals(Hash(await EntriesOfAsync(version, ct)), version.EntriesSha256, StringComparison.Ordinal))
            throw new DomainConflictException(OwnerAccountingErrors.LedgerImmutable, "OwnerLedgerImmutable");
    }

    private static decimal SumOf(IEnumerable<OwnerLedgerEntry> entries, params OwnerLedgerEntryType[] types) =>
        entries.Where(e => types.Contains(e.EntryType)).Sum(e => e.Amount);

    private static NotFoundException VersionNotFound(Guid versionId) =>
        new($"Owner statement version {versionId} was not found") { Code = OwnerAccountingErrors.VersionNotFound, MessageKey = "OwnerStatementVersionNotFound" };

    /// <summary>The published file of a version: the version and its lines, as the owner received them.</summary>
    private sealed record Snapshot(
        Guid VersionId,
        Guid OwnerId,
        int Year,
        int Month,
        int Version,
        string Currency,
        DateTime CutoffAt,
        decimal OpeningBalance,
        decimal Revenue,
        decimal ChannelFees,
        decimal Commission,
        decimal Charges,
        decimal Payments,
        decimal Corrections,
        decimal ClosingBalance,
        string EntriesSha256,
        IReadOnlyList<SnapshotLine> Lines)
    {
        public static Snapshot Of(OwnerStatementVersion v, IReadOnlyList<OwnerLedgerEntry> entries) => new(
            v.Id, v.PropertyOwnerId, v.PeriodYear, v.PeriodMonth, v.VersionNumber, v.Currency, v.CutoffAt, v.OpeningBalance, v.Revenue, v.ChannelFees,
            v.Commission, v.Charges, v.Payments, v.Corrections, v.ClosingBalance, v.EntriesSha256,
            entries.Select(e => new SnapshotLine(
                e.Id, e.EntryType, e.SourceType, e.SourceId, e.SourceReference, e.SourceDocumentId, e.PropertyId, e.ServiceDate, e.RecognitionDate,
                e.PaymentDate, e.SharePercent, e.OriginalCurrency, e.OriginalAmount, e.StatementCurrency, e.FxRate, e.FxSource, e.FxDate, e.TaxCode,
                e.RuleVersion, e.Amount, e.ReversesEntryId, e.Description)).ToList());
    }

    private sealed record SnapshotLine(
        Guid Id,
        OwnerLedgerEntryType Type,
        OwnerLedgerSourceType Source,
        Guid? SourceId,
        string? SourceReference,
        Guid? SourceDocumentId,
        Guid? PropertyId,
        DateOnly ServiceDate,
        DateOnly RecognitionDate,
        DateOnly? PaymentDate,
        decimal SharePercent,
        string OriginalCurrency,
        decimal OriginalAmount,
        string StatementCurrency,
        decimal FxRate,
        string? FxSource,
        DateOnly? FxDate,
        string? TaxCode,
        string? RuleVersion,
        decimal Amount,
        Guid? ReversesEntryId,
        string? Description);
}
