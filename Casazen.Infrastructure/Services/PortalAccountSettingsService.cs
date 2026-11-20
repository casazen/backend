using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPortalAccountSettingsService"/>
/// <remarks>
/// The defaults live on the profile of the issuer of the organization (<see cref="OrgFiscalIssuerProfile.PortalAccountHolder"/>,
/// <see cref="OrgFiscalIssuerProfile.RechargePortalCommissions"/>), the holders of the listings in <see cref="PropertyChannelAccountHolder"/>. The logs carry ids
/// and counts only. The waiver of the recharge (FIX/BE-V2-NO-RECHARGE), whether a save of the defaults or a bulk write of the holders starts it, is acknowledged in the activity
/// log of the organization (<paramref name="activityLog"/>), in the save of the change: a line <see cref="OrgActivityType.OrgSettingsChanged"/> with the part
/// <see cref="PortalAccountErrors.RechargeWaiverActivityPart"/>, whose actor and instant are who acknowledged it and when (the place the other settings of an organization
/// record their changes; no column of its own).
/// </remarks>
public sealed class PortalAccountSettingsService(
    AppDbContext db,
    TimeProvider time,
    ILogger<PortalAccountSettingsService> logger,
    IActivityLog? activityLog = null) : IPortalAccountSettingsService
{
    public async Task<PortalAccountSettings> GetAsync(Guid orgId, CancellationToken ct = default)
    {
        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().OfOrganization(orgId).FirstOrDefaultAsync(ct);
        return profile is null
            ? new PortalAccountSettings(PortalAccountHolders.Defaults.Default, PortalAccountHolders.Defaults.RechargePortalCommissions)
            : new PortalAccountSettings(profile.PortalAccountHolder, profile.RechargePortalCommissions);
    }

    public async Task<PortalAccountSettings> SaveAsync(Guid orgId, PortalAccountSettingsCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.PortalAccountHolder is { } holder && !Enum.IsDefined(holder))
        {
            throw new DomainRuleException(PortalAccountErrors.ChannelsInvalid, "BillingChannelsInvalid")
            {
                Extensions = new Dictionary<string, object?> { ["field"] = PortalAccountFields.PortalAccountHolder },
            };
        }

        var profile = await db.OrgFiscalIssuerProfiles.OfOrganization(orgId).FirstOrDefaultAsync(ct);

        // The waiver of the recharge (decision 1 of 20/11/2026): with the recharge off the commissions of the accounts the agency holds are its own cost and the owners are not
        // charged them. The law gives the agency their reimbursement (art. 1720 c.c., art. 402 CO), so a save that starts the waiver (after it: the recharge off and an account of
        // the agency, its default or the one of a listing; before it: not so) needs the explicit acknowledgement, checked before anything is written.
        var startsWaiver = await StartsRechargeWaiverAsync(orgId, profile, command, ct);
        if (startsWaiver && command.AcknowledgeRechargeWaiver != true)
            throw new DomainRuleException(PortalAccountErrors.RechargeWaiverNotAcknowledged, "RechargeWaiverNotAcknowledged");

        if (profile is null)
        {
            // An organization that never opened the wizard gets a draft profile: everything else stays empty until the wizard says it.
            profile = new OrgFiscalIssuerProfile { OrgId = orgId };
            db.OrgFiscalIssuerProfiles.Add(profile);
        }

        if (command.PortalAccountHolder is { } newHolder)
            profile.PortalAccountHolder = newHolder;
        if (command.RechargePortalCommissions is { } recharge)
            profile.RechargePortalCommissions = recharge;
        profile.UpdatedAt = time.GetUtcNow().UtcDateTime;
        profile.UpdatedByUserId = userId;

        // Who acknowledged the waiver and when: the actor and the instant of the line, written in the same save as the change (a change rolled back leaves no line).
        if (startsWaiver)
            RecordWaiver(orgId, userId);

        await db.SaveChangesAsync(ct);

        if (startsWaiver)
            logger.LogInformation("The recharge of the portal commissions of org {OrgId} was waived by {UserId}, acknowledged", orgId, userId);
        logger.LogInformation(
            "Portal account settings of org {OrgId} saved by {UserId}: holder {Holder}, recharge {Recharge}",
            orgId, userId, profile.PortalAccountHolder, profile.RechargePortalCommissions);
        return new PortalAccountSettings(profile.PortalAccountHolder, profile.RechargePortalCommissions);
    }

    /// <summary>
    /// True when the save starts the waiver of the recharge: after it the recharge is off and the agency holds at least one portal account (the default of the organization or the
    /// holder of a listing), and before it the recharge was on or the agency held none. A save that keeps a waiver already in place (or extends it to the default) starts nothing.
    /// </summary>
    private async Task<bool> StartsRechargeWaiverAsync(Guid orgId, OrgFiscalIssuerProfile? profile, PortalAccountSettingsCommand command, CancellationToken ct)
    {
        var holderBefore = profile?.PortalAccountHolder ?? PortalAccountHolders.Defaults.Default;
        var rechargeBefore = profile?.RechargePortalCommissions ?? PortalAccountHolders.Defaults.RechargePortalCommissions;
        var holderAfter = command.PortalAccountHolder ?? holderBefore;
        var rechargeAfter = command.RechargePortalCommissions ?? rechargeBefore;
        if (rechargeAfter)
            return false;

        var listingOfTheAgency = await db.PropertyChannelAccountHolders.AsNoTracking()
            .AnyAsync(h => h.OrgId == orgId && h.Holder == PortalAccountHolder.Agency, ct);
        return Waived(rechargeAfter, holderAfter, listingOfTheAgency) && !Waived(rechargeBefore, holderBefore, listingOfTheAgency);
    }

    /// <summary>
    /// True when the bulk write of the holders starts the waiver of the recharge: the recharge is off, the agency is not the default holder and holds no listing yet, and the
    /// write gives it one. The same effect as a save of the defaults that makes the agency the default holder, so the same acknowledgement.
    /// </summary>
    private async Task<bool> StartsRechargeWaiverAsync(Guid orgId, IReadOnlyList<ChannelAccountHolderCommand> commands, CancellationToken ct)
    {
        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().OfOrganization(orgId).FirstOrDefaultAsync(ct);
        var recharge = profile?.RechargePortalCommissions ?? PortalAccountHolders.Defaults.RechargePortalCommissions;
        var holder = profile?.PortalAccountHolder ?? PortalAccountHolders.Defaults.Default;
        if (recharge || holder == PortalAccountHolder.Agency || !commands.Any(c => c.Holder == PortalAccountHolder.Agency))
            return false;

        // Before the write, as stored; after it, a listing of the agency at least (the one the write names).
        var listingBefore = await db.PropertyChannelAccountHolders.AsNoTracking()
            .AnyAsync(h => h.OrgId == orgId && h.Holder == PortalAccountHolder.Agency, ct);
        return Waived(recharge, holder, listingOfTheAgency: true) && !Waived(recharge, holder, listingBefore);
    }

    /// <summary>The waiver of the recharge is in place: the recharge off while the agency holds at least one portal account, its default or the one of a listing.</summary>
    private static bool Waived(bool recharge, PortalAccountHolder defaultHolder, bool listingOfTheAgency) =>
        !recharge && (defaultHolder == PortalAccountHolder.Agency || listingOfTheAgency);

    /// <summary>
    /// Who acknowledged the waiver and when: a line <see cref="OrgActivityType.OrgSettingsChanged"/> with the part <see cref="PortalAccountErrors.RechargeWaiverActivityPart"/>,
    /// whose actor and instant are the person and the moment, added to the save of the change.
    /// </summary>
    private void RecordWaiver(Guid orgId, string? userId)
    {
        activityLog?.Record(OrgActivity.Of(
            orgId,
            OrgActivityType.OrgSettingsChanged,
            userId is { Length: > 0 and <= OrgActivityCatalog.MaxIdLength } ? userId : null,
            orgId.ToString(),
            (OrgActivityDetailKeys.Part, PortalAccountErrors.RechargeWaiverActivityPart)));
    }

    public async Task<IReadOnlyList<ChannelAccountHolderView>> ListAccountHoldersAsync(Guid orgId, CancellationToken ct = default)
    {
        var rows = await db.PropertyChannelAccountHolders.AsNoTracking()
            .Where(h => h.OrgId == orgId)
            .Join(
                db.Properties.AsNoTracking().Where(p => p.OrgId == orgId),
                h => h.PropertyId,
                p => p.Id,
                (h, p) => new { h.PropertyId, PropertyName = p.Name, h.Channel, h.Holder, h.UpdatedAt })
            .ToListAsync(ct);

        return rows
            .Select(r => Enum.TryParse<BookingSource>(r.Channel, ignoreCase: false, out var channel) && Enum.IsDefined(channel)
                ? new ChannelAccountHolderView(r.PropertyId, r.PropertyName, channel, r.Holder, r.UpdatedAt)
                : null)
            .OfType<ChannelAccountHolderView>()
            .OrderBy(v => v.PropertyName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(v => v.PropertyId)
            .ThenBy(v => v.Channel)
            .ToList();
    }

    public async Task<IReadOnlyList<ChannelAccountHolderView>> SaveAccountHoldersAsync(
        Guid orgId,
        IReadOnlyList<ChannelAccountHolderCommand>? commands,
        string? userId,
        bool acknowledgeRechargeWaiver = false,
        CancellationToken ct = default)
    {
        // An empty list is never «all the listings», and never «none» either: it is refused.
        if (commands is null || commands.Count == 0 || commands.Count > PortalAccountErrors.MaxAccountHolders)
            throw Invalid(null, "items");

        var seen = new HashSet<(Guid, BookingSource)>();
        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i] ?? throw Invalid(i, "item");
            if (command.PropertyId == Guid.Empty)
                throw Invalid(i, "propertyId");
            if (!Enum.IsDefined(command.Channel) || !ChannelCommissionRules.Portals.Contains(command.Channel))
                throw Invalid(i, "channel");
            if (command.Holder is { } holder && !Enum.IsDefined(holder))
                throw Invalid(i, "holder");
            if (!seen.Add((command.PropertyId, command.Channel)))
                throw Invalid(i, "channel");
        }

        // Every property named is one of the org (a property of another org, or one that does not exist, is refused with its index: nothing is written).
        var propertyIds = commands.Select(c => c.PropertyId).Distinct().ToList();
        var known = (await db.Properties.AsNoTracking()
                .Where(p => p.OrgId == orgId && propertyIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct))
            .ToHashSet();
        for (var i = 0; i < commands.Count; i++)
        {
            if (!known.Contains(commands[i].PropertyId))
                throw Invalid(i, "propertyId");
        }

        // The waiver of the recharge (decision 1 of 20/11/2026): with the recharge off, the first listing of the agency makes the commissions of its account a cost of the agency,
        // as making the agency the default holder does, so the write needs the same explicit acknowledgement, checked before anything is written.
        var startsWaiver = await StartsRechargeWaiverAsync(orgId, commands, ct);
        if (startsWaiver && !acknowledgeRechargeWaiver)
            throw new DomainRuleException(PortalAccountErrors.RechargeWaiverNotAcknowledged, "RechargeWaiverNotAcknowledged");

        var rows = await db.PropertyChannelAccountHolders
            .Where(h => h.OrgId == orgId && propertyIds.Contains(h.PropertyId))
            .ToListAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        int written = 0, removed = 0;
        foreach (var command in commands)
        {
            var name = command.Channel.ToString();
            var row = rows.FirstOrDefault(h => h.PropertyId == command.PropertyId && string.Equals(h.Channel, name, StringComparison.Ordinal));
            if (command.Holder is not { } holder)
            {
                if (row is not null)
                {
                    db.PropertyChannelAccountHolders.Remove(row);
                    removed++;
                }

                continue;
            }

            if (row is null)
            {
                db.PropertyChannelAccountHolders.Add(new PropertyChannelAccountHolder
                {
                    OrgId = orgId,
                    PropertyId = command.PropertyId,
                    Channel = name,
                    Holder = holder,
                    UpdatedAt = now,
                    UpdatedByUserId = userId,
                });
                written++;
            }
            else if (row.Holder != holder)
            {
                row.Holder = holder;
                row.UpdatedAt = now;
                row.UpdatedByUserId = userId;
                written++;
            }
        }

        // Who acknowledged the waiver and when, in the same save as the holders (a write refused or rolled back leaves no line).
        if (startsWaiver)
            RecordWaiver(orgId, userId);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, PropertyChannelAccountHolder.PropertyChannelIndexName))
        {
            // Another save of the same listing and portal came first: nothing of this one is written, the caller reads the list again.
            throw new DomainConflictException(PortalAccountErrors.AccountHoldersConflict, "ChannelAccountHoldersConflict");
        }

        if (startsWaiver)
            logger.LogInformation("The recharge of the portal commissions of org {OrgId} was waived by {UserId} with the holders of the listings, acknowledged", orgId, userId);
        logger.LogInformation(
            "Channel account holders of org {OrgId} saved by {UserId}: {Written} written, {Removed} back to the default",
            orgId, userId, written, removed);
        return await ListAccountHoldersAsync(orgId, ct);
    }

    private static DomainRuleException Invalid(int? index, string field) =>
        new(PortalAccountErrors.AccountHoldersInvalid, "ChannelAccountHoldersInvalid")
        {
            Extensions = new Dictionary<string, object?> { ["index"] = index, ["field"] = field },
        };
}
