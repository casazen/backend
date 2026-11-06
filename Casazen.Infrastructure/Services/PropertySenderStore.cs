using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPropertySenderStore" />
/// <remarks>
/// <para><b>Reads.</b> Every read goes through the <see cref="AppDbContext"/> of the request, so the tenant query filter hides the
/// properties, the senders and the mappings of the other orgs, and the soft-delete filter the deleted properties. The default reads the
/// confirmed mappings and their connection as <c>ChannelMappingService</c> does: the connection of this environment
/// (<see cref="OctorateEnvironments.Of"/>), connected, of the org of the property.</para>
/// <para><b>The one write.</b> <see cref="SetAsync"/> writes both senders with one value (ADR-029-R5) and stages the activity line in the
/// same unit of work (<see cref="IActivityLog.Record"/>), so the row and its line are saved together or not at all. The unique index on
/// the property makes a concurrent first write lose (23505), and the Alloggiati sender is a concurrency token, so a write that read
/// another sender than the stored one when it saves loses too: the loser drops what it staged and re-reads the row, and answers it when
/// it holds the sender asked, or refuses with <c>sender_changed_concurrently</c> (the person confirmed a switch from a sender that is not
/// the stored one any more).</para>
/// </remarks>
public sealed class PropertySenderStore(
    AppDbContext db,
    IActivityLog activityLog,
    IHostEnvironment hostEnvironment,
    TimeProvider? timeProvider = null) : IPropertySenderStore
{
    /// <summary>The error code of a write that lost to another one setting another sender.</summary>
    public const string ChangedConcurrentlyCode = "sender_changed_concurrently";

    /// <summary>The error code of a property outside Italy.</summary>
    public const string NotApplicableCode = "sender_not_applicable";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<PropertySenderInfo> GetAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await FindPropertyAsync(propertyId, ct) ?? throw PropertyNotFound(propertyId);
        var senders = await ReadAsync([property], ct);
        return senders[property.Id];
    }

    public async Task<IReadOnlyDictionary<Guid, PropertySenderInfo>> GetManyAsync(IReadOnlyCollection<Guid> propertyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var ids = propertyIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, PropertySenderInfo>();

        var properties = await db.Properties
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new PropertyRef(p.Id, p.OrgId, p.Country))
            .ToListAsync(ct);
        return await ReadAsync(properties, ct);
    }

    public async Task<PropertySenderInfo> SetAsync(Guid propertyId, ComplianceSenderType sender, string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (userId.Length > PropertyComplianceSender.UserIdMaxLength)
            throw new ArgumentException("The user id of a sender change is an id of at most 255 characters.", nameof(userId));
        if (!Enum.IsDefined(sender))
            throw new ArgumentOutOfRangeException(nameof(sender), sender, "Not a compliance sender.");

        var property = await FindPropertyAsync(propertyId, ct) ?? throw PropertyNotFound(propertyId);
        if (!IsItalian(property.Country))
            throw new DomainRuleException(NotApplicableCode, "SenderNotApplicable");

        var row = await db.PropertyComplianceSenders.FirstOrDefaultAsync(s => s.PropertyId == property.Id, ct);
        if (row is not null && row.AlloggiatiSender == sender && row.IstatSender == sender)
            return Stored(row); // The same sender again: nothing changes, nothing is recorded.

        // The sender before the change: the stored one, or the default the property had until now.
        var from = row?.AlloggiatiSender
                   ?? ((await ChannelManagedAsync([property], ct)).Contains(property.Id)
                       ? ComplianceSenderType.ChannelManager
                       : ComplianceSenderType.Manual);
        var now = Now();
        if (row is null)
        {
            row = new PropertyComplianceSender { OrgId = property.OrgId, PropertyId = property.Id, CreatedAt = now };
            db.PropertyComplianceSenders.Add(row);
        }

        // ISTAT follows Alloggiati (ADR-029-R5): one value for both, always.
        row.AlloggiatiSender = sender;
        row.IstatSender = sender;
        row.LastChangedAt = now;
        row.LastChangedByUserId = userId;
        if (from != sender)
        {
            activityLog.Record(OrgActivity.Of(
                property.OrgId,
                OrgActivityType.PropertySenderChanged,
                userId,
                property.Id.ToString(),
                (OrgActivityDetailKeys.FromSender, from.ToString()),
                (OrgActivityDetailKeys.ToSender, sender.ToString())));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException
                                           || OrgTeamAccess.IsUniqueViolation(ex, PropertyComplianceSender.PropertyIndexName))
        {
            // Another write set the sender of the property between the read and the save: nothing of this one was written.
            Forget(row);
            var current = await db.PropertyComplianceSenders
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.PropertyId == property.Id, ct);
            if (current is not null && current.AlloggiatiSender == sender)
                return Stored(current);

            throw new DomainConflictException(ChangedConcurrentlyCode, "SenderChangedConcurrently");
        }

        return Stored(row);
    }

    /// <summary>
    /// The rule of <c>TouristTaxComune.ForProperty</c>: a property is in Italy when its country is <c>IT</c> or not given. Elsewhere there
    /// is no Alloggiati Web nor ISTAT sender to choose (the Ticino file of a Swiss property is the host's, E03/F01/US09).
    /// </summary>
    internal static bool IsItalian(string? country) =>
        string.IsNullOrWhiteSpace(country) || string.Equals(country, "IT", StringComparison.OrdinalIgnoreCase);

    /// <summary>The senders of properties already found: stored, by default, or not applicable. Two queries at most.</summary>
    private async Task<Dictionary<Guid, PropertySenderInfo>> ReadAsync(IReadOnlyList<PropertyRef> properties, CancellationToken ct)
    {
        var senders = new Dictionary<Guid, PropertySenderInfo>(properties.Count);
        var italian = new List<PropertyRef>(properties.Count);
        foreach (var property in properties)
        {
            if (IsItalian(property.Country))
                italian.Add(property);
            else
                senders[property.Id] = Default(property, ComplianceSenderType.Manual, SenderSource.NotApplicable, applies: false);
        }

        if (italian.Count == 0)
            return senders;

        var ids = italian.Select(p => p.Id).ToList();
        var stored = await db.PropertyComplianceSenders
            .AsNoTracking()
            .Where(s => ids.Contains(s.PropertyId))
            .ToDictionaryAsync(s => s.PropertyId, ct);
        var withoutChoice = italian.Where(p => !stored.ContainsKey(p.Id)).ToList();
        var channelManaged = withoutChoice.Count == 0 ? new HashSet<Guid>() : await ChannelManagedAsync(withoutChoice, ct);

        foreach (var property in italian)
        {
            senders[property.Id] = stored.TryGetValue(property.Id, out var row)
                ? Stored(row)
                : channelManaged.Contains(property.Id)
                    ? Default(property, ComplianceSenderType.ChannelManager, SenderSource.DefaultConnected, applies: true)
                    : Default(property, ComplianceSenderType.Manual, SenderSource.DefaultNotConnected, applies: true);
        }

        return senders;
    }

    /// <summary>
    /// The properties whose listing is confirmed (<see cref="OctorateMappingStatus.Confirmed"/>) on a connection of the org of the property,
    /// of this environment and connected (PO-02): the channel manager sends for them by default.
    /// </summary>
    private async Task<HashSet<Guid>> ChannelManagedAsync(IReadOnlyCollection<PropertyRef> properties, CancellationToken ct)
    {
        var environment = OctorateEnvironments.Of(hostEnvironment);
        var ids = properties.Select(p => p.Id).ToList();
        var confirmed = await db.OctorateListingMappings
            .AsNoTracking()
            .Where(m => m.Status == OctorateMappingStatus.Confirmed && m.PropertyId != null && ids.Contains(m.PropertyId.Value))
            .Where(m => db.OctorateConnections.Any(c => c.Id == m.ConnectionId
                                                        && c.OrgId == m.OrgId
                                                        && c.Environment == environment
                                                        && c.Status == OctorateConnectionStatus.Connected))
            .Select(m => new { PropertyId = m.PropertyId!.Value, m.OrgId })
            .ToListAsync(ct);

        var orgOf = properties.ToDictionary(p => p.Id, p => p.OrgId);
        return confirmed
            .Where(m => orgOf.TryGetValue(m.PropertyId, out var orgId) && orgId == m.OrgId)
            .Select(m => m.PropertyId)
            .ToHashSet();
    }

    /// <summary>A property the caller's org has (tenant and soft-delete filters), with what the sender depends on.</summary>
    private Task<PropertyRef?> FindPropertyAsync(Guid propertyId, CancellationToken ct) =>
        db.Properties
            .AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => new PropertyRef(p.Id, p.OrgId, p.Country))
            .FirstOrDefaultAsync(ct);

    /// <summary>Drops what <see cref="SetAsync"/> staged (the row and its activity line) after a lost save.</summary>
    private void Forget(PropertyComplianceSender row)
    {
        db.Entry(row).State = EntityState.Detached;
        var subject = row.PropertyId.ToString();
        foreach (var line in db.ChangeTracker.Entries<OrgActivityEntry>()
                     .Where(e => e.State == EntityState.Added
                                 && e.Entity.Type == OrgActivityType.PropertySenderChanged
                                 && e.Entity.SubjectId == subject)
                     .ToList())
        {
            line.State = EntityState.Detached;
        }
    }

    private static PropertySenderInfo Stored(PropertyComplianceSender row) =>
        new(row.PropertyId, row.OrgId, row.AlloggiatiSender, row.IstatSender, SenderSource.Stored, true, row.LastChangedAt, row.LastChangedByUserId);

    private static PropertySenderInfo Default(PropertyRef property, ComplianceSenderType sender, SenderSource source, bool applies) =>
        new(property.Id, property.OrgId, sender, sender, source, applies, null, null);

    private static NotFoundException PropertyNotFound(Guid propertyId) =>
        new($"Property {propertyId} not found.") { Code = "property_not_found", MessageKey = "PropertyNotFound" };

    /// <summary>Now, cut to the microsecond PostgreSQL keeps, so what is written and what is read back compare equal.</summary>
    private DateTime Now() => UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);

    /// <summary>A property and what its sender depends on: its org and its country.</summary>
    private sealed record PropertyRef(Guid Id, Guid OrgId, string? Country);
}
