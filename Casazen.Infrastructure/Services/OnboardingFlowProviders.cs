using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IPropertyMappingReader"/> over <see cref="IChannelMappingService.ListAsync"/> (E10/F01/US01/T02, NF-106): the confirmed rows
/// only, so epic E10 never reads the mapping tables itself. A row that is proposed, ambiguous or rejected, or whose property was deleted, is
/// not a mapping.
/// </summary>
public sealed class ChannelMappingReader(IChannelMappingService mappings) : IPropertyMappingReader
{
    public bool IsProvided => true;

    public async Task<IReadOnlyDictionary<Guid, string>> GetConfirmedAccommodationIdsAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var confirmed = new Dictionary<Guid, string>();
        if (propertyIds.Count == 0)
            return confirmed;

        var wanted = propertyIds.ToHashSet();
        foreach (var row in await mappings.ListAsync(orgId, ct))
        {
            // The unique index of the confirmed mappings holds one listing for each property.
            if (row.Status == OctorateMappingStatus.Confirmed && row.Property is { } property && wanted.Contains(property.Id))
                confirmed[property.Id] = row.OctorateAccommodationId;
        }

        return confirmed;
    }
}

/// <summary>
/// The prerequisites the flows share (E10/F01/US01/T02, NF-106): the org's Octorate connection and the confirmed mapping of a property. Read
/// through the stored facts only (no call to Octorate), each once for the request: one instance serves one request (it is scoped), so the
/// three flows that need the connection and the mapping read them once for a page of properties, not once for each flow.
/// </summary>
public sealed class FlowPrerequisiteReader(
    AppDbContext db,
    IPropertyMappingReader mappingReader,
    IHostEnvironment hostEnvironment,
    ILogger<FlowPrerequisiteReader> logger)
{
    private readonly Dictionary<Guid, FlowPrerequisite> _connections = [];
    private readonly Dictionary<(Guid OrgId, Guid PropertyId), FlowPrerequisite> _mappings = [];

    /// <summary>
    /// The <c>connection</c> prerequisite of the org: its <see cref="OctorateConnection"/> of this environment is
    /// <see cref="OctorateConnectionStatus.Connected"/>. Not met with <see cref="OnboardingFlowReasons.ConnectionLost"/> when the connection is
    /// disconnected or in error, and with <see cref="OnboardingFlowReasons.ConnectionMissing"/> when there is none.
    /// </summary>
    public async Task<FlowPrerequisite> ConnectionAsync(Guid orgId, CancellationToken ct)
    {
        if (_connections.TryGetValue(orgId, out var known))
            return known;

        var environment = OctorateEnvironments.Of(hostEnvironment);
        var status = await db.OctorateConnections
            .AsNoTracking()
            .Where(c => c.OrgId == orgId && c.Environment == environment)
            .Select(c => (OctorateConnectionStatus?)c.Status)
            .FirstOrDefaultAsync(ct);

        var prerequisite = status switch
        {
            OctorateConnectionStatus.Connected => FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.Connection),
            null => FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Connection, OnboardingFlowReasons.ConnectionMissing),
            _ => FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Connection, OnboardingFlowReasons.ConnectionLost),
        };
        _connections[orgId] = prerequisite;
        return prerequisite;
    }

    /// <summary>
    /// The <c>mapping</c> prerequisite of each property: met when its listing is confirmed. A property whose listing is proposed, ambiguous
    /// or not found is not met (<see cref="OnboardingFlowReasons.MappingUnconfirmed"/>); when the mapping cannot be read, or nothing provides
    /// it, every property is not met (<see cref="OnboardingFlowReasons.MappingUnavailable"/>): the mapping is never presumed confirmed.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, FlowPrerequisite>> MappingAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var ids = propertyIds.Distinct().ToList();
        var unread = ids.Where(id => !_mappings.ContainsKey((orgId, id))).ToList();
        if (unread.Count > 0)
        {
            var confirmed = await ReadConfirmedAsync(orgId, unread, ct);
            foreach (var id in unread)
            {
                _mappings[(orgId, id)] = confirmed is null
                    ? FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Mapping, OnboardingFlowReasons.MappingUnavailable)
                    : confirmed.ContainsKey(id)
                        ? FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.Mapping)
                        : FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Mapping, OnboardingFlowReasons.MappingUnconfirmed);
            }
        }

        return ids.ToDictionary(id => id, id => _mappings[(orgId, id)]);
    }

    /// <summary>The confirmed mapping of the properties, or <c>null</c> when it cannot be read (the caller never treats that as confirmed).</summary>
    private async Task<IReadOnlyDictionary<Guid, string>?> ReadConfirmedAsync(Guid orgId, IReadOnlyCollection<Guid> propertyIds, CancellationToken ct)
    {
        if (!mappingReader.IsProvided)
            return null;

        try
        {
            return await mappingReader.GetConfirmedAccommodationIdsAsync(orgId, propertyIds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The org and the kind of failure only: no listing, no name, no token.
            logger.LogWarning("The channel mapping of org {OrgId} could not be read for the flow status ({ExceptionType})", orgId, ex.GetType().Name);
            return null;
        }
    }
}

/// <summary>
/// The listings flow (E10/F01/US01/T02, NF-106): the content of the property on the channels. Needs the connection, the confirmed mapping
/// and no channel that refused the content. Not available when every channel the property is on is written on the channel itself (Expedia):
/// CasaZen sends nothing there, so there is nothing to complete.
/// </summary>
public sealed class ListingsFlowProvider(AppDbContext db, FlowPrerequisiteReader prerequisites) : IFlowReadinessProvider
{
    public OnboardingFlow Flow => OnboardingFlow.Listings;

    public int Order => FlowProviderOrders.Provider;

    public async Task<IReadOnlyDictionary<Guid, FlowReadiness>> EvaluateAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var ids = propertyIds.Distinct().ToList();
        var connection = await prerequisites.ConnectionAsync(orgId, ct);
        var mapping = await prerequisites.MappingAsync(orgId, ids, ct);
        var channelStates = (await db.ListingChannelStates
                .AsNoTracking()
                .Where(s => s.OrgId == orgId && ids.Contains(s.PropertyId))
                .Select(s => new { s.PropertyId, s.Status })
                .ToListAsync(ct))
            .ToLookup(s => s.PropertyId, s => s.Status);

        var readiness = new Dictionary<Guid, FlowReadiness>(ids.Count);
        foreach (var id in ids)
        {
            var states = channelStates[id].ToList();
            if (states.Count > 0 && states.All(status => status == ChannelPublishStatus.ManagedOnChannel))
            {
                readiness[id] = FlowReadiness.NotAvailable(OnboardingFlowReasons.ContentManagedOnChannel);
                continue;
            }

            // No row, or no row that a channel refused: the content is not what stops the flow.
            var content = states.Contains(ChannelPublishStatus.ToFix)
                ? FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Content, OnboardingFlowReasons.ChannelContentToFix)
                : FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.Content);
            readiness[id] = FlowReadiness.Needing(connection, mapping[id], content);
        }

        return readiness;
    }
}

/// <summary>The bookings flow (E10/F01/US01/T02, NF-106): the bookings and the calendar of the property. Needs the connection and the confirmed mapping.</summary>
public sealed class BookingsFlowProvider(FlowPrerequisiteReader prerequisites) : IFlowReadinessProvider
{
    public OnboardingFlow Flow => OnboardingFlow.Bookings;

    public int Order => FlowProviderOrders.Provider;

    public async Task<IReadOnlyDictionary<Guid, FlowReadiness>> EvaluateAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var ids = propertyIds.Distinct().ToList();
        var connection = await prerequisites.ConnectionAsync(orgId, ct);
        var mapping = await prerequisites.MappingAsync(orgId, ids, ct);

        return ids.ToDictionary(id => id, id => FlowReadiness.Needing(connection, mapping[id]));
    }
}

/// <summary>
/// The check-in and compliance flow (E10/F01/US01/T02, NF-106): the guests' check-in and the Alloggiati, ISTAT and tourist tax duties. Needs
/// the connection, the confirmed mapping, a stored single sender of the police and ISTAT communications (ADR-029: the sender is one value,
/// so two automatic senders cannot be represented), a valid CIN and a tourist tax rate in force today in the comune of the property.
/// </summary>
/// <remarks>
/// <para>The tourist tax rate is looked up through <see cref="ITouristTaxQuoteService"/> on today's date in Europe/Rome (the injected
/// <see cref="TimeProvider"/>): a comune with no rate in force is not met, never presumed to have a zero rate. One lookup for each comune of
/// the request, not for each property.</para>
/// <para>A property outside Italy has no police sender to choose nor Italian tourist tax to apply (<see cref="TouristTaxComune.IsItalianCountry"/>):
/// the flow is not provided for it, instead of a state the host could never complete.</para>
/// </remarks>
public sealed class CheckInComplianceFlowProvider(
    AppDbContext db,
    FlowPrerequisiteReader prerequisites,
    ITouristTaxQuoteService touristTax,
    TimeProvider timeProvider) : IFlowReadinessProvider
{
    private readonly Dictionary<(string IstatCode, string Name), bool> _rateInForce = [];

    public OnboardingFlow Flow => OnboardingFlow.CheckInCompliance;

    public int Order => FlowProviderOrders.Provider;

    public async Task<IReadOnlyDictionary<Guid, FlowReadiness>> EvaluateAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var ids = propertyIds.Distinct().ToList();
        var connection = await prerequisites.ConnectionAsync(orgId, ct);
        var mapping = await prerequisites.MappingAsync(orgId, ids, ct);

        var properties = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && ids.Contains(p.Id))
            .Select(p => new { p.Id, p.CinCode, p.Country, p.ComuneIstatCode, p.City })
            .ToListAsync(ct);
        var withSender = (await db.PropertyComplianceSenders
                .AsNoTracking()
                .Where(s => s.OrgId == orgId && ids.Contains(s.PropertyId))
                .Select(s => new { s.PropertyId, Single = s.AlloggiatiSender == s.IstatSender })
                .ToListAsync(ct))
            .Where(s => s.Single)
            .Select(s => s.PropertyId)
            .ToHashSet();

        var today = timeProvider.TodayInRomeAsDateOnly();
        var readiness = new Dictionary<Guid, FlowReadiness>(ids.Count);
        foreach (var property in properties)
        {
            if (!TouristTaxComune.IsItalianCountry(property.Country))
            {
                readiness[property.Id] = FlowReadiness.NotProvided;
                continue;
            }

            var singleSender = withSender.Contains(property.Id)
                ? FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.SingleSender)
                : FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.SingleSender, OnboardingFlowReasons.SenderMissing);
            var cin = CinFormat.IsValid(property.CinCode)
                ? FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.Cin)
                : FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Cin, OnboardingFlowReasons.CinMissingOrInvalid);
            var comune = TouristTaxComune.ForProperty(
                new Property { Country = property.Country, ComuneIstatCode = property.ComuneIstatCode, City = property.City });
            var taxRate = await HasRateInForceAsync(comune, today, ct)
                ? FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.TaxRate)
                : FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.TaxRate, OnboardingFlowReasons.TaxRateMissing);

            readiness[property.Id] = FlowReadiness.Needing(connection, mapping[property.Id], singleSender, cin, taxRate);
        }

        return readiness;
    }

    private async Task<bool> HasRateInForceAsync(TouristTaxComune comune, DateOnly today, CancellationToken ct)
    {
        // The same comune for many properties (an agency's portfolio) is looked up once; the key is what the lookup compares.
        var key = (comune.IstatCode?.Trim() ?? string.Empty, TouristTaxComune.NormalizeName(comune.Name));
        if (!_rateInForce.TryGetValue(key, out var found))
        {
            found = (await touristTax.GetRatesInForceAsync(comune, today, ct)).Count > 0;
            _rateInForce[key] = found;
        }

        return found;
    }
}

/// <summary>
/// The placeholder of a flow nobody has built yet (a provider of the epic that owns the flow registers with a higher
/// <see cref="IFlowReadinessProvider.Order"/>, and the placeholder stops being asked): every property is «not available»,
/// <see cref="OnboardingFlowReasons.NotProvided"/>. A flow with no provider is never presumed ready. Today every flow has its provider (the messages,
/// documents and reports ones are in <c>OnboardingCrossEpicProviders</c>, E10/F01/US04/T01) and nothing registers this one; it is the model for a
/// future flow and what the tests use as the answer of a flow with nobody behind it.
/// </summary>
public sealed class NotProvidedFlowProvider(OnboardingFlow flow) : IFlowReadinessProvider
{
    public OnboardingFlow Flow { get; } = flow;

    public int Order => FlowProviderOrders.Placeholder;

    public Task<IReadOnlyDictionary<Guid, FlowReadiness>> EvaluateAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        IReadOnlyDictionary<Guid, FlowReadiness> readiness = propertyIds
            .Distinct()
            .ToDictionary(id => id, _ => FlowReadiness.NotProvided);
        return Task.FromResult(readiness);
    }
}
