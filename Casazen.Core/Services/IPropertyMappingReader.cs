namespace Casazen.Core.Services;

/// <summary>
/// The confirmed mapping of the CasaZen properties to the listings of the channel manager, as the flows of the activation need it (NF-106,
/// E10/F01/US01/T02): the one port through which epic E10 reads the mapping, so it never reads the mapping tables itself. The mapping is
/// built by <see cref="IChannelMappingService"/> (E02/F01/US02/T03); <c>ChannelMappingReader</c> implements this port over it.
/// </summary>
public interface IPropertyMappingReader
{
    /// <summary>
    /// False when nothing provides the mapping: a flow that needs it has the <c>mapping</c> prerequisite unmet
    /// (<c>mapping_unavailable</c>), never met by default.
    /// </summary>
    bool IsProvided { get; }

    /// <summary>
    /// The Octorate accommodation id of each property in <paramref name="propertyIds"/> whose listing is <b>confirmed</b> by a person, by
    /// property id. A property whose listing is proposed, ambiguous, rejected or not found is left out of the answer: unconfirmed is not
    /// mapped. A property of another org is never in it.
    /// </summary>
    /// <exception cref="Exception">
    /// Anything the implementation throws when the mapping cannot be read; the caller treats it as «unavailable» and never as «confirmed».
    /// </exception>
    Task<IReadOnlyDictionary<Guid, string>> GetConfirmedAccommodationIdsAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct);
}
