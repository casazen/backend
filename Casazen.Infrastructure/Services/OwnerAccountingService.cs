using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerAccountingService"/>
/// <remarks>
/// <para><b>Posting.</b> The lines of a month are planned from their sources: the completed payments (the owner's share on the payment day, recognized on
/// the payment day, or on the check-out for a profile with the recognition rule <c>stay</c>), the mandate commission on that share, the channel commission
/// of an active booking whose payments are gross (with its first payment), the approved charges, the paid wires. Each line gets a key made of its source,
/// type, owner and amounts: a key already posted is left alone, a new key is posted, a posted key no longer planned is reversed. The shares are split
/// in the currency of the source, rounded half to even, the last owner (by id) taking the remainder, then converted with the rate of the agency.</para>
/// <para><b>Locks.</b> Posting, generation, close and corrections of one org run one at a time (<see cref="PostgresAdvisoryLocks.Scope.OwnerAccounting"/>).</para>
/// </remarks>
public sealed partial class OwnerAccountingService(
    AppDbContext db,
    IOwnerFiscalProfileService fiscal,
    TimeProvider time,
    ILogger<OwnerAccountingService> logger) : IOwnerAccountingService
{
    /// <summary>A rate older than this many days before the day of the source is not used.</summary>
    public const int FxMaxAgeDays = 7;

    /// <summary>The recognition rule of a profile that recognizes the revenue on the check-out (any other: on the payment day).</summary>
    public const string StayRecognitionRule = "stay";

    /// <summary>The end of the key of a line computed without a rule version.</summary>
    internal const string NoRuleKeySuffix = ":norule";

    private static readonly OwnerLedgerSourceType[] PostedSources =
    [
        OwnerLedgerSourceType.Payment, OwnerLedgerSourceType.Booking, OwnerLedgerSourceType.OwnerCharge, OwnerLedgerSourceType.WireBatch,
    ];

    /// <summary>The states of a payment that collected money: a refund moves it to <c>PartiallyRefunded</c> or <c>Refunded</c> (the revenue is the net).</summary>
    private static readonly PaymentStatus[] CollectedStatuses = [PaymentStatus.Completed, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded];

    public async Task<OwnerPostingResult> PostPeriodAsync(Guid orgId, int year, int month, string? userId, CancellationToken ct = default)
    {
        var period = Period.Of(year, month);
        await using var tx = await LockAsync(orgId, ct);
        var plan = await PlanAsync(orgId, period, ct);
        var now = time.GetUtcNow().UtcDateTime;

        var live = await LiveEntriesAsync(orgId, period, ct);
        var automated = live.Where(e => PostedSources.Contains(e.SourceType) && !plan.ClosedOwners.Contains(e.PropertyOwnerId)).ToList();
        var liveKeys = automated.Select(e => BaseKey(e.IdempotencyKey)).ToHashSet(StringComparer.Ordinal);
        var plannedKeys = plan.Entries.Select(e => e.IdempotencyKey).ToHashSet(StringComparer.Ordinal);

        // A key may already be posted in another month (the recognition rule changed) or have been reversed: posted elsewhere is left alone, a reversed
        // key is posted again with a suffix (the unique index keeps one row per key).
        var sameKey = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && plannedKeys.Contains(e.IdempotencyKey))
            .Select(e => new { e.Id, e.IdempotencyKey })
            .ToListAsync(ct);
        var sameKeyReversed = await ReversedIdsAsync(orgId, sameKey.Select(e => e.Id).ToList(), ct);
        var taken = sameKey.Select(e => e.IdempotencyKey).ToHashSet(StringComparer.Ordinal);
        var liveElsewhere = sameKey.Where(e => !sameKeyReversed.Contains(e.Id)).Select(e => e.IdempotencyKey).ToHashSet(StringComparer.Ordinal);

        var touched = new HashSet<Guid>();
        int posted = 0, reversed = 0, unchanged = 0;
        foreach (var entry in plan.OpenEntries)
        {
            if (liveKeys.Contains(entry.IdempotencyKey) || liveElsewhere.Contains(entry.IdempotencyKey))
            {
                unchanged++;
                continue;
            }

            if (taken.Contains(entry.IdempotencyKey))
                entry.IdempotencyKey = string.Concat(entry.IdempotencyKey, "#", Guid.NewGuid().ToString("N").AsSpan(0, 8));
            entry.PostedAt = now;
            entry.PostedByUserId = userId;
            db.OwnerLedgerEntries.Add(entry);
            touched.Add(entry.PropertyOwnerId);
            posted++;
        }

        foreach (var stale in automated.Where(e => !plannedKeys.Contains(BaseKey(e.IdempotencyKey))))
        {
            db.OwnerLedgerEntries.Add(Reversal(stale, null, now, userId));
            touched.Add(stale.PropertyOwnerId);
            reversed++;
        }

        await MarkDirtyAsync(orgId, period, touched, now, ct);
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);

        logger.LogInformation(
            "Owner ledger {Year}-{Month} of org {OrgId} posted by {UserId}: {Posted} posted, {Reversed} reversed, {Unchanged} unchanged, {Issues} issues",
            year, month, orgId, userId, posted, reversed, unchanged, plan.Issues.Count);
        return new OwnerPostingResult(year, month, posted, reversed, unchanged, plan.Issues);
    }

    public async Task<OwnerCloseReadiness> GetReadinessAsync(Guid orgId, int year, int month, CancellationToken ct = default)
    {
        var period = Period.Of(year, month);
        var plan = await PlanAsync(orgId, period, ct);
        var live = await LiveEntriesAsync(orgId, period, ct);
        var all = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.PeriodYear == year && e.PeriodMonth == month)
            .ToListAsync(ct);
        var versions = await db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.OrgId == orgId && v.PeriodYear == year && v.PeriodMonth == month && v.Status != OwnerStatementVersionStatus.Superseded)
            .ToListAsync(ct);
        var rows = await db.OwnerClosePeriods.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.PeriodYear == year && p.PeriodMonth == month)
            .ToListAsync(ct);

        var owners = live.Select(e => e.PropertyOwnerId)
            .Concat(plan.Entries.Select(e => e.PropertyOwnerId))
            .Concat(versions.Select(v => v.PropertyOwnerId))
            .Concat(rows.Select(r => r.PropertyOwnerId))
            .Concat(plan.Issues.Where(i => i.OwnerId is not null).Select(i => i.OwnerId!.Value))
            .Concat(OwnersOfPropertiesWithIssues(plan))
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        // What the earlier months say now, against the balance a version carried in: a line posted there after its cut-off makes the version stale.
        var openings = (await db.OwnerLedgerEntries.AsNoTracking()
                .Where(e => e.OrgId == orgId && (e.PeriodYear < year || (e.PeriodYear == year && e.PeriodMonth < month)))
                .GroupBy(e => e.PropertyOwnerId)
                .Select(g => new { Owner = g.Key, Sum = g.Sum(e => e.Amount) })
                .ToListAsync(ct))
            .ToDictionary(x => x.Owner, x => x.Sum);

        var result = new List<OwnerPeriodReadiness>();
        foreach (var owner in owners)
        {
            var row = rows.FirstOrDefault(r => r.PropertyOwnerId == owner);
            var latest = versions.Where(v => v.PropertyOwnerId == owner).MaxBy(v => v.VersionNumber);
            var status = row?.Status ?? OwnerClosePeriodStatus.Open;
            List<OwnerReadinessIssue> issues;
            if (status == OwnerClosePeriodStatus.Closed)
            {
                // The month is closed and stays so: what the sources say after the close is told, never posted.
                issues = [new OwnerReadinessIssue(OwnerReadinessCodes.PeriodClosed, owner, Blocking: false)];
                issues.AddRange(plan.Issues.Where(i => i.OwnerId == owner && i.Code == OwnerReadinessCodes.LateSourceAfterClose));
            }
            else
            {
                issues = LedgerIssues(plan, live, owner);
                if (latest is null)
                {
                    issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.StatementMissing, owner));
                }
                else
                {
                    if (latest.Status is OwnerStatementVersionStatus.Draft or OwnerStatementVersionStatus.Flagged)
                        issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.StatementNotApproved, owner, SourceId: latest.Id));
                    var atCutoff = all.Where(e => e.PropertyOwnerId == owner && e.PostedAt <= latest.CutoffAt);
                    if (!string.Equals(Hash(atCutoff), latest.EntriesSha256, StringComparison.Ordinal) || openings.GetValueOrDefault(owner) != latest.OpeningBalance)
                        issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.LedgerStatementImbalance, owner, SourceId: latest.Id));
                    if (latest.ClosingBalance < 0m)
                        issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.NegativeBalance, owner, SourceId: latest.Id));
                }

                if (row?.IsDirty == true)
                    issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.PeriodDirty, owner));
            }

            result.Add(new OwnerPeriodReadiness(owner, status, row?.IsDirty == true, latest?.VersionNumber, latest?.Status, issues));
        }

        return new OwnerCloseReadiness(year, month, plan.Issues.Where(i => i.OwnerId is null).ToList(), result);
    }

    /// <summary>
    /// The issues of the ledger of an owner's month that block a version: the owner cannot be used (fiscal profile and rule), a source without its rate, a
    /// source not posted yet (or a posted line whose source changed), the same source posted twice, lines in more than one statement currency.
    /// </summary>
    private static List<OwnerReadinessIssue> LedgerIssues(Plan plan, IReadOnlyList<OwnerLedgerEntry> live, Guid owner)
    {
        var issues = plan.Issues.Where(i => i.OwnerId == owner).ToList();

        // The issues of a property (a share gap, no owner on the day, a payment without an amount) have no owner of their own: they hold back the statement of
        // every owner of that property, since a source left out of the ledger is a line missing from their accounts (and a line already posted is reversed).
        if (plan.OwnerProperties.TryGetValue(owner, out var properties))
        {
            issues.AddRange(plan.Issues
                .Where(i => i.OwnerId is null && i.PropertyId is { } property && properties.Contains(property))
                .Select(i => i with { OwnerId = owner }));
        }

        var ownLive = live.Where(e => e.PropertyOwnerId == owner).ToList();
        var liveKeys = ownLive.Select(e => BaseKey(e.IdempotencyKey)).ToHashSet(StringComparer.Ordinal);
        var plannedKeys = plan.Entries.Where(e => e.PropertyOwnerId == owner).Select(e => e.IdempotencyKey).ToHashSet(StringComparer.Ordinal);

        foreach (var planned in plan.Entries.Where(e => e.PropertyOwnerId == owner && !liveKeys.Contains(e.IdempotencyKey)))
            issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.UnpostedSource, owner, planned.PropertyId, planned.SourceId));
        foreach (var stale in ownLive.Where(e => PostedSources.Contains(e.SourceType) && !plannedKeys.Contains(BaseKey(e.IdempotencyKey))))
            issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.UnpostedSource, owner, stale.PropertyId, stale.SourceId));
        foreach (var duplicate in ownLive.Where(e => e.SourceType != OwnerLedgerSourceType.Manual)
                     .GroupBy(e => (e.EntryType, e.SourceType, e.SourceId, e.SourceReference))
                     .Where(g => g.Count() > 1))
            issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.DuplicateEntry, owner, duplicate.First().PropertyId, duplicate.Key.SourceId));
        if (ownLive.Select(e => e.StatementCurrency).Distinct(StringComparer.Ordinal).Count() > 1)
            issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.CurrencyMixed, owner));
        return issues;
    }

    /// <summary>The owners of the properties that have an issue without an owner (the ones <see cref="LedgerIssues"/> holds back).</summary>
    private static IEnumerable<Guid> OwnersOfPropertiesWithIssues(Plan plan)
    {
        var properties = plan.Issues.Where(i => i.OwnerId is null && i.PropertyId is not null).Select(i => i.PropertyId!.Value).ToHashSet();
        return properties.Count == 0
            ? []
            : plan.OwnerProperties.Where(o => o.Value.Overlaps(properties)).Select(o => o.Key);
    }

    /// <summary>The lines of the month that count: neither reversals nor reversed.</summary>
    private async Task<List<OwnerLedgerEntry>> LiveEntriesAsync(Guid orgId, Period period, CancellationToken ct)
    {
        var entries = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.PeriodYear == period.Year && e.PeriodMonth == period.Month)
            .ToListAsync(ct);
        var reversed = await ReversedIdsAsync(orgId, entries.Select(e => e.Id).ToList(), ct);
        return entries.Where(e => e.ReversesEntryId is null && !reversed.Contains(e.Id)).ToList();
    }

    private async Task<HashSet<Guid>> ReversedIdsAsync(Guid orgId, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];
        var reversed = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.ReversesEntryId != null && ids.Contains(e.ReversesEntryId.Value))
            .Select(e => e.ReversesEntryId!.Value)
            .ToListAsync(ct);
        return reversed.ToHashSet();
    }

    /// <summary>Marks dirty the months of the owners whose current version was cut before a line just posted.</summary>
    private async Task MarkDirtyAsync(Guid orgId, Period period, IReadOnlyCollection<Guid> owners, DateTime now, CancellationToken ct)
    {
        if (owners.Count == 0)
            return;
        var ids = owners.ToList();
        var withVersion = await db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.OrgId == orgId && v.PeriodYear == period.Year && v.PeriodMonth == period.Month && ids.Contains(v.PropertyOwnerId)
                && v.Status != OwnerStatementVersionStatus.Superseded)
            .Select(v => v.PropertyOwnerId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var owner in withVersion)
        {
            var row = await PeriodRowAsync(orgId, owner, period, now, ct);
            if (row.IsDirty)
                continue;
            row.IsDirty = true;
            row.DirtySince = now;
            row.UpdatedAt = now;
        }
    }

    /// <summary>The month row of the owner, tracked, created open when there is none.</summary>
    private async Task<OwnerClosePeriod> PeriodRowAsync(Guid orgId, Guid ownerId, Period period, DateTime now, CancellationToken ct)
    {
        var row = db.OwnerClosePeriods.Local.FirstOrDefault(p => p.OrgId == orgId && p.PropertyOwnerId == ownerId && p.PeriodYear == period.Year && p.PeriodMonth == period.Month)
            ?? await db.OwnerClosePeriods.FirstOrDefaultAsync(
                p => p.OrgId == orgId && p.PropertyOwnerId == ownerId && p.PeriodYear == period.Year && p.PeriodMonth == period.Month, ct);
        if (row is not null)
            return row;
        row = new OwnerClosePeriod
        {
            OrgId = orgId,
            PropertyOwnerId = ownerId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.OwnerClosePeriods.Add(row);
        return row;
    }

    /// <summary>The reversal of a line: the same facts with the opposite amounts, in the month of the line or in <paramref name="into"/>.</summary>
    private static OwnerLedgerEntry Reversal(OwnerLedgerEntry original, Period? into, DateTime now, string? userId, string? description = null) => new()
    {
        OrgId = original.OrgId,
        PropertyOwnerId = original.PropertyOwnerId,
        PropertyId = original.PropertyId,
        EntryType = original.EntryType,
        SourceType = original.SourceType,
        SourceId = original.SourceId,
        SourceReference = original.SourceReference,
        SourceDocumentId = original.SourceDocumentId,
        AttachmentReference = original.AttachmentReference,
        IdempotencyKey = "rev:" + original.Id.ToString("N"),
        ServiceDate = original.ServiceDate,
        RecognitionDate = into?.First ?? original.RecognitionDate,
        PaymentDate = original.PaymentDate,
        PeriodYear = into?.Year ?? original.PeriodYear,
        PeriodMonth = into?.Month ?? original.PeriodMonth,
        PropertyOwnershipId = original.PropertyOwnershipId,
        SharePercent = original.SharePercent,
        OriginalCurrency = original.OriginalCurrency,
        SourceAmount = -original.SourceAmount,
        OriginalAmount = -original.OriginalAmount,
        StatementCurrency = original.StatementCurrency,
        FxRate = original.FxRate,
        FxSource = original.FxSource,
        FxDate = original.FxDate,
        TaxCode = original.TaxCode,
        RuleVersion = original.RuleVersion,
        Amount = -original.Amount,
        ReversesEntryId = original.Id,
        Description = Truncate(description, OwnerLedgerEntry.TextMaxLength) ?? original.Description,
        PostedAt = now,
        PostedByUserId = userId,
    };

    /// <summary>The reversal of a line in its own month, or in the month of <paramref name="into"/> (the payouts, E06/F05/US02/T03).</summary>
    internal static OwnerLedgerEntry ReversalOf(OwnerLedgerEntry original, DateOnly? into, DateTime now, string? userId, string? description) =>
        Reversal(original, into is { } day ? Period.Of(day) : null, now, userId, description);

    /// <summary>SHA-256 (hex) of the ids and amounts of the lines, by posting time then id: the proof a version is made of these lines.</summary>
    internal static string Hash(IEnumerable<OwnerLedgerEntry> entries)
    {
        var text = string.Join('\n', entries
            .OrderBy(e => e.PostedAt)
            .ThenBy(e => e.Id)
            .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Id:N}:{e.Amount:0.00}")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The key without the suffix of a re-posting.</summary>
    private static string BaseKey(string key)
    {
        var hash = key.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? key : key[..hash];
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.ToEven);

    private static string? Truncate(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private Task<IDbContextTransaction?> LockAsync(Guid orgId, CancellationToken ct) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.OwnerAccounting, orgId.ToString("N")));

    private async Task EnsureOwnerAsync(Guid orgId, Guid ownerId, CancellationToken ct)
    {
        if (!await db.PropertyOwners.AsNoTracking().AnyAsync(o => o.Id == ownerId && o.OrgId == orgId, ct))
            throw new NotFoundException($"Owner {ownerId} was not found") { Code = OwnerFiscalErrors.OwnerNotFound, MessageKey = "OwnerNotFound" };
    }

    /// <summary>A month of the owner accounting.</summary>
    private readonly record struct Period(int Year, int Month)
    {
        public DateOnly First => new(Year, Month, 1);

        public DateOnly Last => First.AddMonths(1).AddDays(-1);

        public DateTime StartUtc => new(Year, Month, 1, 0, 0, 0, DateTimeKind.Utc);

        public DateTime EndUtc => StartUtc.AddMonths(1);

        public bool Contains(DateOnly day) => day >= First && day <= Last;

        public bool IsBefore(int year, int month) => Year < year || (Year == year && Month < month);

        public static Period Of(int year, int month) =>
            year is < 2000 or > 2100 || month is < 1 or > 12
                ? throw new DomainRuleException(OwnerAccountingErrors.PeriodInvalid, "OwnerPeriodInvalid")
                : new Period(year, month);

        public static Period Of(DateOnly day) => new(day.Year, day.Month);
    }
}
