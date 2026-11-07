using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The functions of the channel manager's license for one property (E09/F02/US02/T01, NF-56, MKT-03, PO-12, D21): which of the twelve modules
/// (<see cref="MarketplaceLicensedFunctions.Codes"/>) are on, the plan and the room limit, the guided request of a module that is off, and the
/// reconciliation of the requests with the license. CasaZen only reads the license; it never writes it (the partner's permission on it is read-only,
/// S25): a request is the host's own, to be made to the channel manager, and CasaZen only records it and waits for the license to show it.
/// </summary>
/// <remarks>
/// <para><b>Per property.</b> The license belongs to the accommodation (D14), so a function is asked for, and is on, for one property: its
/// <c>MarketplaceActivation</c> row has the property (kind <c>LicensedFunction</c>, target = the module code). Every method but
/// <see cref="ReconcileAsync"/> takes the caller's <see cref="HostScope"/>: a property outside it is the 404 of a property that does not exist
/// (<c>marketplace_property_not_found</c>), the same for another org's. The org is checked first: the scope of another org is an
/// <see cref="ArgumentException"/> (fail closed).</para>
/// <para><b>What needs Octorate.</b> Every method needs the connector on, an org with a connected account in this environment
/// (<c>marketplace_octorate_not_connected</c>, 409) and the property's confirmed listing (<c>marketplace_property_not_linked</c>, 422), whose accommodation
/// id is the <c>acc</c> of the call. <see cref="ListAsync"/> asks Octorate nothing; the others read the license live, once.</para>
/// <para><b>Only the license decides.</b> A function is Active only when the license shows its module on, and a module the license does not state is
/// neither on nor off: nothing changes for it. A request that was not answered stays Requested. The tier and the room limit are those of the last live
/// read, kept in memory (no table): after a restart they are absent until the next read, and the answer says when it was read.</para>
/// </remarks>
public interface IMarketplaceLicensedFunctionService
{
    /// <summary>
    /// The twelve modules of the property in the order of the catalog, each with its state from the property's activation rows (no call to
    /// Octorate), and the plan and the room limit of the last read. A module with no row is <see cref="MarketplaceFunctionStatus.NotActive"/>.
    /// <paramref name="language"/> (<c>it</c> or <c>en</c>) is the language of the names and of the request guidance.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>marketplace_property_not_found</c>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><c>marketplace_octorate_not_connected</c>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException"><c>marketplace_property_not_linked</c>.</exception>
    Task<MarketplaceFunctionList> ListAsync(
        Guid orgId,
        HostScope scope,
        Guid propertyId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the license of the property live and reconciles the requests with it (<see cref="ReconcileAsync"/>), then answers what
    /// <see cref="ListAsync"/> answers. A failure of the read (no answer, a refusal, the rate limit) is the typed error of the client, never a list
    /// that looks as if the license had been read.
    /// </summary>
    Task<MarketplaceFunctionList> RefreshAsync(
        Guid orgId,
        HostScope scope,
        Guid propertyId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default);

    /// <summary>
    /// Records that the host asked the channel manager for a module: reads the license live (and reconciles with it), then creates the activation
    /// of the module for the property with status <see cref="MarketplaceFunctionStatus.Requested"/>, declared by the host, or returns the one that
    /// exists. Idempotent: a request under way is returned as it is, with its first <c>RequestedAt</c>; a module that went off (To fix) or was turned
    /// off is asked for again. When the live read fails with a failure worth a retry (no answer, a 5xx, the quota used) and a request is under way
    /// (Requested, or Pending), that request is returned as it is, so that asking again always gives the same one; with no request under way the typed
    /// error of the client is raised and nothing is recorded. <paramref name="userId"/> is the person who asked, for the log. Nothing is ever sent to
    /// the channel manager: the request is the host's.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>marketplace_function_unknown</c> (the code is not a module of the list), <c>marketplace_property_not_found</c>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// <c>marketplace_function_already_active</c> (the license shows the module on: the card is made Active first), <c>marketplace_octorate_not_connected</c>.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException"><c>marketplace_property_not_linked</c>.</exception>
    Task<MarketplaceFunctionEntry> RequestAsync(
        Guid orgId,
        HostScope scope,
        Guid propertyId,
        string moduleCode,
        string userId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the license of the property live and brings its activation rows in line with it (the health sync of the active integrations calls this,
    /// with no caller: the org and the property are checked, and the property must be the org's). A module the license shows on: a Requested row
    /// (or one to fix) becomes Active, verified, no longer declared; a module with no row gets a verified Active row. A verified Active row whose
    /// module the license shows off becomes To fix with <see cref="MarketplaceFunctionErrorCodes.LicenseModuleOff"/>. A Requested row stays as it is.
    /// A module the license does not state changes nothing. A row whose state changes closes its open change request.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>marketplace_property_not_found</c> (not a property of the org).</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><c>marketplace_octorate_not_connected</c>: nothing is read or written.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException"><c>marketplace_property_not_linked</c>: nothing is read or written.</exception>
    Task ReconcileAsync(Guid orgId, Guid propertyId, CancellationToken ct = default);
}

/// <summary>The stable codes of the licensed functions (E09/F02/US02/T01, NF-56): the answers of the request, and the code kept on a row.</summary>
public static class MarketplaceFunctionErrorCodes
{
    /// <summary>409: the license already shows the module on, so there is nothing to ask the channel manager.</summary>
    public const string AlreadyActive = "marketplace_function_already_active";

    /// <summary>404: the code is not one of the modules of <see cref="MarketplaceLicensedFunctions.Codes"/>.</summary>
    public const string Unknown = "marketplace_function_unknown";

    /// <summary>Kept in <c>MarketplaceActivation.LastErrorCode</c> of a function that was Active and that the license no longer shows on.</summary>
    public const string LicenseModuleOff = "license_module_off";
}

/// <summary>Where a function stands for a property: the states of the page of the licensed functions.</summary>
public enum MarketplaceFunctionStatus
{
    /// <summary>The license does not show it on and nobody asked for it (no activation row, or a turned off one).</summary>
    NotActive = 1,

    /// <summary>The host asked the channel manager for it and the license does not show it yet; declared by the host, not proven.</summary>
    Requested = 2,

    /// <summary>The license shows it on.</summary>
    Active = 3,

    /// <summary>It was on and the license no longer shows it: the host has to ask again or check with the channel manager.</summary>
    ToFix = 4,
}

/// <summary>
/// One function of the license for one property. <see cref="Name"/> and <see cref="RequestGuidance"/> are in the language asked.
/// </summary>
/// <param name="Code">The module code (<see cref="MarketplaceLicensedFunctions.Codes"/>).</param>
/// <param name="Name">The name of the function.</param>
/// <param name="Status">Where it stands for the property.</param>
/// <param name="ActivationId">The activation row, <c>null</c> while there is none.</param>
/// <param name="IsDeclared">The status was declared by the host and the license did not prove it (a request).</param>
/// <param name="RequestedAt">When the host asked for it (UTC); <c>null</c> when nobody did.</param>
/// <param name="VerifiedAt">When the license first showed it on (UTC); <c>null</c> until then.</param>
/// <param name="LastSyncedAt">When CasaZen last read the license for it (UTC).</param>
/// <param name="LastErrorCode">The code of the last problem found (<see cref="MarketplaceFunctionErrorCodes.LicenseModuleOff"/>), a code the client turns into simple words; <c>null</c> when there is none.</param>
/// <param name="ChangeRequestedAt">When the host asked the channel manager for a change of it (UTC), while the license has not answered; <c>null</c> otherwise.</param>
/// <param name="RequestGuidance">
/// What to ask the channel manager, in simple words, with the name of the function and of the property put in; <c>null</c> for a function that is Active.
/// </param>
public sealed record MarketplaceFunctionEntry(
    string Code,
    string Name,
    MarketplaceFunctionStatus Status,
    Guid? ActivationId,
    bool IsDeclared,
    DateTime? RequestedAt,
    DateTime? VerifiedAt,
    DateTime? LastSyncedAt,
    string? LastErrorCode,
    DateTime? ChangeRequestedAt,
    string? RequestGuidance);

/// <summary>The license of one property as the page of the licensed functions shows it.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Tier">The plan of the channel manager at the last read; <c>null</c> until a read.</param>
/// <param name="RoomsLimit">The number of rooms the license covers at the last read; <c>null</c> until a read.</param>
/// <param name="NoRoomLicense">
/// True when the last read found a room limit of 0: without a license per room the channel manager publishes no prices and no availability, and the page
/// says so. False when the limit is more than 0 or not known.
/// </param>
/// <param name="LicenseReadAt">When the license was last read (UTC); <c>null</c> until a read (also after a restart: the read is kept in memory).</param>
/// <param name="Functions">The twelve modules, in the order of the catalog.</param>
public sealed record MarketplaceFunctionList(
    Guid PropertyId,
    string? Tier,
    int? RoomsLimit,
    bool NoRoomLicense,
    DateTime? LicenseReadAt,
    IReadOnlyList<MarketplaceFunctionEntry> Functions);
