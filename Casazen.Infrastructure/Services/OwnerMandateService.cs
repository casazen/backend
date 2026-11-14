using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerMandateService"/>
/// <remarks>
/// The writes of the mandates and of the channel rules run under the lock of the owner accounting of the org (<see cref="PostgresAdvisoryLocks.Scope.OwnerAccounting"/>),
/// like a posting: two versions written at once never both stay open, and a posting never reads half a list. Logs carry ids and counts: never an address.
/// </remarks>
public sealed partial class OwnerMandateService(
    AppDbContext db,
    TimeProvider time,
    IOptions<OwnerAccountingOptions> options,
    ILogger<OwnerMandateService> logger) : IOwnerMandateService
{
    /// <summary>The highest amount of a decimal(10,2) column: a flat fee above it is a typing mistake.</summary>
    private const decimal MaxFlatRate = 99_999_999.99m;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9/_.-]*$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex StorageKeyShape();

    public async Task<IReadOnlyList<OwnerMandate>> ListAsync(HostScope scope, Guid ownerId, CancellationToken ct = default)
    {
        await RequireOwnerInReachAsync(scope, ownerId, ct);
        return await db.OwnerMandates.AsNoTracking()
            .Where(m => m.OrgId == scope.OrgId && m.PropertyOwnerId == ownerId)
            .OrderByDescending(m => m.ValidFrom)
            .ThenByDescending(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<OwnerMandate> CreateVersionAsync(HostScope scope, Guid ownerId, OwnerMandateCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var terms = Validated(command);
        await RequireOwnerInReachAsync(scope, ownerId, ct);

        await using var tx = await LockAsync(scope.OrgId, ct);
        var versions = await db.OwnerMandates
            .Where(m => m.OrgId == scope.OrgId && m.PropertyOwnerId == ownerId && m.PropertyOwnershipId == null)
            .ToListAsync(ct);
        if (versions.Any(v => v.ValidFrom >= terms.ValidFrom))
            throw new DomainConflictException(OwnerMandateErrors.MandateOverlap, "OwnerMandateOverlap");

        // The version in force before the new one ends the day before it starts: the versions of an owner never overlap.
        foreach (var open in versions.Where(v => v.ValidTo is null || v.ValidTo >= terms.ValidFrom))
            open.ValidTo = terms.ValidFrom.AddDays(-1);

        var mandate = NewMandate(scope.OrgId, ownerId, terms, userId);
        db.OwnerMandates.Add(mandate);
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);

        logger.LogInformation(
            "Mandate {MandateId} of owner {OwnerId} in org {OrgId} saved from {ValidFrom} by {UserId}",
            mandate.Id, ownerId, scope.OrgId, mandate.ValidFrom, userId);
        return mandate;
    }

    public async Task<OwnerMandateTemplateResult> ApplyTemplateAsync(HostScope scope, OwnerMandateTemplateCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Template is null)
            throw MandateInvalid("template");

        var terms = Validated(command.Template);
        if (command.OwnerIds is { Count: 0 })
            throw new DomainRuleException(OwnerMandateErrors.TemplateOwnersEmpty, "OwnerMandateOwnersEmpty");

        var today = time.TodayInRomeAsDateOnly();
        await using var tx = await LockAsync(scope.OrgId, ct);

        // The candidates: the listed owners (each must be in reach: an owner outside it is the 404 of an owner that does not exist), or every owner of the org
        // in reach that is not anonymized.
        List<Guid> candidates;
        if (command.OwnerIds is { } listed)
        {
            candidates = listed.Distinct().ToList();
            var inReach = await OwnersInReachAsync(scope, candidates, ct);
            var outside = candidates.Where(id => !inReach.Contains(id)).ToList();
            if (outside.Count > 0)
                throw OwnerNotFound(outside[0]);
        }
        else
        {
            var all = await db.PropertyOwners.AsNoTracking()
                .Where(o => o.OrgId == scope.OrgId && o.AnonymizedAt == null)
                .Select(o => o.Id)
                .ToListAsync(ct);
            var inReach = await OwnersInReachAsync(scope, all, ct);
            candidates = all.Where(inReach.Contains).ToList();
        }

        var periods = await db.PropertyOwnerships.AsNoTracking()
            .Where(po => po.OrgId == scope.OrgId && candidates.Contains(po.PropertyOwnerId))
            .Select(po => new { po.Id, po.PropertyOwnerId, po.OwnershipEnd })
            .ToListAsync(ct);
        var mandates = await db.OwnerMandates.AsNoTracking()
            .Where(m => m.OrgId == scope.OrgId)
            .Select(m => new { m.PropertyOwnerId, m.PropertyOwnershipId, m.ValidTo })
            .ToListAsync(ct);

        var created = new List<Guid>();
        var skipped = new List<Guid>();
        foreach (var ownerId in candidates.OrderBy(id => id))
        {
            // An ownership not ended (in force or to come): the owner is managed by the agency. A period that ended is history.
            var own = periods.Where(p => p.PropertyOwnerId == ownerId).ToList();
            var managed = own.Any(p => p.OwnershipEnd is not { } end || RomeCalendar.DateInRome(end) >= today);

            // A mandate of the owner (or of one of its ownerships) still in force on the first day of the template or later: the owner has one, and a new
            // version would overlap it.
            var ownershipIds = own.Select(p => p.Id).ToHashSet();
            var hasMandate = mandates.Any(m => (m.PropertyOwnershipId is null ? m.PropertyOwnerId == ownerId : ownershipIds.Contains(m.PropertyOwnershipId.Value))
                && (m.ValidTo is not { } last || last >= terms.ValidFrom));
            if (!managed || hasMandate)
            {
                skipped.Add(ownerId);
                continue;
            }

            db.OwnerMandates.Add(NewMandate(scope.OrgId, ownerId, terms, userId));
            created.Add(ownerId);
        }

        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);

        logger.LogInformation(
            "Standard mandate from {ValidFrom} applied in org {OrgId} by {UserId}: {Created} created, {Skipped} skipped",
            terms.ValidFrom, scope.OrgId, userId, created.Count, skipped.Count);
        return new OwnerMandateTemplateResult(created.Count, skipped.Count, created, skipped);
    }

    public async Task<PropertyOwner> GetAddressAsync(HostScope scope, Guid ownerId, CancellationToken ct = default)
    {
        await RequireOwnerInReachAsync(scope, ownerId, ct);
        return await db.PropertyOwners.AsNoTracking().FirstAsync(o => o.Id == ownerId && o.OrgId == scope.OrgId, ct);
    }

    public async Task<PropertyOwner> SaveAddressAsync(HostScope scope, Guid ownerId, OwnerAddressCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var street = AddressText(command.Street, PropertyOwner.BillingStreetMaxLength, "street", required: true);
        var building = AddressText(command.BuildingNumber, PropertyOwner.BillingCivicMaxLength, "buildingNumber", required: false);
        var postalCode = AddressText(command.PostalCode, PropertyOwner.BillingPostalCodeMaxLength, "postalCode", required: true);
        var town = AddressText(command.Town, PropertyOwner.BillingCityMaxLength, "town", required: true);
        var province = AddressText(command.Province, PropertyOwner.BillingProvinceMaxLength, "province", required: false);
        var country = command.Country?.Trim() ?? string.Empty;
        if (country.Length != 2 || !country.All(char.IsAsciiLetter))
            throw AddressInvalid("country");

        country = country.ToUpperInvariant();

        // Italy needs the province (the sigla) on the documents: the same rule the readiness of the owner applies.
        if (province is null && string.Equals(country, OwnerFieldRules.Italy, StringComparison.Ordinal))
            throw AddressInvalid("province");

        await RequireOwnerInReachAsync(scope, ownerId, ct);
        var owner = await db.PropertyOwners.FirstAsync(o => o.Id == ownerId && o.OrgId == scope.OrgId, ct);
        owner.BillingStreet = street;
        owner.BillingCivic = building;
        owner.BillingPostalCode = postalCode;
        owner.BillingCity = town;
        owner.BillingProvince = province?.ToUpperInvariant();
        owner.BillingCountry = country;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Billing address of owner {OwnerId} in org {OrgId} saved by {UserId}", ownerId, scope.OrgId, userId);
        return owner;
    }

    public async Task<ChannelCommissionRuleSet> GetChannelRulesAsync(Guid orgId, CancellationToken ct = default)
    {
        var rules = await db.OrgChannelCommissionRules.AsNoTracking()
            .Where(r => r.OrgId == orgId)
            .OrderBy(r => r.Channel)
            .ThenBy(r => r.ValidFrom)
            .ToListAsync(ct);
        return SetOf(rules);
    }

    public async Task<ChannelCommissionRuleSet> SaveChannelRulesAsync(
        Guid orgId,
        IReadOnlyList<ChannelCommissionRuleCommand>? rules,
        string? userId,
        CancellationToken ct = default)
    {
        if (rules is null)
            throw ChannelRulesInvalid(null, "rules");

        var wanted = new List<(int Index, string Channel, decimal Percent, CommissionBase Base, DateOnly ValidFrom, DateOnly? ValidTo)>();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i] ?? throw ChannelRulesInvalid(i, "rule");
            if (!FiscalCopy.IsOtaBookingSource(rule.Channel))
                throw ChannelRulesInvalid(i, "channel");
            if (!IsPercent(rule.Percent))
                throw ChannelRulesInvalid(i, "percent");
            if (rule.Base is { } basis && !Enum.IsDefined(basis))
                throw ChannelRulesInvalid(i, "base");
            if (rule.ValidFrom == default)
                throw ChannelRulesInvalid(i, "validFrom");
            if (rule.ValidTo is { } last && last < rule.ValidFrom)
                throw ChannelRulesInvalid(i, "validTo");

            wanted.Add((i, rule.Channel.ToString(), rule.Percent, rule.Base ?? CommissionBase.GrossExclTouristTax, rule.ValidFrom, rule.ValidTo));
        }

        // The rules of one channel never overlap: on a check-out day one rule at most is in force.
        foreach (var channel in wanted.GroupBy(w => w.Channel))
        {
            var ordered = channel.OrderBy(w => w.ValidFrom).ThenBy(w => w.Index).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i - 1].ValidTo is not { } previousEnd || previousEnd >= ordered[i].ValidFrom)
                    throw ChannelRulesInvalid(Math.Max(ordered[i - 1].Index, ordered[i].Index), "validFrom");
            }
        }

        await using var tx = await LockAsync(orgId, ct);
        var stored = await db.OrgChannelCommissionRules.Where(r => r.OrgId == orgId).ToListAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var kept = new HashSet<Guid>();
        var added = 0;
        foreach (var w in wanted)
        {
            // A rule identical to a stored one keeps its row and its id: the lines already posted name it (channel-rule:{id}).
            var same = stored.FirstOrDefault(r => !kept.Contains(r.Id) && r.Channel == w.Channel && r.ValidFrom == w.ValidFrom && r.ValidTo == w.ValidTo
                && r.Percent == w.Percent && r.Base == w.Base);
            if (same is not null)
            {
                kept.Add(same.Id);
                continue;
            }

            db.OrgChannelCommissionRules.Add(new OrgChannelCommissionRule
            {
                OrgId = orgId,
                Channel = w.Channel,
                Percent = w.Percent,
                Base = w.Base,
                ValidFrom = w.ValidFrom,
                ValidTo = w.ValidTo,
                CreatedAt = now,
                CreatedByUserId = userId,
            });
            added++;
        }

        var removed = stored.Where(r => !kept.Contains(r.Id)).ToList();
        db.OrgChannelCommissionRules.RemoveRange(removed);
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);

        logger.LogInformation(
            "Channel commission rules of org {OrgId} saved by {UserId}: {Kept} kept, {Added} added, {Removed} removed",
            orgId, userId, kept.Count, added, removed.Count);
        return await GetChannelRulesAsync(orgId, ct);
    }

    /// <summary>The rules and, for each portal, what applies today: the rule in force, else the configured default (marked), else nothing.</summary>
    private ChannelCommissionRuleSet SetOf(IReadOnlyList<OrgChannelCommissionRule> rules)
    {
        var today = time.TodayInRomeAsDateOnly();
        var channels = new List<ChannelCommissionInForce>();
        foreach (var channel in ChannelCommissionRules.Portals)
        {
            if (ChannelCommissionRules.InForce(rules, channel, today) is { } rule)
            {
                channels.Add(new ChannelCommissionInForce(channel, rule.Percent, rule.Base, false, rule.Id, rule.ValidFrom, rule.ValidTo));
            }
            else if (options.Value.TryGetChannelDefault(channel, out var percent))
            {
                channels.Add(new ChannelCommissionInForce(channel, percent, CommissionBase.GrossExclTouristTax, true, null, null, null));
            }
            else
            {
                channels.Add(new ChannelCommissionInForce(channel, null, null, false, null, null, null));
            }
        }

        return new ChannelCommissionRuleSet(rules, channels);
    }

    /// <summary>
    /// The owners among <paramref name="ownerIds"/> that are owners of the org of the scope and in its reach: all of them for an org-wide scope; for a partial
    /// one only those that hold at least one ownership period and every one of them on a property the scope reaches (no declaration is never in reach).
    /// </summary>
    private async Task<HashSet<Guid>> OwnersInReachAsync(HostScope scope, IReadOnlyCollection<Guid> ownerIds, CancellationToken ct)
    {
        if (ownerIds.Count == 0)
            return [];

        var ids = ownerIds.ToList();
        var owners = await db.PropertyOwners.AsNoTracking()
            .Where(o => o.OrgId == scope.OrgId && ids.Contains(o.Id))
            .Select(o => o.Id)
            .ToListAsync(ct);
        if (scope.IsOrgWide)
            return [.. owners];

        var reachable = db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).Select(p => p.Id);
        var periods = await db.PropertyOwnerships.AsNoTracking()
            .Where(po => po.OrgId == scope.OrgId && ids.Contains(po.PropertyOwnerId))
            .Select(po => new { po.PropertyOwnerId, Inside = reachable.Contains(po.PropertyId) })
            .ToListAsync(ct);
        var byOwner = periods.ToLookup(p => p.PropertyOwnerId);
        return [.. owners.Where(id => byOwner[id].Any() && byOwner[id].All(p => p.Inside))];
    }

    private async Task RequireOwnerInReachAsync(HostScope scope, Guid ownerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!(await OwnersInReachAsync(scope, [ownerId], ct)).Contains(ownerId))
            throw OwnerNotFound(ownerId);
    }

    private OwnerMandate NewMandate(Guid orgId, Guid ownerId, MandateTerms terms, string? userId) => new()
    {
        OrgId = orgId,
        PropertyOwnerId = ownerId,
        PropertyOwnershipId = null,
        ValidFrom = terms.ValidFrom,
        ValidTo = null,
        CommissionPercent = terms.CommissionPercent,
        CommissionBase = terms.CommissionBase,
        CommissionIncludesVat = terms.CommissionIncludesVat,
        CleaningFlatRate = terms.CleaningFlatRate,
        LinenFlatRate = terms.LinenFlatRate,
        PassthroughPaymentFees = terms.PassthroughPaymentFees,
        WithholdingTreatment = terms.WithholdingTreatment,
        MandatePdfUrl = terms.Document ?? string.Empty,
        CreatedAt = time.GetUtcNow().UtcDateTime,
        CreatedByUserId = userId,
    };

    /// <summary>The terms of a version, checked: 422 <c>owner_mandate_invalid</c> with the first field that is wrong.</summary>
    private static MandateTerms Validated(OwnerMandateCommand? command)
    {
        if (command is null)
            throw MandateInvalid("template");
        if (command.ValidFrom == default)
            throw MandateInvalid("validFrom");
        if (!IsPercent(command.CommissionPercent))
            throw MandateInvalid("commissionPercent");
        if (command.CommissionBase is { } basis && !Enum.IsDefined(basis))
            throw MandateInvalid("commissionBase");
        if (!IsFlatRate(command.CleaningFlatRate))
            throw MandateInvalid("cleaningFlatRate");
        if (!IsFlatRate(command.LinenFlatRate))
            throw MandateInvalid("linenFlatRate");
        if (command.WithholdingTreatment is { } treatment && !Enum.IsDefined(treatment))
            throw MandateInvalid("withholdingTreatment");

        var document = string.IsNullOrWhiteSpace(command.DocumentReference) ? null : command.DocumentReference.Trim();
        if (document is not null && !IsDocumentReference(document))
            throw MandateInvalid("documentReference");

        return new MandateTerms(
            command.ValidFrom,
            command.CommissionPercent,
            command.CommissionBase ?? CommissionBase.GrossExclTouristTax,
            command.CommissionIncludesVat,
            command.CleaningFlatRate,
            command.LinenFlatRate,
            command.PassthroughPaymentFees,
            command.WithholdingTreatment ?? CommissionTreatment.Deductible,
            document);
    }

    /// <summary>0 to 100 with at most two decimals.</summary>
    private static bool IsPercent(decimal value) => value is >= 0m and <= 100m && decimal.Round(value, 2) == value;

    private static bool IsFlatRate(decimal? value) => value is not { } amount || (amount is >= 0m and <= MaxFlatRate && decimal.Round(amount, 2) == amount);

    /// <summary>An https address, or the key of a file of the private storage (letters, digits, <c>/ _ . -</c>, never <c>..</c>).</summary>
    private static bool IsDocumentReference(string value)
    {
        if (value.Length > OwnerMandate.DocumentMaxLength)
            return false;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return uri.Scheme == Uri.UriSchemeHttps;

        return StorageKeyShape().IsMatch(value) && !value.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>The trimmed text of a part of the address; <c>null</c> when it is blank and optional. 422 <c>owner_address_invalid</c> otherwise.</summary>
    private static string? AddressText(string? value, int max, string field, bool required)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text is null)
        {
            if (required)
                throw AddressInvalid(field);

            return null;
        }

        if (text.Length > max || text.Any(char.IsControl))
            throw AddressInvalid(field);

        return text;
    }

    private Task<IDbContextTransaction?> LockAsync(Guid orgId, CancellationToken ct) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.OwnerAccounting, orgId.ToString("N")));

    private static NotFoundException OwnerNotFound(Guid ownerId) =>
        new($"Owner {ownerId} was not found") { Code = OwnerFiscalErrors.OwnerNotFound, MessageKey = "OwnerNotFound" };

    private static DomainRuleException MandateInvalid(string field) =>
        new(OwnerMandateErrors.MandateInvalid, "OwnerMandateInvalid") { Extensions = new Dictionary<string, object?> { ["field"] = field } };

    private static DomainRuleException AddressInvalid(string field) =>
        new(OwnerMandateErrors.AddressInvalid, "OwnerAddressInvalid") { Extensions = new Dictionary<string, object?> { ["field"] = field } };

    private static DomainRuleException ChannelRulesInvalid(int? index, string field) =>
        new(OwnerMandateErrors.ChannelRulesInvalid, "ChannelCommissionRulesInvalid")
        {
            Extensions = new Dictionary<string, object?> { ["index"] = index, ["field"] = field },
        };

    private sealed record MandateTerms(
        DateOnly ValidFrom,
        decimal CommissionPercent,
        CommissionBase CommissionBase,
        bool CommissionIncludesVat,
        decimal? CleaningFlatRate,
        decimal? LinenFlatRate,
        bool PassthroughPaymentFees,
        CommissionTreatment WithholdingTreatment,
        string? Document);
}
