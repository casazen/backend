using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
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
        var outcome = await GenerateCoreAsync(orgId, ownerId, Period.Of(year, month), userId, skipWhenCurrent: false, ct);
        return outcome.Version;
    }

    /// <summary>A version of the owner's month, and whether this call made it (<c>false</c>: the version in force was already the picture of the ledger).</summary>
    private sealed record GenerateOutcome(OwnerStatementVersion Version, bool Created);

    /// <summary>
    /// The generation of a version. With <paramref name="skipWhenCurrent"/> (the bulk generation of E13/F01/US03/T01, idempotent) a version in force that is still the picture
    /// of the ledger is answered as it is instead of being generated again: its lines, its opening balance and its issues are those of now and the month is not dirty. A single
    /// generation never skips: an approved or published version in that state is 409 <c>owner_statement_unchanged</c>, a draft or flagged one is generated again.
    /// </summary>
    private async Task<GenerateOutcome> GenerateCoreAsync(Guid orgId, Guid ownerId, Period period, string? userId, bool skipWhenCurrent, CancellationToken ct)
    {
        var year = period.Year;
        var month = period.Month;
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

        // A balance is money in a currency, not a bare number. A month without entries retains the currency of the
        // outstanding balance; changing currency while another currency is still owed needs an explicit conversion. A line for information only (the commission of a
        // portal the agency bears, FIX/BE-V2-NO-RECHARGE) is in no balance: it is in the lines of the version (their hash) and in none of its amounts.
        var priorBalances = entries.Where(e => OwnerLedgerBalance.Counts(e.EntryType) && new Period(e.PeriodYear, e.PeriodMonth).IsBefore(year, month))
            .GroupBy(e => e.StatementCurrency, StringComparer.Ordinal)
            .Select(group => new { Currency = group.Key, Amount = group.Sum(e => e.Amount) })
            .Where(balance => balance.Amount != 0m)
            .ToList();
        var currencies = ofMonth.Select(e => e.StatementCurrency).Distinct(StringComparer.Ordinal).ToList();
        var currency = currencies.Count > 0 ? currencies[0]
            : priorBalances.FirstOrDefault()?.Currency ?? previous?.Currency ?? StayCurrency.Normalize(null);
        if (priorBalances.Any(balance => balance.Currency != currency))
            throw new DomainConflictException(OwnerAccountingErrors.StatementCurrencyConflict, "OwnerStatementCurrencyConflict");

        // The opening balance is what the earlier months say now: a correction posted in an earlier month changes it, and a version cut before it is no longer
        // the picture of the ledger even though the lines of its own month are the same.
        var opening = priorBalances.Sum(balance => balance.Amount);
        var sameLines = previous is not null && !row.IsDirty && previous.Currency == currency
            && string.Equals(previous.EntriesSha256, hash, StringComparison.Ordinal) && previous.OpeningBalance == opening;
        if (sameLines && previous is { Status: OwnerStatementVersionStatus.Approved or OwnerStatementVersionStatus.Published })
        {
            if (skipWhenCurrent)
                return new GenerateOutcome(previous, false);
            throw new DomainConflictException(OwnerAccountingErrors.VersionUnchanged, "OwnerStatementUnchanged");
        }

        var plan = await PlanAsync(orgId, period, ct);
        var live = await LiveEntriesAsync(orgId, period, ct);
        var issues = LedgerIssues(plan, live, ownerId);
        var closing = opening + ofMonth.Where(e => OwnerLedgerBalance.Counts(e.EntryType)).Sum(e => e.Amount);
        if (closing < 0m)
            issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.NegativeBalance, ownerId, Blocking: false));

        // The statement flag (E18/F01/US03/T01, ⚖ I8, behind Features:TaxReportingExports): an owner past the limit of units of a private person is presumed a business. It
        // never blocks the version: the statement is right, the tax treatment of the owner is what the agency looks at. The units are those of the last day of the month.
        if (taxChecks is { WarningsEnabled: true }
            && (await taxChecks.CheckUnitsAsync(orgId, [ownerId], period.Last, ct)).TryGetValue(ownerId, out var units)
            && units.PresumedBusiness)
        {
            issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.ShortLetBusinessPresumed, ownerId, Blocking: false));
        }

        // A draft or flagged version made of these very lines, with these very issues, is what a new version would be: the bulk generation leaves it (running it twice
        // changes nothing more).
        if (skipWhenCurrent && sameLines && previous is not null && SameIssues(previous, issues))
            return new GenerateOutcome(previous, false);

        var version = new OwnerStatementVersion
        {
            OrgId = orgId,
            PropertyOwnerId = ownerId,
            PeriodYear = year,
            PeriodMonth = month,
            VersionNumber = versions.Count == 0 ? 1 : versions.Max(v => v.VersionNumber) + 1,
            Status = issues.Any(i => i.Blocking) ? OwnerStatementVersionStatus.Flagged : OwnerStatementVersionStatus.Draft,
            Currency = currency,
            CutoffAt = now,
            OpeningBalance = opening,
            Revenue = SumOf(ofMonth, OwnerLedgerEntryType.BookingRevenue),

            // The commissions the owner bears: the ones the agency bears (ChannelFeeBorneByAgency) are for information only, in no column.
            ChannelFees = SumOf(ofMonth, OwnerLedgerEntryType.ChannelFee),
            // The commission of the agency with the VAT its owner invoice added on top (E14/F01/US01/T01): the section of the statement that names the invoice.
            Commission = SumOf(ofMonth, OwnerLedgerEntryType.AgencyCommission, OwnerLedgerEntryType.AgencyVat),
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
        return new GenerateOutcome(version, true);
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

        // The PDF the owner downloads is made now, from the same lines, and stored with the version in the same save (E13/F01/US02/T01): one file, never made again.
        var stored = statementFiles is null ? StoredStatementPdf.None : await statementFiles.StorePublishedPdfAsync(version, entries, userId, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            if (statementFiles is not null)
                await statementFiles.DiscardStoredPdfAsync(stored, CancellationToken.None);
            throw;
        }

        logger.LogInformation("Owner statement version {VersionId} published by {UserId}", version.Id, userId);
        await TellOwnerAsync(version, ct);
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

        // A month is closed once it is over: closing it earlier would leave every payment and charge that comes after the close out of the owner's account.
        if (period.Last >= time.TodayInRomeAsDateOnly())
            throw new DomainConflictException(OwnerAccountingErrors.PeriodNotEnded, "OwnerPeriodNotEnded");

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

        // Nothing the sources say may be missing from the ledger: a payment that came after the cut-off and was never posted, a gap of the shares, an exchange rate
        // that is missing, an owner whose profile cannot be used. The readiness the version was generated with is read again now.
        var plan = await PlanAsync(orgId, period, ct);
        var blocking = LedgerIssues(plan, await LiveEntriesAsync(orgId, period, ct), ownerId)
            .Where(i => i.Blocking)
            .Select(i => i.Code)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (blocking.Count > 0)
        {
            throw new DomainRuleException(OwnerAccountingErrors.CloseNotReady, "OwnerCloseNotReady")
            {
                Extensions = new Dictionary<string, object?> { ["issues"] = blocking },
            };
        }

        // The approved closing balance against what settled it, in the currency of the version: the confirmed payouts of the month's versions (their gross: the
        // net transferred plus the withholding kept, E06/F05/US02/T03) or, when the month has none, the wires marked paid for the month's statements. A month
        // settled with payouts is settled by them (PlanAsync leaves its wires out), so one transfer is counted once.
        var versionIds = await db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.OrgId == orgId && v.PropertyOwnerId == ownerId && v.PeriodYear == year && v.PeriodMonth == month)
            .Select(v => v.Id)
            .ToListAsync(ct);
        var payouts = await db.OwnerPayouts.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.PropertyOwnerId == ownerId && p.Status == OwnerPayoutStatus.Confirmed && versionIds.Contains(p.OwnerStatementVersionId))
            .Select(p => new { p.Currency, p.GrossAmount })
            .ToListAsync(ct);
        var paid = payouts.Count > 0
            ? payouts.Where(p => p.Currency == version.Currency).Sum(p => p.GrossAmount)
            : await db.WireBatchStatements.AsNoTracking()
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

        // FIX/BE-CLEANING-VAT: the question of the VAT of a cleaning company answered «Non lo so» (or by nobody) comes back once, now that the month is closed; it never held the close.
        await AskCleaningVatAgainAsync(orgId, ownerId, period, ct);
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
        (await LinesOf(db.OwnerLedgerEntries.AsNoTracking(), version).ToListAsync(ct))
        .OrderBy(e => e.PostedAt)
        .ThenBy(e => e.Id)
        .ToList();

    /// <summary>
    /// The ledger lines of a version: its owner's lines of the month posted up to its cut-off, the one definition of what a version is made of (its hash, its published file and
    /// its PDF and CSV read the lines through it).
    /// </summary>
    internal static IQueryable<OwnerLedgerEntry> LinesOf(IQueryable<OwnerLedgerEntry> entries, OwnerStatementVersion version)
    {
        return entries.Where(e => e.OrgId == version.OrgId && e.PropertyOwnerId == version.PropertyOwnerId && e.PeriodYear == version.PeriodYear
            && e.PeriodMonth == version.PeriodMonth && e.PostedAt <= version.CutoffAt);
    }

    /// <summary>
    /// Tells the owner that a statement was published (E13/F01/US02/T02), after the publication was saved. The statement is published whatever happens here, so the
    /// notice never turns the publication into an error: <see cref="IOwnerStatementDocuments.TellOwnerPublishedAsync"/> queues the e-mail and, when it cannot, logs the
    /// ids and raises a «To do» for the agency, which tells the owner by hand. What is left for this last resort is that the «To do» itself could not be raised: that
    /// is an error in the log with the ids, and the only case in which the owner is told by nobody.
    /// </summary>
    private async Task TellOwnerAsync(OwnerStatementVersion version, CancellationToken ct)
    {
        if (statementFiles is null)
            return;

        try
        {
            await statementFiles.TellOwnerPublishedAsync(version, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Owner statement version {VersionId} of owner {OwnerId} (org {OrgId}) is published, the owner was not told and the To do for the agency could not be raised",
                version.Id,
                version.PropertyOwnerId,
                version.OrgId);
        }
    }

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
        var entries = await EntriesOfAsync(version, ct);
        if (!string.Equals(Hash(entries), version.EntriesSha256, StringComparison.Ordinal))
            throw new DomainConflictException(OwnerAccountingErrors.LedgerImmutable, "OwnerLedgerImmutable");
        if (entries.Any(entry => entry.StatementCurrency != version.Currency))
            throw new DomainConflictException(OwnerAccountingErrors.StatementCurrencyConflict, "OwnerStatementCurrencyConflict");

        // The balance carried in from the earlier months is part of the picture: a line posted there after the cut-off makes this version stale too. A line for information
        // only is in no balance.
        var priorBalances = await db.OwnerLedgerEntries.AsNoTracking()
            .CountingInBalance()
            .Where(e => e.OrgId == version.OrgId && e.PropertyOwnerId == version.PropertyOwnerId
                && (e.PeriodYear < version.PeriodYear || (e.PeriodYear == version.PeriodYear && e.PeriodMonth < version.PeriodMonth)))
            .GroupBy(e => e.StatementCurrency)
            .Select(group => new { Currency = group.Key, Amount = group.Sum(e => e.Amount) })
            .ToListAsync(ct);
        if (priorBalances.Any(balance => balance.Amount != 0m && balance.Currency != version.Currency))
            throw new DomainConflictException(OwnerAccountingErrors.StatementCurrencyConflict, "OwnerStatementCurrencyConflict");
        var opening = priorBalances.Where(balance => balance.Currency == version.Currency).Sum(balance => balance.Amount);
        if (opening != version.OpeningBalance)
            throw new DomainConflictException(OwnerAccountingErrors.PeriodDirty, "OwnerPeriodDirty");
    }

    private static decimal SumOf(IEnumerable<OwnerLedgerEntry> entries, params OwnerLedgerEntryType[] types) =>
        entries.Where(e => types.Contains(e.EntryType)).Sum(e => e.Amount);

    /// <summary>The issues recorded on a version at its generation; <c>null</c> when the record cannot be read.</summary>
    private static List<OwnerReadinessIssue>? IssuesOf(OwnerStatementVersion version)
    {
        if (string.IsNullOrEmpty(version.IssuesJson))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<OwnerReadinessIssue>>(version.IssuesJson, SnapshotJson) ?? [];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The codes of the blocking issues a version was generated with, sorted: why a flagged version is flagged.</summary>
    private static List<string> BlockingCodesOf(OwnerStatementVersion version) =>
        IssuesOf(version)?.Where(i => i.Blocking).Select(i => i.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? [];

    /// <summary>Whether the issues recorded on a version are the ones the month has now (same codes about the same owners, properties and sources).</summary>
    private static bool SameIssues(OwnerStatementVersion version, IReadOnlyList<OwnerReadinessIssue> current) =>
        IssuesOf(version) is { } recorded && IssueKeys(recorded).SequenceEqual(IssueKeys(current), StringComparer.Ordinal);

    private static IEnumerable<string> IssueKeys(IEnumerable<OwnerReadinessIssue> issues) =>
        issues
            .Select(i => string.Join('|', i.Code, i.OwnerId?.ToString("N"), i.PropertyId?.ToString("N"), i.SourceId?.ToString("N"), i.Blocking ? "1" : "0"))
            .Order(StringComparer.Ordinal);

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
