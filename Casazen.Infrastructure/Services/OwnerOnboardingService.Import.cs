using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public sealed partial class OwnerOnboardingService
{
    /// <summary>The longest text of the reference a row names its property by that the answer echoes.</summary>
    private const int MaxReferenceLength = 64;

    public async Task<OwnerImportResult> ImportAsync(
        HostScope scope,
        string actorUserId,
        string fileName,
        byte[] content,
        bool dryRun,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentNullException.ThrowIfNull(content);
        if (actorUserId.Length > OrgActivityCatalog.MaxIdLength)
            throw new ArgumentException("Not a user id.", nameof(actorUserId));

        var parsed = OwnerImportParser.Parse(OnboardingBulkImportService.ReadTable(fileName, content));
        var fingerprint = OwnerImportParser.Fingerprint(scope.OrgId, parsed.Rows);

        // A write holds the lock of the owners of the org, and the file is judged only after it is taken: a second import of the same file waits,
        // then finds what the first wrote and has nothing left to do. A dry run reads only.
        await using var transaction = dryRun
            ? null
            : await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                db, ct, (PostgresAdvisoryLocks.Scope.OwnerOnboarding, scope.OrgId.ToString("N")));

        var judged = await JudgeAsync(scope, parsed, ct);
        if (!dryRun)
        {
            await ApplyAsync(scope, actorUserId, fingerprint, judged, ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }

        var rows = judged.Rows.Select(row => ToAnswer(row, dryRun)).ToList();
        var valid = judged.Rows.Where(r => r.Valid).ToList();
        var summary = new OwnerImportSummary(
            rows.Count,
            valid.Count,
            rows.Count - valid.Count,
            judged.Rows.Count(r => r.Applied),
            valid.Select(r => r.Plan).Where(p => p is { IsNew: true }).Distinct().Count(),
            valid.Select(r => r.Plan).Where(p => p is { IsNew: false }).Distinct().Count(),
            valid.Count(r => r.OwnershipAction == OwnerImportActions.CreateOwnership),
            valid.Count(r => r.OwnershipAction == OwnerImportActions.UpdateFuturePeriod));

        // Counts only: no cell of the file, no name, no identifier.
        logger.LogInformation(
            "Owner import for org {OrgId}: {Rows} rows, {Valid} valid, {Applied} written, nothing written {DryRun}",
            scope.OrgId,
            summary.Rows,
            summary.Valid,
            summary.Applied,
            dryRun);
        return new OwnerImportResult(dryRun, fingerprint, summary, parsed.UnknownColumns, rows, judged.PropertyIssues);
    }

    // ---------------------------------------------------------------- the answer

    private static OwnerImportRow ToAnswer(OwnerRowState row, bool dryRun)
    {
        var ownerKey = row.Values.Key ?? (row.Source.OwnerKey is { } raw ? Clip(raw) : null);
        var ownerId = row.ExistingOwner?.Id ?? (!dryRun && row.Applied && row.Plan is { IsNew: true } plan ? plan.Id : (Guid?)null);
        return new OwnerImportRow(
            row.Source.Number,
            row.PropertyRef,
            row.PropertyId,
            ownerKey,
            ownerId,
            row.MatchedBy,
            row.Valid ? OwnerImportRowCodes.Ok : row.Errors[0].Code,
            [.. row.Errors],
            row.OwnerAction,
            row.OwnershipAction,
            [.. row.Candidates.Take(OwnerOnboardingLimits.MaxCandidates).Select(c => new OwnerImportCandidate(c.Id, c.ExternalKey, OwnerFieldRules.DisplayName(c)))],
            row.Applied);
    }

    private static string Clip(string value) => value.Length <= MaxReferenceLength ? value : value[..MaxReferenceLength];

    // ---------------------------------------------------------------- judging

    private sealed record PropertyFacts(Guid Id, string? CinCode);

    private sealed record MandateRow(Guid OwnershipId, DateOnly From, DateOnly? Until);

    private sealed class Judged
    {
        public List<OwnerRowState> Rows { get; } = [];

        public List<OwnerImportPropertyIssue> PropertyIssues { get; } = [];
    }

    private sealed class OwnerLookups(IReadOnlyCollection<PropertyOwner> owners)
    {
        public Dictionary<Guid, PropertyOwner> ById { get; } = owners.ToDictionary(o => o.Id);

        public Dictionary<string, PropertyOwner> ByKey { get; } = owners
            .Where(o => !string.IsNullOrWhiteSpace(o.ExternalKey))
            .GroupBy(o => o.ExternalKey!.Trim().ToUpperInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        public Dictionary<string, List<PropertyOwner>> ByFiscal { get; } = owners
            .Where(o => OwnerFieldRules.NormalizeFiscalCode(o.FiscalCode).Length > 0)
            .GroupBy(o => OwnerFieldRules.NormalizeFiscalCode(o.FiscalCode), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        public Dictionary<string, List<PropertyOwner>> ByVat { get; } = owners
            .Where(o => OwnerFieldRules.NormalizeVat(o.VatNumber).Length > 0)
            .GroupBy(o => OwnerFieldRules.NormalizeVat(o.VatNumber), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        /// <summary>The keys of the org that belong to an owner outside the reach of the caller: a key the caller cannot take.</summary>
        public HashSet<string> HiddenKeys { get; } = new(StringComparer.Ordinal);
    }

    private async Task<Judged> JudgeAsync(HostScope scope, ParsedOwnerImport parsed, CancellationToken ct)
    {
        var today = Today();
        var judged = new Judged();
        foreach (var source in parsed.Rows)
        {
            var state = new OwnerRowState(source);
            OwnerRowRules.ParseValues(state);
            judged.Rows.Add(state);
        }

        await ResolvePropertiesAsync(scope, judged.Rows, ct);

        // A caller with a partial reach writes nothing that is tied to no property it reaches: a row with no property would create or complete an
        // owner of the whole org, and no declaration is never inside the reach.
        if (!scope.IsOrgWide)
        {
            foreach (var row in judged.Rows.Where(r => !r.Source.NamesProperty))
                row.Fail(OwnerImportColumns.PropertyId, OwnerImportRowCodes.PropertyNotFound);
        }

        var owners = await LoadVisibleOwnersAsync(scope, ct);
        var lookups = new OwnerLookups(owners);
        await CollectHiddenKeysAsync(scope, judged.Rows, lookups, ct);
        var sharedOutsideReach = await OwnersSharedOutsideReachAsync(scope, owners, ct);

        var propertyIds = judged.Rows.Where(r => r.PropertyId is not null).Select(r => r.PropertyId!.Value).Distinct().ToList();
        var periods = propertyIds.Count == 0
            ? []
            : await db.PropertyOwnerships
                .AsNoTracking()
                .Where(po => po.OrgId == scope.OrgId && propertyIds.Contains(po.PropertyId))
                .Select(po => new PeriodRow(po.Id, po.PropertyId, po.PropertyOwnerId, po.SharePercent, po.OwnershipStart, po.OwnershipEnd))
                .ToListAsync(ct);
        var periodsByProperty = periods.ToLookup(p => p.PropertyId);
        var ownershipIds = periods.Select(p => p.Id).ToList();
        var mandatesByOwnership = (ownershipIds.Count == 0
                ? []
                : await db.OwnerMandates
                    .AsNoTracking()
                    .Where(m => m.OrgId == scope.OrgId && ownershipIds.Contains(m.PropertyOwnershipId))
                    .Select(m => new { m.PropertyOwnershipId, m.EffectiveFrom, m.EffectiveUntil })
                    .ToListAsync(ct))
            .ToLookup(
                m => m.PropertyOwnershipId,
                m => new MandateRow(
                    m.PropertyOwnershipId,
                    RomeCalendar.DateInRome(m.EffectiveFrom),
                    m.EffectiveUntil is { } until ? RomeCalendar.DateInRome(until) : null));

        // The owners of the file, row after row: the same owner named twice is one owner, and what a row gives is checked against what the
        // owner holds and against what the earlier valid rows gave it.
        var plansByExisting = new Dictionary<Guid, OwnerPlan>();
        var newByKey = new Dictionary<string, OwnerPlan>(StringComparer.Ordinal);
        var newByFiscal = new Dictionary<string, OwnerPlan>(StringComparer.Ordinal);
        var newByVat = new Dictionary<string, OwnerPlan>(StringComparer.Ordinal);
        var seenPeriods = new HashSet<(Guid Property, Guid Owner, DateOnly Start)>();

        foreach (var row in judged.Rows)
        {
            ResolveExistingOwner(row, lookups);
            var storedResidence = row.ExistingOwner?.TaxResidenceCountry;
            OwnerRowRules.ValidateIdentifiers(row, row.Values.TaxResidence ?? storedResidence);
            if (!row.Valid)
                continue;

            if (!AssignPlan(row, plansByExisting, newByKey, newByFiscal, newByVat, sharedOutsideReach))
                continue;

            ClassifyOwnership(row, today, periodsByProperty, mandatesByOwnership, seenPeriods);
        }

        CheckShares(judged, today, periodsByProperty);
        return judged;
    }

    /// <summary>
    /// The property of each row: by id or by CIN, among the properties of the caller's org inside its reach. A property of another org, one outside
    /// the reach and one that does not exist are one and the same status.
    /// </summary>
    private async Task ResolvePropertiesAsync(HostScope scope, List<OwnerRowState> rows, CancellationToken ct)
    {
        var wantedIds = new HashSet<Guid>();
        var wantedCins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.Source.NamesProperty))
        {
            if (row.Source.PropertyId is { } rawId)
            {
                if (Guid.TryParse(rawId, out var id) && id != Guid.Empty)
                    wantedIds.Add(id);
            }
            else if (CinFormat.Normalize(row.Source.Cin) is { } cin)
            {
                wantedCins.Add(cin);
            }
        }

        var reachable = db.Properties.AsNoTracking().Where(p => p.OrgId == scope.OrgId).InScope(scope);
        var byId = wantedIds.Count == 0
            ? new Dictionary<Guid, PropertyFacts>()
            : await reachable
                .Where(p => wantedIds.Contains(p.Id))
                .Select(p => new PropertyFacts(p.Id, p.CinCode))
                .ToDictionaryAsync(p => p.Id, ct);
        var byCin = wantedCins.Count == 0
            ? new Dictionary<string, List<PropertyFacts>>()
            : (await reachable
                    .Where(p => p.CinCode != null && wantedCins.Contains(p.CinCode))
                    .Select(p => new PropertyFacts(p.Id, p.CinCode))
                    .ToListAsync(ct))
                .GroupBy(p => p.CinCode!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var row in rows.Where(r => r.Source.NamesProperty))
        {
            var column = OwnerImportColumns.PropertyId;
            PropertyFacts? found = null;
            var ambiguous = false;
            if (row.Source.PropertyId is { } rawId)
            {
                row.PropertyRef = Clip(rawId);
                if (Guid.TryParse(rawId, out var id) && byId.TryGetValue(id, out var property))
                    found = property;
            }
            else
            {
                column = OwnerImportColumns.CinCode;
                var cin = CinFormat.Normalize(row.Source.Cin);
                row.PropertyRef = Clip(cin ?? row.Source.Cin!);
                if (cin is not null && byCin.TryGetValue(cin, out var matches))
                {
                    if (matches.Count == 1)
                        found = matches[0];
                    else
                        ambiguous = true;
                }
            }

            if (ambiguous)
                row.Fail(column, OwnerImportRowCodes.PropertyAmbiguous);
            else if (found is null)
                row.Fail(column, OwnerImportRowCodes.PropertyNotFound);
            else
                row.PropertyId = found.Id;
        }
    }

    /// <summary>
    /// For a caller with a partial reach: the keys of the org that belong to an owner it cannot see. A row that tries to take one is a conflict of
    /// identity, not a key that is free (the unique index would refuse it anyway).
    /// </summary>
    private async Task CollectHiddenKeysAsync(HostScope scope, List<OwnerRowState> rows, OwnerLookups lookups, CancellationToken ct)
    {
        if (scope.IsOrgWide)
            return;

        var keys = rows.Where(r => r.Values.Key is not null && !lookups.ByKey.ContainsKey(r.Values.Key)).Select(r => r.Values.Key!).Distinct().ToList();
        if (keys.Count == 0)
            return;

        var taken = await db.PropertyOwners
            .AsNoTracking()
            .Where(o => o.OrgId == scope.OrgId && o.ExternalKey != null && keys.Contains(o.ExternalKey))
            .Select(o => o.ExternalKey!)
            .ToListAsync(ct);
        foreach (var key in taken)
            lookups.HiddenKeys.Add(key);
    }

    /// <summary>
    /// For a caller with a partial reach: the owners it sees that also hold a period on a property it does not reach. Empty for an org-wide scope.
    /// </summary>
    private async Task<HashSet<Guid>> OwnersSharedOutsideReachAsync(HostScope scope, List<PropertyOwner> visible, CancellationToken ct)
    {
        if (scope.IsOrgWide)
            return [];

        var ids = visible.Select(o => o.Id).ToList();
        var reachable = db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).Select(p => p.Id);
        var outside = await db.PropertyOwnerships
            .AsNoTracking()
            .Where(po => po.OrgId == scope.OrgId && ids.Contains(po.PropertyOwnerId) && !reachable.Contains(po.PropertyId))
            .Select(po => po.PropertyOwnerId)
            .Distinct()
            .ToListAsync(ct);
        return [.. outside];
    }

    /// <summary>
    /// Who in the registry the row is about, in this order: the <c>ownerId</c> of the row, the key, the fiscal identifier. The e-mail address is
    /// never looked at. A fiscal identifier that matches more than one owner is ambiguous; a key and a fiscal identifier that point to two owners,
    /// or to an owner that holds other ones, are a conflict of identity.
    /// </summary>
    private static void ResolveExistingOwner(OwnerRowState row, OwnerLookups lookups)
    {
        var values = row.Values;
        PropertyOwner? found = null;
        string? matchedBy = null;

        if (row.Source.OwnerId is { } rawOwnerId)
        {
            if (!Guid.TryParse(rawOwnerId, out var ownerId) || !lookups.ById.TryGetValue(ownerId, out found))
            {
                row.Fail(OwnerImportColumns.OwnerId, OwnerImportRowCodes.OwnerNotFound);
                return;
            }

            matchedBy = OwnerMatchKinds.OwnerId;
        }

        if (found is null && values.Key is { } key)
        {
            if (lookups.ByKey.TryGetValue(key, out found))
            {
                matchedBy = OwnerMatchKinds.ExternalKey;
            }
            else if (lookups.HiddenKeys.Contains(key))
            {
                row.Fail(OwnerImportColumns.OwnerKey, OwnerImportRowCodes.OwnerIdentityConflict);
                return;
            }
        }

        if (found is null && (values.FiscalCode is not null || values.Vat is not null))
        {
            var candidates = new List<PropertyOwner>();
            if (values.FiscalCode is { } fiscal && lookups.ByFiscal.TryGetValue(fiscal, out var byFiscal))
                candidates.AddRange(byFiscal);

            if (values.Vat is { } vat && lookups.ByVat.TryGetValue(vat, out var byVat))
                candidates.AddRange(byVat.Where(o => candidates.All(c => c.Id != o.Id)));

            if (candidates.Count == 1)
            {
                found = candidates[0];
                matchedBy = OwnerMatchKinds.FiscalIdentifier;
            }
            else if (candidates.Count > 1)
            {
                row.Candidates.AddRange(candidates.OrderBy(c => c.Id).Take(OwnerOnboardingLimits.MaxCandidates));
                row.Fail(values.FiscalCode is not null ? OwnerImportColumns.FiscalCode : OwnerImportColumns.VatNumber, OwnerImportRowCodes.OwnerAmbiguous);
                row.OwnerAction = OwnerImportActions.Conflict;
                return;
            }
        }

        if (found is null)
            return;

        row.ExistingOwner = found;
        row.MatchedBy = matchedBy;
        row.OwnerAction = OwnerImportActions.MatchOwner;

        // The identity the row gives has to be the identity the owner has: a key or a fiscal identifier that is somebody else's is a conflict.
        var held = OwnerRowRules.ValuesOf(found);
        foreach (var (column, code) in OwnerRowRules.Conflicts(held, values).Where(c => c.Code == OwnerImportRowCodes.OwnerIdentityConflict))
            row.Fail(column, code);

        // A key the owner does not have yet cannot be one another owner has (or one outside the reach).
        if (held.Key is null && values.Key is { } wanted && (lookups.ByKey.ContainsKey(wanted) || lookups.HiddenKeys.Contains(wanted)))
            row.Fail(OwnerImportColumns.OwnerKey, OwnerImportRowCodes.OwnerIdentityConflict);

        if (!row.Valid)
            row.OwnerAction = OwnerImportActions.Conflict;
    }

    /// <summary>
    /// Gives the row its owner of the file: the stored one, or one that the file creates (found again by key or fiscal identifier in a later
    /// row), and merges what the row says into what the owner holds, refusing a different value. False when the row cannot go on.
    /// </summary>
    private static bool AssignPlan(
        OwnerRowState row,
        Dictionary<Guid, OwnerPlan> plansByExisting,
        Dictionary<string, OwnerPlan> newByKey,
        Dictionary<string, OwnerPlan> newByFiscal,
        Dictionary<string, OwnerPlan> newByVat,
        ISet<Guid> sharedOutsideReach)
    {
        var values = row.Values;
        OwnerPlan plan;
        OwnerValues held;

        if (row.ExistingOwner is { } existing)
        {
            if (!plansByExisting.TryGetValue(existing.Id, out plan!))
            {
                plan = new OwnerPlan(existing.Id, existing) { FirstRow = row.Source.Number };
                plansByExisting[existing.Id] = plan;
            }

            // An owner that also holds properties outside the reach of the caller is not the caller's to complete: the row may add a period, nothing
            // the owner lacks.
            if (sharedOutsideReach.Contains(existing.Id) && OwnerRowRules.Fill(OwnerRowRules.ValuesOf(existing), values))
            {
                row.Fail(OwnerImportColumns.OwnerId, OwnerImportRowCodes.OwnerSharedOutsideReach);
                row.OwnerAction = OwnerImportActions.Conflict;
                return false;
            }

            // What the owner holds is what is stored, then what earlier rows of the file gave it.
            held = OwnerRowRules.ValuesOf(existing);
            OwnerRowRules.Fill(held, plan.Given);
        }
        else
        {
            // An e-mail address identifies nobody: a new owner needs a key or a fiscal identifier of its own, or two imports of the same file
            // would create it twice.
            if (values.Key is null && values.FiscalCode is null && values.Vat is null)
            {
                row.Fail(OwnerImportColumns.OwnerKey, OwnerImportRowCodes.OwnerIdentityMissing);
                return false;
            }

            var found = new List<OwnerPlan>();
            AddPlan(found, values.Key, newByKey);
            AddPlan(found, values.FiscalCode, newByFiscal);
            AddPlan(found, values.Vat, newByVat);
            if (found.Count > 1)
            {
                row.Fail(OwnerImportColumns.OwnerKey, OwnerImportRowCodes.OwnerIdentityConflict);
                row.OwnerAction = OwnerImportActions.Conflict;
                return false;
            }

            if (found.Count == 1)
            {
                plan = found[0];
                row.MatchedBy = values.Key is not null && newByKey.TryGetValue(values.Key, out var byKey) && byKey == plan
                    ? OwnerMatchKinds.ExternalKey
                    : OwnerMatchKinds.FiscalIdentifier;
                row.OwnerAction = OwnerImportActions.MatchOwner;
            }
            else
            {
                if (values.LegalName is null)
                {
                    row.Fail(OwnerImportColumns.LegalName, OwnerImportRowCodes.LegalNameRequired);
                    return false;
                }

                plan = new OwnerPlan(Guid.NewGuid(), null) { FirstRow = row.Source.Number };
                row.OwnerAction = OwnerImportActions.CreateOwner;
            }

            held = plan.Given;
        }

        foreach (var (column, code) in OwnerRowRules.Conflicts(held, values))
            row.Fail(column, code);

        if (!row.Valid)
        {
            row.OwnerAction = OwnerImportActions.Conflict;
            return false;
        }

        // Nothing of a row that was refused reaches the owner: its values are merged only now.
        OwnerRowRules.Fill(plan.Given, values);
        if (plan.IsNew)
        {
            Index(newByKey, plan.Given.Key, plan);
            Index(newByFiscal, plan.Given.FiscalCode, plan);
            Index(newByVat, plan.Given.Vat, plan);
        }

        row.Plan = plan;
        return true;
    }

    private static void AddPlan(List<OwnerPlan> found, string? token, Dictionary<string, OwnerPlan> index)
    {
        if (token is not null && index.TryGetValue(token, out var plan) && !found.Contains(plan))
            found.Add(plan);
    }

    private static void Index(Dictionary<string, OwnerPlan> index, string? token, OwnerPlan plan)
    {
        if (token is not null)
            index[token] = plan;
    }

    /// <summary>
    /// What the row does with the ownership. A period is found by its property, owner and first day: the same one is <c>unchanged</c>; a new one is
    /// created; one that already ended is history and a conflict; one in force can only be closed (or its end moved to today or later), its share
    /// never changes; one that has not started can change. A stored mandate must stay inside the period.
    /// </summary>
    private static void ClassifyOwnership(
        OwnerRowState row,
        DateOnly today,
        ILookup<Guid, PeriodRow> periodsByProperty,
        ILookup<Guid, MandateRow> mandatesByOwnership,
        HashSet<(Guid Property, Guid Owner, DateOnly Start)> seen)
    {
        if (!row.IsOwnershipRow || row.PropertyId is not { } propertyId || row.Plan is not { } plan)
            return;

        var column = row.Source.PropertyId is not null ? OwnerImportColumns.PropertyId : OwnerImportColumns.CinCode;
        if (!seen.Add((propertyId, plan.Id, row.Start)))
        {
            row.Fail(column, OwnerImportRowCodes.DuplicateRow);
            return;
        }

        var existing = plan.IsNew
            ? null
            : periodsByProperty[propertyId].FirstOrDefault(p => p.OwnerId == plan.Id && RomeCalendar.DateInRome(p.Start) == row.Start);
        if (existing is null)
        {
            row.OwnershipAction = OwnerImportActions.CreateOwnership;
            return;
        }

        row.ExistingPeriodId = existing.Id;
        var storedEnd = existing.End is { } end ? RomeCalendar.DateInRome(end) : (DateOnly?)null;
        var sameShare = existing.SharePercent == row.Share;
        var sameEnd = storedEnd == row.End;
        if (sameShare && sameEnd)
        {
            row.OwnershipAction = OwnerImportActions.Unchanged;
            return;
        }

        var history = storedEnd is { } stored && stored < today;
        var inForce = !history && row.Start <= today;
        var rewrites = history
            || (inForce && !sameShare)
            || (inForce && row.End is { } newEnd && newEnd < today);
        if (rewrites)
        {
            row.OwnershipAction = OwnerImportActions.Conflict;
            row.Fail(OwnerImportColumns.OwnershipStart, OwnerImportRowCodes.OwnershipPeriodConflict);
            return;
        }

        row.OwnershipAction = OwnerImportActions.UpdateFuturePeriod;
        if (mandatesByOwnership[existing.Id].Any(m => OwnerReadinessRules.IsOutside(m.From, m.Until, row.Start, row.End)))
        {
            row.OwnershipAction = OwnerImportActions.Conflict;
            row.Fail(OwnerImportColumns.OwnershipEnd, OwnerImportRowCodes.MandateOutsideOwnership);
        }
    }

    /// <summary>
    /// The shares of each property the file touches, as they would be after the file: the stored periods (those the file changes in their new
    /// form) and the valid rows, from the first day the file touches. An owner twice on the same days fails the rows of that owner; shares that
    /// are not exactly 100.00 on every day fail every valid row of the property, and none of them is written.
    /// </summary>
    private static void CheckShares(Judged judged, DateOnly today, ILookup<Guid, PeriodRow> periodsByProperty)
    {
        foreach (var group in judged.Rows.Where(r => r.Valid && r.IsOwnershipRow && r.PropertyId is not null).GroupBy(r => r.PropertyId!.Value))
        {
            var rows = group.ToList();
            var final = new List<OwnershipPeriod>();
            foreach (var stored in periodsByProperty[group.Key])
            {
                var changedBy = rows.FirstOrDefault(r => r.ExistingPeriodId == stored.Id && r.OwnershipAction == OwnerImportActions.UpdateFuturePeriod);
                final.Add(changedBy is null
                    ? ToPeriod(stored)
                    : new OwnershipPeriod(stored.OwnerId, changedBy.Share, changedBy.Start, changedBy.End));
            }

            final.AddRange(rows
                .Where(r => r.OwnershipAction == OwnerImportActions.CreateOwnership)
                .Select(r => new OwnershipPeriod(r.Plan!.Id, r.Share, r.Start, r.End)));

            var issues = OwnerOwnershipTimeline.Analyze(final, rows.Min(r => r.Start), today);
            if (issues.Count == 0)
                continue;

            foreach (var overlap in issues.Where(i => i.Kind == OwnershipIssueKind.OwnerOverlap))
            {
                foreach (var row in rows.Where(r => r.Plan!.Id == overlap.OwnerId && r.Start <= (overlap.To ?? DateOnly.MaxValue) && (r.End ?? DateOnly.MaxValue) >= overlap.From))
                    row.Fail(OwnerImportColumns.OwnershipStart, OwnerImportRowCodes.OwnershipOverlap);
            }

            var shares = issues.Where(i => i.Kind != OwnershipIssueKind.OwnerOverlap).ToList();
            if (shares.Count > 0)
            {
                foreach (var row in rows)
                    row.Fail(OwnerImportColumns.SharePercent, OwnerImportRowCodes.OwnershipSharesInvalid);

                judged.PropertyIssues.Add(new OwnerImportPropertyIssue(group.Key, shares));
            }
        }
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Writes the valid rows in one unit of work, inside the lock of the org: the owners the file creates, what the owners it matches lack (an
    /// empty field is filled, a value is never overwritten), and the periods; and the line of the activity log. The property of a row is looked
    /// up again in the same reach: one that has gone since the judging is <c>property_not_found</c> and nothing of its rows is written.
    /// </summary>
    private async Task ApplyAsync(HostScope scope, string actorUserId, string fingerprint, Judged judged, CancellationToken ct)
    {
        var now = Now();
        var ownershipRows = judged.Rows.Where(r => r.Valid && r.IsOwnershipRow && r.PropertyId is not null).ToList();
        if (ownershipRows.Count > 0)
        {
            var wanted = ownershipRows.Select(r => r.PropertyId!.Value).Distinct().ToList();
            var still = (await db.Properties
                    .AsNoTracking()
                    .Where(p => p.OrgId == scope.OrgId && wanted.Contains(p.Id))
                    .InScope(scope)
                    .Select(p => p.Id)
                    .ToListAsync(ct))
                .ToHashSet();
            foreach (var row in ownershipRows.Where(r => !still.Contains(r.PropertyId!.Value)))
            {
                row.Fail(row.Source.PropertyId is not null ? OwnerImportColumns.PropertyId : OwnerImportColumns.CinCode, OwnerImportRowCodes.PropertyNotFound);
                row.OwnershipAction = null;
            }
        }

        var valid = judged.Rows.Where(r => r.Valid).ToList();

        // 1. The owners: each one the valid rows name, once, with what those rows gave it.
        foreach (var plan in valid.Select(r => r.Plan!).Distinct())
        {
            var rows = valid.Where(r => r.Plan == plan).ToList();
            var given = new OwnerValues();
            foreach (var row in rows)
                OwnerRowRules.Fill(given, row.Values);

            if (plan.IsNew)
            {
                var owner = new PropertyOwner
                {
                    Id = plan.Id,
                    OrgId = scope.OrgId,
                    CreatedAt = now,
                    FirstName = string.Empty,
                    LastName = string.Empty,
                    Email = string.Empty,
                };
                OwnerRowRules.FillEntity(owner, given, creating: true);

                // Through the DbSet: the Guid of the row is preset, so a row attached any other way would be tracked as modified.
                db.PropertyOwners.Add(owner);
                rows[0].Applied = true;
                continue;
            }

            var stored = await db.PropertyOwners.FirstOrDefaultAsync(o => o.Id == plan.Id && o.OrgId == scope.OrgId, ct);
            if (stored is null)
            {
                foreach (var row in rows)
                    row.Fail(OwnerImportColumns.OwnerId, OwnerImportRowCodes.OwnerNotFound);

                continue;
            }

            if (OwnerRowRules.FillEntity(stored, given, creating: false))
                rows[0].Applied = true;
        }

        // 2. The periods.
        foreach (var row in valid.Where(r => r.Valid && r.IsOwnershipRow))
        {
            if (row.OwnershipAction == OwnerImportActions.CreateOwnership)
            {
                db.PropertyOwnerships.Add(new PropertyOwnership
                {
                    OrgId = scope.OrgId,
                    PropertyId = row.PropertyId!.Value,
                    PropertyOwnerId = row.Plan!.Id,
                    SharePercent = row.Share,
                    OwnershipStart = StartInstant(row.Start),
                    OwnershipEnd = row.End is { } end ? EndInstant(end) : null,
                });
                row.Applied = true;
            }
            else if (row.OwnershipAction == OwnerImportActions.UpdateFuturePeriod)
            {
                var period = await db.PropertyOwnerships.FirstOrDefaultAsync(p => p.Id == row.ExistingPeriodId && p.OrgId == scope.OrgId, ct);
                if (period is null)
                {
                    row.Fail(OwnerImportColumns.OwnershipStart, OwnerImportRowCodes.OwnershipPeriodConflict);
                    continue;
                }

                period.SharePercent = row.Share;
                period.OwnershipEnd = row.End is { } end ? EndInstant(end) : null;
                row.Applied = true;
            }
        }

        var applied = judged.Rows.Count(r => r.Applied && r.Valid);
        if (applied > 0)
        {
            activityLog.Record(OrgActivity.Of(
                scope.OrgId,
                OrgActivityType.OwnerImportApplied,
                actorUserId,
                scope.OrgId.ToString("D"),
                (OrgActivityDetailKeys.Rows, judged.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (OrgActivityDetailKeys.Applied, applied.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (OrgActivityDetailKeys.Fingerprint, fingerprint[..32])));
        }

        await db.SaveChangesAsync(ct);
    }
}
