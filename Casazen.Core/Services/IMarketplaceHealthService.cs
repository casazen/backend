using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The health of the active integrations of an org (E09/F03/US01/T01, NF-57, MKT-04, OCT-14): what the host reads in the Active tab for each property and
/// channel, tool or function of the channel manager's license (<see cref="GetHealthAsync"/>), and the periodic sync that keeps it true
/// (<see cref="SyncAsync"/>, run every 30 minutes by <c>marketplace-connection-sync</c>). A problem becomes one «To do» item, which closes by itself
/// when the connection is healthy again.
/// </summary>
/// <remarks>
/// <para><b>A read never calls Octorate.</b> <see cref="GetHealthAsync"/> reads CasaZen's own rows and the quota of the last sync (kept in memory, not in a
/// table: absent after a restart until the next sync, and <see cref="MarketplaceQuota.ReadAt"/> says when it was read). Only the sync reads Octorate, and it
/// only reads: it never writes to a connection or to the license.</para>
/// <para><b>The reach of the caller.</b> A caller limited to some properties reads the rows of those properties only: a row with no property (a function of the
/// license written before the license was a property's) belongs to the whole org and is read by a caller who reaches the whole org only. A property outside the
/// scope is the 404 of a property that does not exist (<c>marketplace_property_not_found</c>), the same for another org's. Another org's rows are never read.</para>
/// <para><b>Words, not Octorate's words.</b> The error of a row is a stable code and a plain-language message from the resource files by code (Italian and
/// English); the text Octorate itself gives (the log of the channel) never reaches the user, nor the log, nor the answer (ADR-023-R6).</para>
/// <para><b>Only Octorate decides.</b> The sync keeps an Active activation Active while its connection is <c>ENABLED</c> and makes another one Active only when the
/// connection is <c>ENABLED</c> and its pulls are on (<see cref="OctorateConnectionInfo.PullsAreOn"/>: the activation wizard switches them on last, so «enabled» is an access
/// given, not a finished activation, and finishing it is the wizard's); it turns <c>WAITING</c> into Pending and <c>DISABLED</c> into To fix (<c>connection_disabled</c>); a
/// connection that is no longer listed is To fix too (<c>connection_missing</c>). A status nobody knew changes nothing and is logged as a warning, so a healthy connection is
/// never marked broken by a word of Octorate's that CasaZen has not met.</para>
/// </remarks>
public interface IMarketplaceHealthService
{
    /// <summary>
    /// One row for each activation of kind <see cref="MarketplaceActivationKind.Channel"/>, <see cref="MarketplaceActivationKind.Tool"/> or
    /// <see cref="MarketplaceActivationKind.LicensedFunction"/> inside the caller's scope that is not turned off, ordered by property name, then kind and
    /// name. With a <paramref name="propertyId"/> only that property's rows; without one every property of the scope (and, for a caller who reaches the whole
    /// org, the rows with no property). With the connector off nothing exists for anybody: an empty answer.
    /// <paramref name="language"/> (<c>it</c> or <c>en</c>) is the language of the names and of the messages.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>marketplace_property_not_found</c> (the property is not one of the org inside the scope).</exception>
    Task<MarketplaceHealth> GetHealthAsync(
        Guid orgId,
        HostScope scope,
        Guid? propertyId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the connections of the org's properties from Octorate and brings the activations in line with them, reconciles the license modules of the
    /// properties that have a function, reads the quota once, and raises or closes the «To do» items. Writes only while the connector is on and the org has a
    /// connected account in this environment; otherwise nothing is read or written. Each property is saved before the next is read, so a run that stops keeps
    /// what it did. A failure of one property (no answer, a refusal of the request) is logged with its code and left for the next run; a refusal of the org's
    /// own token ends the org's run; the properties are read the least recently synced first. The rate-limit error of the client
    /// (<c>OctorateRateLimitException</c>) is raised as it is: the run stops at the first one (nothing more is asked of Octorate) and the rest is left to the
    /// next run, and the job stops too.
    /// </summary>
    Task<MarketplaceSyncResult> SyncAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// The orgs the job syncs: the ones with an activation of a channel, a tool or a function of the license (with a property) that is not turned off and a
    /// connected account in this environment, the least recently synced first, so a run that was stopped continues with the orgs it did not reach. Empty
    /// while the connector is off.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListOrgsToSyncAsync(CancellationToken ct = default);
}

/// <summary>The stable codes of the problems the health panel shows, kept in <c>MarketplaceActivation.LastErrorCode</c> (E09/F03/US01/T01).</summary>
public static class MarketplaceHealthErrorCodes
{
    /// <summary>Octorate lists the connection as <c>DISABLED</c>: it was switched off on the channel manager.</summary>
    public const string ConnectionDisabled = "connection_disabled";

    /// <summary>The connection is no longer listed by Octorate (removed there). Also the code the activation wizard keeps when it finds the connection gone.</summary>
    public const string ConnectionMissing = MarketplaceActivationErrorCodes.ConnectionLost;

    /// <summary>After «mark connected» Octorate does not show the connection as working (kept by the activation wizard).</summary>
    public const string ConnectionNotEnabled = MarketplaceActivationErrorCodes.ConnectionNotEnabled;

    /// <summary>A function that was on is no longer in the license (kept by the reconciliation of the license).</summary>
    public const string LicenseModuleOff = MarketplaceFunctionErrorCodes.LicenseModuleOff;

    /// <summary>The channel or tool behind a connection failed (Octorate's 550); the same code as <c>OctoratePartnerFailureException</c>.</summary>
    public const string ChannelFailed = "octorate_partner_failed";

    /// <summary>Octorate did not answer; the same code as <c>OctorateServerException</c>. The next start or check goes on from there.</summary>
    public const string ChannelManagerUnavailable = "octorate_unavailable";

    /// <summary>The resource key of the message of a code nobody listed: it says only that the connection needs a look, and never a word of the channel manager.</summary>
    public const string GenericMessageKey = "Marketplace.Error.generic";

    /// <summary>Every code of the list above: each has a message in the resource files, Italian and English (<see cref="MessageKeyOf"/>).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ConnectionDisabled,
        ConnectionMissing,
        ConnectionNotEnabled,
        LicenseModuleOff,
        ChannelFailed,
        ChannelManagerUnavailable,
    ];

    /// <summary>The resource key of the plain-language message of a code.</summary>
    public static string MessageKeyOf(string code) => $"Marketplace.Error.{code}";
}

/// <summary>The health of the integrations a caller reaches, as <c>GET api/marketplace/health</c> answers it.</summary>
/// <param name="Items">One row for each activation, ordered by property name, then kind and name.</param>
public sealed record MarketplaceHealth(IReadOnlyList<MarketplaceHealthItem> Items);

/// <summary>One activation of a channel, a tool or a function of the license, with what the health panel shows of it.</summary>
/// <param name="ActivationId">The activation.</param>
/// <param name="PropertyId">The property it is for; <c>null</c> for a row of the whole org (read by a caller who reaches the whole org only).</param>
/// <param name="PropertyName">The name of the property; <c>null</c> without a property.</param>
/// <param name="Kind"><c>Channel</c>, <c>Tool</c> or <c>LicensedFunction</c>.</param>
/// <param name="Code">The portal code, or the code of the license module.</param>
/// <param name="Name">The name of the channel or tool as the catalog has it (its code when the cache has none), or of the function in the language asked.</param>
/// <param name="Status">Where it stands: Pending, Active or To fix (a function that waits for the license is Requested).</param>
/// <param name="IsDeclared">The status was declared by the host and Octorate has not proven it.</param>
/// <param name="LastSyncedAt">When CasaZen last read the state of it from Octorate (UTC); <c>null</c> until then.</param>
/// <param name="Error">The problem, in simple words; <c>null</c> when there is none.</param>
/// <param name="Quota">The quota of the channel manager's API at the last sync of the org, the same for every row; <c>null</c> until a sync read it.</param>
/// <param name="ChangeRequestedAt">When the host asked the channel manager for a change of a function (UTC), until the license answers; <c>null</c> otherwise.</param>
public sealed record MarketplaceHealthItem(
    Guid ActivationId,
    Guid? PropertyId,
    string? PropertyName,
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    MarketplaceActivationStatus Status,
    bool IsDeclared,
    DateTime? LastSyncedAt,
    MarketplaceHealthError? Error,
    MarketplaceQuota? Quota,
    DateTime? ChangeRequestedAt);

/// <summary>A problem in simple words: the stable code and its message from the resource files in the language asked. Never a text of Octorate's.</summary>
/// <param name="Code">The stable code (<see cref="MarketplaceHealthErrorCodes"/>), which a client may also translate.</param>
/// <param name="Message">What is wrong and what to do, in the language asked.</param>
public sealed record MarketplaceHealthError(string Code, string Message);

/// <summary>
/// The quota of the channel manager's API at the last sync (<c>GET /rest/v1/api/configuration</c>): <c>quotaStatus</c> counts the units used in the window
/// of the quota and <c>quotaLimit</c> is what it allows (E09 epic, S22, S25).
/// </summary>
/// <param name="Used">The units used (<c>quotaStatus</c>).</param>
/// <param name="Limit">The units the quota allows (<c>quotaLimit</c>).</param>
/// <param name="Warn">True when <paramref name="Used"/> reached <c>Marketplace:Health:QuotaWarnPercent</c> of <paramref name="Limit"/> (80 by default).</param>
/// <param name="ReadAt">When it was read (UTC).</param>
public sealed record MarketplaceQuota(int Used, int Limit, bool Warn, DateTime ReadAt);

/// <summary>What one sync of an org did, for the log of the job and for the tests.</summary>
/// <param name="Properties">The properties whose connections were read from Octorate.</param>
/// <param name="Updated">The activations whose status or problem changed.</param>
/// <param name="Unrecognised">The connections with a status nobody knew: they were left as they are.</param>
/// <param name="Skipped">The properties left for the next run without a failure: no confirmed listing, or a row changed by somebody else meanwhile.</param>
/// <param name="Failed">The reads that failed (no answer, a refusal of the request, a license that could not be read) and are tried again at the next run.</param>
/// <param name="TodoRaised">The «To do» items raised.</param>
/// <param name="TodoClosed">The «To do» items closed because the connection is healthy again.</param>
public sealed record MarketplaceSyncResult(
    int Properties,
    int Updated,
    int Unrecognised,
    int Skipped,
    int Failed,
    int TodoRaised,
    int TodoClosed)
{
    /// <summary>A sync that did nothing (the connector is off, or the org is not connected).</summary>
    public static MarketplaceSyncResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>The sum of two results: how the job counts a whole run.</summary>
    public MarketplaceSyncResult Plus(MarketplaceSyncResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new MarketplaceSyncResult(
            Properties + other.Properties,
            Updated + other.Updated,
            Unrecognised + other.Unrecognised,
            Skipped + other.Skipped,
            Failed + other.Failed,
            TodoRaised + other.TodoRaised,
            TodoClosed + other.TodoClosed);
    }
}
