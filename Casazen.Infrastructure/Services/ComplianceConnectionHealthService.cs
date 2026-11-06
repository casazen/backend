using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IComplianceConnectionHealthService" />
/// <remarks>
/// <para><b>Nothing new is read from Octorate here.</b> The state of the sender and the police fields come from
/// <see cref="IPropertySenderService.GetAsync"/> (one bounded read of the police account, every failure an unknown reading); this service
/// only turns that view into the state of the Alloggiati and ISTAT communications and adds what the host needs to see about the credentials
/// kept in CasaZen. A property of another org is the <c>NotFoundException</c> of the sender service.</para>
/// <para><b>Secrets.</b> The Questura credentials are never read: the sender view says only whether they exist, and the one extra read (the
/// outcome of the last verification, for the sender «CasaZen sends») selects that column alone, so the encrypted username, password and web
/// service key are not even loaded. Nothing is logged but ids, a state and a stable code.</para>
/// <para><b>Bulk reads.</b> <see cref="GetManyAsync"/> keeps each answer in memory for
/// <see cref="ComplianceConnectionHealthRules.CacheDuration"/> and reads at most
/// <see cref="ComplianceConnectionHealthRules.MaxLiveReadsPerCall"/> properties from Octorate per call, so a cockpit of many properties never
/// waits for many calls; it serves from memory only the properties that the tenant filter lets the caller see.</para>
/// </remarks>
public sealed class ComplianceConnectionHealthService(
    IPropertySenderService senders,
    AppDbContext db,
    IMemoryCache cache,
    ILogger<ComplianceConnectionHealthService> logger,
    TimeProvider? timeProvider = null) : IComplianceConnectionHealthService
{
    /// <summary>The reason of a communication whose Questura credentials, kept in CasaZen, were refused by the service.</summary>
    internal const string CredentialsRefusedReason = "credentials_refused";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ComplianceConnectionHealth> GetAsync(Guid propertyId, CancellationToken ct)
    {
        var health = await ReadAsync(propertyId, ct);
        Remember(health);
        return health;
    }

    public async Task<IReadOnlyDictionary<Guid, ComplianceConnectionHealth>> GetManyAsync(
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var answers = new Dictionary<Guid, ComplianceConnectionHealth>();
        if (propertyIds.Count == 0)
            return answers;

        // The properties of the caller's org only (the tenant filter): an id of another org is neither read nor served from memory.
        var wanted = propertyIds.Distinct().ToList();
        var visible = await db.Properties.AsNoTracking()
            .Where(p => wanted.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(ct);

        // What is in memory is served; of the others, the ones never read or read longest ago are read first, so that over the calls every property
        // gets its turn (a fixed order would read the same five every time the answers have expired and never reach the sixth).
        var waiting = new List<Guid>();
        foreach (var propertyId in visible)
        {
            if (cache.TryGetValue(CacheKeyOf(propertyId), out ComplianceConnectionHealth? known) && known is not null)
                answers[propertyId] = known;
            else
                waiting.Add(propertyId);
        }

        var liveReads = 0;
        foreach (var propertyId in waiting.OrderBy(LastReadAt).ThenBy(id => id))
        {
            if (liveReads >= ComplianceConnectionHealthRules.MaxLiveReadsPerCall)
            {
                answers[propertyId] = NotCheckedYet(propertyId);
                continue;
            }

            liveReads++;
            var health = await ReadAsync(propertyId, ct);
            Remember(health);
            answers[propertyId] = health;
        }

        return answers;
    }

    /// <summary>When the property was last read from Octorate (UTC), whether or not the answer is still in memory; the beginning of time when never.</summary>
    private DateTime LastReadAt(Guid propertyId) =>
        cache.TryGetValue(LastReadKeyOf(propertyId), out DateTime at) ? at : DateTime.MinValue;

    /// <summary>The health of a property from its sender view; the sender service decides what Octorate proved.</summary>
    private async Task<ComplianceConnectionHealth> ReadAsync(Guid propertyId, CancellationToken ct)
    {
        var view = await senders.GetAsync(propertyId, ct);

        // Only for the sender «CasaZen sends», which no property can be set to in this release: whether the service refused the credentials.
        var refused = view.Applies
                      && view.Sender == ComplianceSenderType.CasaZen
                      && view.QuesturaCredentialsConfigured
                      && await db.PropertyQuesturaCredentials.AsNoTracking()
                          .Where(c => c.PropertyId == propertyId)
                          .Select(c => c.LastVerificationOk)
                          .FirstOrDefaultAsync(ct) == false;

        var health = new ComplianceConnectionHealth(
            propertyId,
            view.Applies,
            Line(view, view.Sender, refused),
            Line(view, view.IstatSender, refused),
            view.QuesturaCredentialsConfigured,
            // The verification of the Questura credentials is postponed (plan of 2026-11-05): the receipts cannot be downloaded, so the
            // credentials are never usable, whatever the row says. Never "true" until that verification exists.
            false,
            _clock.GetUtcNow().UtcDateTime);

        logger.LogDebug(
            "Connection health of property {PropertyId}: Alloggiati {State}, ISTAT {IstatState}",
            propertyId, health.Alloggiati.State, health.Istat.State);
        return health;
    }

    private static ConnectionHealthLine Line(PropertySenderView view, ComplianceSenderType sender, bool credentialsRefused)
    {
        var effectiveSender = view.Applies ? sender : ComplianceSenderType.Manual;
        var state = ComplianceConnectionHealthRules.StateOf(
            effectiveSender,
            view.Applies,
            view.State,
            credentialsUsable: false,
            credentialsRefused);

        // The police account is the channel manager's: for any other sender its fields are not about this send.
        var channelManager = effectiveSender == ComplianceSenderType.ChannelManager;
        var reason = effectiveSender switch
        {
            ComplianceSenderType.ChannelManager when state != ConnectionHealthState.Active => view.Reason,
            ComplianceSenderType.CasaZen when credentialsRefused => CredentialsRefusedReason,
            ComplianceSenderType.CasaZen => SenderReasonCodes.CasaZenNotAvailable,
            _ => null,
        };

        return new ConnectionHealthLine(
            effectiveSender,
            state,
            channelManager ? view.PoliceAccountValid : null,
            channelManager ? view.PoliceAutorun : null,
            channelManager ? view.PoliceLastCron : null,
            reason,
            channelManager ? view.DeclaredAt : null);
    }

    /// <summary>
    /// The placeholder for a property that has no answer in memory and is over the limit of live reads of this call: unknown, never active. It
    /// is asked for by the status engine only for properties whose sender is the channel manager, the one sender whose health is read live.
    /// </summary>
    private ComplianceConnectionHealth NotCheckedYet(Guid propertyId)
    {
        var line = new ConnectionHealthLine(
            ComplianceSenderType.ChannelManager,
            ConnectionHealthState.Unknown,
            null,
            null,
            null,
            ComplianceConnectionHealthRules.NotCheckedYetReason,
            null);
        return new ComplianceConnectionHealth(propertyId, true, line, line, false, false, _clock.GetUtcNow().UtcDateTime);
    }

    private void Remember(ComplianceConnectionHealth health)
    {
        cache.Set(CacheKeyOf(health.PropertyId), health, ComplianceConnectionHealthRules.CacheDuration);
        cache.Set(LastReadKeyOf(health.PropertyId), health.CheckedAt, ComplianceConnectionHealthRules.LastReadMemory);
    }

    /// <summary>The key of the answer of a property in memory (internal: a test lets it expire).</summary>
    internal static string CacheKeyOf(Guid propertyId) => $"compliance-connection-health:{propertyId:N}";

    private static string LastReadKeyOf(Guid propertyId) => $"compliance-connection-health-read:{propertyId:N}";
}
