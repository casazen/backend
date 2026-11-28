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

public sealed partial class OwnerMandateService(
    AppDbContext db,
    TimeProvider time,
    IOptions<OwnerAccountingOptions> options,
    ILogger<OwnerMandateService> logger) : IOwnerMandateService
{
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
            var own = periods.Where(p => p.PropertyOwnerId == ownerId).ToList();
            var managed = own.Any(p => p.OwnershipEnd is not { } end || RomeCalendar.DateInRome(end) >= today);

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

        var italian = string.Equals(country, OwnerFieldRules.Italy, StringComparison.Ordinal);
        if (province is null && italian)
            throw AddressInvalid("province");

        await RequireOwnerInReachAsync(scope, ownerId, ct);
        var owner = await db.PropertyOwners.FirstAsync(o => o.Id == ownerId && o.OrgId == scope.OrgId, ct);
        owner.BillingStreet = street;
        owner.BillingCivic = building;
        owner.BillingPostalCode = postalCode;
        owner.BillingCity = town;
        owner.BillingProvince = italian ? province?.ToUpperInvariant() : province;
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
            if (rule.Percent is not { } percent || !IsPercent(percent))
                throw ChannelRulesInvalid(i, "percent");
            if (rule.Base is { } basis && !Enum.IsDefined(basis))
                throw ChannelRulesInvalid(i, "base");
            if (rule.ValidFrom == default)
                throw ChannelRulesInvalid(i, "validFrom");
            if (rule.ValidTo is { } last && last < rule.ValidFrom)
                throw ChannelRulesInvalid(i, "validTo");

            wanted.Add((i, rule.Channel.ToString(), percent, rule.Base ?? CommissionBase.GrossExclTouristTax, rule.ValidFrom, rule.ValidTo));
        }

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

        var matches = new List<(OrgChannelCommissionRule Stored, decimal Percent, CommissionBase Base, DateOnly? ValidTo)>();
        var created = new List<(string Channel, decimal Percent, CommissionBase Base, DateOnly ValidFrom, DateOnly? ValidTo)>();
        foreach (var w in wanted)
        {
            var current = stored.FirstOrDefault(r => r.Channel == w.Channel && r.ValidFrom == w.ValidFrom);
            if (current is null)
                created.Add((w.Channel, w.Percent, w.Base, w.ValidFrom, w.ValidTo));
            else
                matches.Add((current, w.Percent, w.Base, w.ValidTo));
        }

        var removed = stored.Where(r => matches.All(m => m.Stored.Id != r.Id)).ToList();
        var changed = matches.Where(m => m.Stored.Percent != m.Percent || m.Stored.Base != m.Base).Select(m => m.Stored).ToList();

        var named = await RulesNamedByPostedLinesAsync(orgId, removed.Concat(changed).ToList(), ct);
        if (named.Count > 0)
        {
            throw new DomainConflictException(OwnerMandateErrors.ChannelRuleInUse, "ChannelCommissionRuleInUse")
            {
                Extensions = new Dictionary<string, object?> { ["ruleId"] = named[0] },
            };
        }

        foreach (var (current, percent, basis, validTo) in matches)
        {
            current.Percent = percent;
            current.Base = basis;
            current.ValidTo = validTo;
        }

        foreach (var (channel, percent, basis, validFrom, validTo) in created)
        {
            db.OrgChannelCommissionRules.Add(new OrgChannelCommissionRule
            {
                OrgId = orgId,
                Channel = channel,
                Percent = percent,
                Base = basis,
                ValidFrom = validFrom,
                ValidTo = validTo,
                CreatedAt = now,
                CreatedByUserId = userId,
            });
        }

        db.OrgChannelCommissionRules.RemoveRange(removed);
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);

        logger.LogInformation(
            "Channel commission rules of org {OrgId} saved by {UserId}: {Kept} kept, {Added} added, {Removed} removed",
            orgId, userId, matches.Count, created.Count, removed.Count);
        return await GetChannelRulesAsync(orgId, ct);
    }

    private async Task<List<Guid>> RulesNamedByPostedLinesAsync(Guid orgId, IReadOnlyCollection<OrgChannelCommissionRule> rules, CancellationToken ct)
    {
        if (rules.Count == 0)
            return [];

        var names = rules.Select(r => ChannelFeeSources.Rule(r.Id)).ToList();
        var used = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.RuleVersion != null && names.Contains(e.RuleVersion))
            .Select(e => e.RuleVersion!)
            .Distinct()
            .ToListAsync(ct);
        return rules.Where(r => used.Contains(ChannelFeeSources.Rule(r.Id))).Select(r => r.Id).ToList();
    }

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
        CleaningRechargeMode = terms.CleaningRechargeMode,
        LinenFlatRate = terms.LinenFlatRate,
        PassthroughPaymentFees = terms.PassthroughPaymentFees,
        WithholdingTreatment = terms.WithholdingTreatment,
        CoOwnerTaxReporting = terms.CoOwnerTaxReporting,
        MandatePdfUrl = terms.Document ?? string.Empty,
        CreatedAt = time.GetUtcNow().UtcDateTime,
        CreatedByUserId = userId,
    };

    private static MandateTerms Validated(OwnerMandateCommand? command)
    {
        if (command is null)
            throw MandateInvalid("template");
        if (command.ValidFrom == default)
            throw MandateInvalid("validFrom");
        if (command.CommissionPercent is not { } percent || !IsPercent(percent))
            throw MandateInvalid("commissionPercent");
        if (command.CommissionBase is { } basis && !Enum.IsDefined(basis))
            throw MandateInvalid("commissionBase");
        if (!IsFlatRate(command.CleaningFlatRate))
            throw MandateInvalid("cleaningFlatRate");
        if (command.CleaningRechargeMode is { } recharge && !Enum.IsDefined(recharge))
            throw MandateInvalid("cleaningRechargeMode");
        if (!IsFlatRate(command.LinenFlatRate))
            throw MandateInvalid("linenFlatRate");
        if (command.WithholdingTreatment is { } treatment && !Enum.IsDefined(treatment))
            throw MandateInvalid("withholdingTreatment");
        if (command.CoOwnerTaxReporting is { } reporting && !Enum.IsDefined(reporting))
            throw MandateInvalid("coOwnerTaxReporting");

        var document = string.IsNullOrWhiteSpace(command.DocumentReference) ? null : command.DocumentReference.Trim();
        if (document is not null && !IsDocumentReference(document))
            throw MandateInvalid("documentReference");

        return new MandateTerms(
            command.ValidFrom,
            percent,
            command.CommissionBase ?? CommissionBase.GrossExclTouristTax,
            command.CommissionIncludesVat,
            command.CleaningFlatRate,
            command.CleaningRechargeMode ?? CleaningRechargeMode.FlatRate,
            command.LinenFlatRate,
            command.PassthroughPaymentFees,
            command.WithholdingTreatment ?? CommissionTreatment.Deductible,
            command.CoOwnerTaxReporting ?? CoOwnerTaxReporting.Signatory,
            document);
    }

    private static bool IsPercent(decimal value) => value is >= 0m and <= 100m && decimal.Round(value, 2) == value;

    private static bool IsFlatRate(decimal? value) => value is not { } amount || (amount is >= 0m and <= MaxFlatRate && decimal.Round(amount, 2) == amount);

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
        CleaningRechargeMode CleaningRechargeMode,
        decimal? LinenFlatRate,
        bool PassthroughPaymentFees,
        CommissionTreatment WithholdingTreatment,
        CoOwnerTaxReporting CoOwnerTaxReporting,
        string? Document);
}
