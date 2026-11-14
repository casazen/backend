using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IBillingSetupService"/>
/// <remarks>
/// The <c>channels</c> section lives on the profile of the issuer of the organization (<see cref="OrgFiscalIssuerProfile.PortalAccountHolder"/>,
/// <see cref="OrgFiscalIssuerProfile.RechargePortalCommissions"/>), the holders of the listings in <see cref="PropertyChannelAccountHolder"/>. The logs carry ids
/// and counts only.
/// </remarks>
public sealed class BillingSetupService(AppDbContext db, TimeProvider time, ILogger<BillingSetupService> logger) : IBillingSetupService
{
    public async Task<BillingChannelSettings> GetChannelsAsync(Guid orgId, CancellationToken ct = default)
    {
        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.OrgId == orgId, ct);
        return profile is null
            ? new BillingChannelSettings(PortalAccountHolders.Defaults.Default, PortalAccountHolders.Defaults.RechargePortalCommissions)
            : new BillingChannelSettings(profile.PortalAccountHolder, profile.RechargePortalCommissions);
    }

    public async Task<BillingChannelSettings> SaveChannelsAsync(Guid orgId, BillingChannelsCommand command, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.PortalAccountHolder is { } holder && !Enum.IsDefined(holder))
            throw new DomainRuleException(BillingSetupErrors.ChannelsInvalid, "BillingChannelsInvalid") { Extensions = new Dictionary<string, object?> { ["field"] = "portalAccountHolder" } };

        var profile = await db.OrgFiscalIssuerProfiles.FirstOrDefaultAsync(p => p.OrgId == orgId, ct);
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
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Channel settings of org {OrgId} saved by {UserId}: holder {Holder}, recharge {Recharge}",
            orgId, userId, profile.PortalAccountHolder, profile.RechargePortalCommissions);
        return new BillingChannelSettings(profile.PortalAccountHolder, profile.RechargePortalCommissions);
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
        CancellationToken ct = default)
    {
        // An empty list is never «all the listings», and never «none» either: it is refused.
        if (commands is null || commands.Count == 0)
            throw Invalid(null, "items");
        if (commands.Count > BillingSetupErrors.MaxAccountHolders)
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

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, PropertyChannelAccountHolder.PropertyChannelIndexName))
        {
            // Another save of the same listing and portal came first: nothing of this one is written, the caller reads the list again.
            throw new DomainConflictException(BillingSetupErrors.AccountHoldersConflict, "ChannelAccountHoldersConflict");
        }

        logger.LogInformation(
            "Channel account holders of org {OrgId} saved by {UserId}: {Written} written, {Removed} back to the default",
            orgId, userId, written, removed);
        return await ListAccountHoldersAsync(orgId, ct);
    }

    private static DomainRuleException Invalid(int? index, string field) =>
        new(BillingSetupErrors.AccountHoldersInvalid, "ChannelAccountHoldersInvalid")
        {
            Extensions = new Dictionary<string, object?> { ["index"] = index, ["field"] = field },
        };
}
