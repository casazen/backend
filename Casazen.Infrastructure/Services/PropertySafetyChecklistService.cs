using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Safety checklist of a short-stay property under D.L. 145/2023 art. 13-ter (CO-07, A5-21). Rules in
/// <see cref="SafetyChecklistRules"/>. A checklist row always holds one row per item of
/// <see cref="SafetyChecklistRules.Items"/>: it is created with all of them, so concurrent first saves collide on the
/// unique property index (23505), and the loser re-reads and updates. Every save re-evaluates the compliance status of the
/// property (CO-06): an active property whose checklist is no longer complete and confirmed is suspended.
/// </summary>
public class PropertySafetyChecklistService(
    AppDbContext db,
    IPropertyComplianceStatusService complianceStatus,
    ILogger<PropertySafetyChecklistService> logger,
    TimeProvider? timeProvider = null) : IPropertySafetyChecklistService
{
    private const int MaxTextLength = 200;
    private const int MaxNotesLength = 500;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<SafetyChecklistView> GetAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        await GetOrgIdAsync(propertyId, cancellationToken);

        var checklist = await db.PropertySafetyChecklists
            .AsNoTracking()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.PropertyId == propertyId, cancellationToken);

        return await ToViewAsync(checklist, cancellationToken);
    }

    public async Task<SafetyChecklistView> SaveAsync(
        Guid propertyId,
        string userId,
        SafetyChecklistInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var orgId = await GetOrgIdAsync(propertyId, cancellationToken);

        Validate(input, _clock.TodayInRomeAsDateOnly());
        await EnsureEvidenceBelongsToPropertyAsync(propertyId, input.Items, cancellationToken);

        await EnsureChecklistRowAsync(propertyId, orgId, cancellationToken);
        var checklist = await db.PropertySafetyChecklists
            .Include(c => c.Items)
            .SingleAsync(c => c.PropertyId == propertyId, cancellationToken);

        var now = _clock.GetUtcNow().UtcDateTime;
        Apply(checklist, input, userId, now);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // An item row added by a concurrent save (only for rows imported before an item existed).
            throw new DomainConflictException("safety_checklist_concurrent_update", "SafetyChecklistConcurrentUpdate");
        }

        logger.LogInformation(
            "Safety checklist of property {PropertyId} saved by {UserId} (confirmed: {Confirmed})",
            propertyId, userId, input.Confirm);

        await complianceStatus.ReevaluateAsync(propertyId, cancellationToken);
        return await ToViewAsync(checklist, cancellationToken);
    }

    private async Task<Guid> GetOrgIdAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        // Tenant filter: another org's property is not found.
        var orgId = await db.Properties
            .Where(p => p.Id == propertyId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(cancellationToken);
        return orgId ?? throw new NotFoundException($"Property {propertyId} not found");
    }

    private async Task EnsureChecklistRowAsync(Guid propertyId, Guid orgId, CancellationToken cancellationToken)
    {
        if (await db.PropertySafetyChecklists.AnyAsync(c => c.PropertyId == propertyId, cancellationToken))
            return;

        var now = _clock.GetUtcNow().UtcDateTime;
        var checklist = new PropertySafetyChecklist
        {
            OrgId = orgId,
            PropertyId = propertyId,
            SchemaVersion = SafetyChecklistRules.SchemaVersion,
            LegalBasis = SafetyChecklistRules.LegalBasis,
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var code in SafetyChecklistRules.Items)
            checklist.Items.Add(new PropertySafetyChecklistItem { OrgId = orgId, Code = code, UpdatedAt = now });

        db.PropertySafetyChecklists.Add(checklist);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Created by a concurrent save: the caller re-reads it.
        }
        finally
        {
            foreach (var item in checklist.Items)
                db.Entry(item).State = EntityState.Detached;
            db.Entry(checklist).State = EntityState.Detached;
        }
    }

    private static void Apply(PropertySafetyChecklist checklist, SafetyChecklistInput input, string userId, DateTime now)
    {
        var facts = input.Facts;
        checklist.SchemaVersion = SafetyChecklistRules.SchemaVersion;
        checklist.LegalBasis = SafetyChecklistRules.LegalBasis;
        checklist.Entrepreneurial = facts.Entrepreneurial;
        checklist.HasGasSupply = facts.HasGasSupply;
        checklist.CombustionAppliances = facts.CombustionAppliances?.Distinct().Order().ToList();
        checklist.FloorCount = facts.FloorCount;
        checklist.FloorAreasSqm = facts.FloorAreasSqm?.ToList();

        foreach (var code in SafetyChecklistRules.Items)
        {
            var incoming = input.Items.FirstOrDefault(i => i.Code == code);
            var item = checklist.Items.FirstOrDefault(i => i.Code == code);
            if (item is null)
            {
                item = new PropertySafetyChecklistItem { OrgId = checklist.OrgId, ChecklistId = checklist.Id, Code = code };
                checklist.Items.Add(item);
            }

            var changed = ApplyItem(item, incoming);
            if (changed)
            {
                item.UpdatedAt = now;
                item.UpdatedBy = userId;
            }
        }

        // SC-08: the confirmation covers the answers of this save only.
        checklist.ConfirmedAt = input.Confirm ? now : null;
        checklist.ConfirmedBy = input.Confirm ? userId : null;
        checklist.ConfirmedTextVersion = input.Confirm ? SafetyChecklistRules.DeclarationTextVersion : null;
        checklist.UpdatedAt = now;
        checklist.UpdatedBy = userId;
    }

    /// <summary>Copies the answer of one item, keeping only the details that item collects. True when anything changed.</summary>
    private static bool ApplyItem(PropertySafetyChecklistItem item, SafetyChecklistItemInput? incoming)
    {
        var code = item.Code;
        var withDevices = code == SafetyItemCode.FireExtinguishers || SafetyChecklistRules.IsDetector(code);
        var detector = SafetyChecklistRules.IsDetector(code);

        // An imported answer stays "to review" until the host answers the item.
        var answer = incoming?.Answer ?? (item.Answer == SafetyItemAnswer.ToReview ? SafetyItemAnswer.ToReview : null);
        var quantity = withDevices ? incoming?.Quantity : null;
        var location = withDevices ? Clean(incoming?.Location) : null;
        var detectorType = detector ? incoming?.DetectorType : null;
        var checkedOn = incoming?.CheckedOn;
        var expiresOn = detector ? incoming?.ExpiresOn : null;
        var evidence = incoming?.EvidenceDocumentId;
        var notes = Clean(incoming?.Notes);

        var changed = item.Answer != answer
            || item.Quantity != quantity
            || item.Location != location
            || item.DetectorType != detectorType
            || item.CheckedOn != checkedOn
            || item.ExpiresOn != expiresOn
            || item.EvidenceDocumentId != evidence
            || item.Notes != notes;

        item.Answer = answer;
        item.Quantity = quantity;
        item.Location = location;
        item.DetectorType = detectorType;
        item.CheckedOn = checkedOn;
        item.ExpiresOn = expiresOn;
        item.EvidenceDocumentId = evidence;
        item.Notes = notes;
        return changed;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static void Validate(SafetyChecklistInput input, DateOnly today)
    {
        var facts = input.Facts ?? throw Invalid();
        if (input.Items is null)
            throw Invalid();

        if (facts.FloorCount is { } floors && (floors < 1 || floors > SafetyChecklistRules.MaxFloors))
            throw FloorsInvalid();

        if (facts.FloorAreasSqm is { } areas
            && (facts.FloorCount is null
                || areas.Count != facts.FloorCount
                || areas.Any(a => a <= 0 || a > SafetyChecklistRules.MaxFloorAreaSqm)))
        {
            throw FloorsInvalid();
        }

        if (facts.CombustionAppliances?.Any(a => !Enum.IsDefined(a)) == true)
            throw Invalid();

        if (input.Items.GroupBy(i => i.Code).Any(g => g.Count() > 1))
            throw Invalid();

        foreach (var item in input.Items)
        {
            if (!Enum.IsDefined(item.Code)
                || item.Answer is SafetyItemAnswer.ToReview
                || (item.Answer is { } answer && !Enum.IsDefined(answer))
                || (item.DetectorType is { } type && !Enum.IsDefined(type))
                || item.Location?.Trim().Length > MaxTextLength
                || item.Notes?.Trim().Length > MaxNotesLength)
            {
                throw Invalid();
            }

            if (item.Quantity is { } quantity && (quantity < 1 || quantity > SafetyChecklistRules.MaxQuantity))
                throw new DomainRuleException("safety_quantity_invalid", "SafetyQuantityInvalid", SafetyChecklistRules.MaxQuantity);

            if (item.Code == SafetyItemCode.FireExtinguishers && item.Answer == SafetyItemAnswer.Present && item.Quantity is null)
                throw new DomainRuleException("safety_extinguisher_quantity_required", "SafetyExtinguisherQuantityRequired");

            if (item.CheckedOn > today)
                throw new DomainRuleException("safety_date_in_future", "SafetyDateInFuture");
        }

        static DomainRuleException Invalid() => new("safety_checklist_invalid", "SafetyChecklistInvalid");

        static DomainRuleException FloorsInvalid() => new(
            "safety_floors_invalid",
            "SafetyFloorsInvalid",
            SafetyChecklistRules.MaxFloors,
            SafetyChecklistRules.MaxFloorAreaSqm);
    }

    private async Task EnsureEvidenceBelongsToPropertyAsync(
        Guid propertyId,
        IReadOnlyList<SafetyChecklistItemInput> items,
        CancellationToken cancellationToken)
    {
        var ids = items.Where(i => i.EvidenceDocumentId.HasValue).Select(i => i.EvidenceDocumentId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return;

        var found = await db.PropertyDocuments
            .Where(d => ids.Contains(d.Id) && d.PropertyId == propertyId)
            .CountAsync(cancellationToken);
        if (found != ids.Count)
            throw new DomainRuleException("safety_evidence_not_found", "SafetyEvidenceNotFound");
    }

    private async Task<SafetyChecklistView> ToViewAsync(PropertySafetyChecklist? checklist, CancellationToken cancellationToken)
    {
        var evaluation = SafetyChecklistRules.Evaluate(checklist);
        var ids = checklist?.Items
            .Where(i => i.EvidenceDocumentId.HasValue)
            .Select(i => i.EvidenceDocumentId!.Value)
            .Distinct()
            .ToList() ?? [];

        var names = ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.PropertyDocuments
                .AsNoTracking()
                .Where(d => ids.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.FileName, cancellationToken);

        return new SafetyChecklistView(checklist, evaluation, names);
    }
}
