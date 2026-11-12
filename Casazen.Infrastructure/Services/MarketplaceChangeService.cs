using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IMarketplaceChangeService"/> (E09/F03/US01/T02, NF-58, MKT-05): the settings of a channel or a tool, the preview and the confirmed deactivation of
/// an activation, and the change request of a function of the channel manager's license. A channel or a tool is changed and deleted through the operations on a
/// connection of <see cref="IOctorateApiClient"/>; a partner goes through <see cref="IMarketplacePartnerActivationService.UndeclareAsync"/>; a function of the
/// license is never changed or switched off by CasaZen.
/// </summary>
/// <remarks>
/// <para><b>Who may.</b> The activation is looked up by its id inside the org, and then the caller's scope decides: one of a property inside the scope, or one with no
/// property for a caller who reaches the whole org, otherwise the 404 of an activation that does not exist (another org's, outside the scope, of a deleted property).
/// The activations that go through Octorate answer that same 404 while the connector is off.</para>
/// <para><b>A yes first.</b> A deactivation without <c>confirmed</c> is a 422 before anything is read: no connection is deleted and no row changes.</para>
/// <para><b>The order of a deactivation.</b> The connection is deleted on the channel manager first (one it no longer has is already what was asked); then the open «To do»
/// item of the activation is closed and the row becomes Deactivated, kept as history without its connection id. A failure of the channel manager is raised as it is and
/// changes nothing, so the host can try again; if the row cannot be saved after the delete, the next sync finds the connection gone and says so.</para>
/// <para><b>Credentials.</b> The login details reach <see cref="UpdateSettingsAsync"/> in <see cref="MarketplaceSettingsChange"/> and go to the channel manager in one call:
/// no column, no draft, no log line and no message holds them, and no exception of this class carries them.</para>
/// <para><b>Logs.</b> Ids, the kind and the names of the members changed (never their values).</para>
/// </remarks>
public sealed class MarketplaceChangeService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IMarketplacePartnerActivationService partners,
    IInternalActivityService activities,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    IMarketplaceTexts texts,
    TimeProvider timeProvider,
    ILogger<MarketplaceChangeService> logger) : IMarketplaceChangeService
{
    /// <summary>The sentences of the preview of a deactivation: <c>Marketplace.Deactivate.{kind}.Effect1</c> to <c>Effect{n}</c>.</summary>
    private static readonly IReadOnlyDictionary<MarketplaceActivationKind, int> EffectCounts = new Dictionary<MarketplaceActivationKind, int>
    {
        [MarketplaceActivationKind.Channel] = 3,
        [MarketplaceActivationKind.Tool] = 2,
        [MarketplaceActivationKind.LicensedFunction] = 2,
        [MarketplaceActivationKind.Partner] = 2,
    };

    public async Task<MarketplaceActivationOutcome> UpdateSettingsAsync(
        Guid orgId,
        HostScope scope,
        Guid activationId,
        MarketplaceSettingsChange settings,
        string userId,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var activation = await FindAsync(orgId, scope, activationId, track: true, ct);

        // A channel or a tool is always a property's; a row that is not (or that was turned off) has no connection to change.
        if (activation.Kind is not (MarketplaceActivationKind.Channel or MarketplaceActivationKind.Tool)
            || activation.Status == MarketplaceActivationStatus.Deactivated
            || activation.PropertyId is not { } propertyId)
        {
            throw NotSupported();
        }

        var patch = await PatchOfAsync(activation, settings, ct);
        var link = await RequireLinkAsync(orgId, propertyId, ct);
        var connectionId = activation.OctorateConnectionId is { Length: > 0 } known ? known : throw ConnectionMissing();

        try
        {
            using (orgScope.Use(orgId))
                await client.UpdateConnectionAsync(link.AccommodationId, connectionId, patch, ct);
        }
        catch (OctorateNotFoundException)
        {
            // The channel manager no longer has the connection: nothing was changed there, and the activation has to start again.
            throw ConnectionMissing();
        }

        var now = Now();
        await SaveAsync(activation, a => a.UpdatedAt = now, ct);

        logger.LogInformation(
            "Marketplace activation {ActivationId} of org {OrgId}: settings changed ({Members}) by {UserId}",
            activation.Id, orgId, MembersOf(patch), userId);
        return OutcomeOf(activation);
    }

    public async Task<MarketplaceDeactivationPreview> PreviewDeactivationAsync(
        Guid orgId,
        HostScope scope,
        Guid activationId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        var lang = MarketplaceLanguages.Normalize(language);

        var activation = await FindAsync(orgId, scope, activationId, track: false, ct);
        var name = await NameOfAsync(activation, lang, ct);
        var effects = new List<string>();
        for (var number = 1; number <= EffectCounts[activation.Kind]; number++)
            effects.Add(texts.Get($"Marketplace.Deactivate.{activation.Kind}.Effect{number}", lang, name));

        return new MarketplaceDeactivationPreview(
            activation.Id,
            activation.Kind,
            activation.TargetCode,
            name,
            effects,
            RequiresConfirmation: true,
            Guided: activation.Kind is MarketplaceActivationKind.Partner or MarketplaceActivationKind.LicensedFunction);
    }

    public async Task<MarketplaceActivationOutcome> DeactivateAsync(
        Guid orgId,
        HostScope scope,
        Guid activationId,
        bool confirmed,
        string userId,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // Before anything is read or asked of the channel manager: without a yes nothing happens.
        if (!confirmed)
            throw new DomainRuleException(MarketplaceChangeErrorCodes.ConfirmationRequired, "MarketplaceConfirmationRequired");

        var activation = await FindAsync(orgId, scope, activationId, track: true, ct);
        return activation.Kind switch
        {
            MarketplaceActivationKind.Channel or MarketplaceActivationKind.Tool => await DeactivateConnectionAsync(orgId, activation, userId, ct),
            MarketplaceActivationKind.Partner => await DeactivatePartnerAsync(orgId, scope, activation, userId, ct),

            // A function is in the license of the channel manager, which CasaZen only reads: the host asks for the change instead (RequestFunctionChangeAsync).
            _ => throw NotSupported(),
        };
    }

    public async Task<MarketplaceActivationOutcome> RequestFunctionChangeAsync(
        Guid orgId,
        HostScope scope,
        Guid activationId,
        string? note,
        string userId,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // The note is the host's own words and is not kept: it is only kept short. Checked before the row is read, like the confirmation of a deactivation.
        if (note is { Length: > MarketplaceChangeLimits.MaxNoteLength })
            throw new DomainRuleException(MarketplaceChangeErrorCodes.NoteInvalid, "MarketplaceChangeNoteTooLong");

        var activation = await FindAsync(orgId, scope, activationId, track: true, ct);
        if (activation.Kind != MarketplaceActivationKind.LicensedFunction || activation.Status == MarketplaceActivationStatus.Deactivated)
            throw NotSupported();

        // One request is open at a time: the license answers it (the reconciliation clears it when the state of the function changes).
        if (activation.ChangeRequestedAt is not null)
            throw AlreadyRequested();

        var now = Now();
        activation.ChangeRequestedAt = now;
        activation.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The row was written meanwhile, perhaps by the same request made twice: read it again, and a request that is there is the answer.
            await db.Entry(activation).ReloadAsync(ct);
            if (activation.ChangeRequestedAt is not null)
                throw AlreadyRequested();

            activation.ChangeRequestedAt = now;
            activation.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Licensed function {Code} of property {PropertyId} of org {OrgId}: a change was asked of the channel manager by {UserId}, activation {ActivationId}",
            activation.TargetCode, activation.PropertyId, orgId, userId, activation.Id);
        return OutcomeOf(activation);
    }

    // ─── Deactivation by kind ────────────────────────────────────────────────────────────────────────────────

    private async Task<MarketplaceActivationOutcome> DeactivateConnectionAsync(Guid orgId, MarketplaceActivation activation, string userId, CancellationToken ct)
    {
        // Already turned off: there is nothing to delete, and asking twice changes nothing.
        if (activation.Status == MarketplaceActivationStatus.Deactivated)
            return OutcomeOf(activation);

        // A channel or a tool is always a property's: the connection is the one of that property's listing.
        if (activation.PropertyId is not { } propertyId)
            throw NotSupported();

        var link = await RequireLinkAsync(orgId, propertyId, ct);

        // Only the connection CasaZen knows it made is deleted, never one found by guessing: a row without its id has nothing to delete.
        if (activation.OctorateConnectionId is { Length: > 0 } connectionId)
        {
            try
            {
                using (orgScope.Use(orgId))
                    await client.DeleteConnectionAsync(link.AccommodationId, connectionId, ct);
            }
            catch (OctorateNotFoundException)
            {
                // The channel manager no longer has it (removed on its panel, or deleted by a request that was made twice): what was asked is done.
                logger.LogInformation(
                    "Marketplace activation {ActivationId} of org {OrgId}: its connection was no longer on the channel manager, the activation is turned off all the same",
                    activation.Id, orgId);
            }
        }
        else
        {
            logger.LogInformation(
                "Marketplace activation {ActivationId} of org {OrgId}: it has no connection to delete, it is turned off", activation.Id, orgId);
        }

        // The problem it carried is closed with it: the item of the «To do» list goes, closed by the person who turned the activation off.
        await CloseTodoAsync(activation, userId, ct);

        var now = Now();
        await SaveAsync(
            activation,
            a =>
            {
                a.Status = MarketplaceActivationStatus.Deactivated;
                a.OctorateConnectionId = null;
                a.LastErrorCode = null;
                a.LastErrorAt = null;
                a.ChangeRequestedAt = null;
                a.TodoActivityId = null;
                a.UpdatedAt = now;
            },
            ct);

        logger.LogInformation(
            "Marketplace activation {ActivationId} of org {OrgId} ({Kind}, property {PropertyId}) turned off by {UserId}",
            activation.Id, orgId, activation.Kind, activation.PropertyId, userId);
        return OutcomeOf(activation);
    }

    private async Task<MarketplaceActivationOutcome> DeactivatePartnerAsync(
        Guid orgId, HostScope scope, MarketplaceActivation activation, string userId, CancellationToken ct)
    {
        // The reach was decided above (an org-level row only for a caller who reaches the whole org), so the service finds the row and never answers its 403.
        var turnedOff = await partners.UndeclareAsync(orgId, scope, activation.TargetCode, activation.PropertyId, userId, ct);
        return new MarketplaceActivationOutcome(turnedOff.ActivationId, MarketplaceActivationKind.Partner, turnedOff.PartnerCode, turnedOff.PropertyId, turnedOff.Status, null);
    }

    /// <summary>Closes the open item of the activation's «To do», if it has one; an item that is gone is nothing to close.</summary>
    private async Task CloseTodoAsync(MarketplaceActivation activation, string userId, CancellationToken ct)
    {
        if (activation.TodoActivityId is not { } todoId)
            return;

        try
        {
            await activities.CloseAsync(todoId, userId, ct);
        }
        catch (NotFoundException)
        {
            logger.LogInformation("Marketplace activation {ActivationId}: its To do is gone, nothing to close", activation.Id);
        }
    }

    // ─── The checks every method shares ──────────────────────────────────────────────────────────────────────

    /// <summary>The listing a property is confirmed to: its accommodation id, which is the <c>acc</c> of every call.</summary>
    private sealed record Link(string AccommodationId);

    /// <summary>
    /// The activation of the caller. Another org's, one outside the scope, one of a property that was deleted and one that does not exist are the same 404, and so is one
    /// that goes through Octorate while the connector is off. A row with no property is the whole org's: a caller limited to some properties does not reach it.
    /// </summary>
    private async Task<MarketplaceActivation> FindAsync(Guid orgId, HostScope scope, Guid activationId, bool track, CancellationToken ct)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        IQueryable<MarketplaceActivation> rows = track ? db.MarketplaceActivations : db.MarketplaceActivations.AsNoTracking();
        var activation = await rows.FirstOrDefaultAsync(a => a.Id == activationId && a.OrgId == orgId, ct)
                         ?? throw ActivationNotFound();

        // What goes through Octorate does not exist for anybody while the connector is off; only the partners are not behind the flag.
        if (activation.Kind != MarketplaceActivationKind.Partner && !featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            throw ActivationNotFound();

        if (activation.PropertyId is not { } propertyId)
            return scope.IsOrgWide ? activation : throw ActivationNotFound();

        var reachable = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Id == propertyId)
            .InScope(scope)
            .AnyAsync(ct);
        return reachable ? activation : throw ActivationNotFound();
    }

    /// <summary>The org is connected (the connector is on and it has a connected account in this environment) and the property has its confirmed listing.</summary>
    private async Task<Link> RequireLinkAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        var connectionKey = await ConnectedAccountAsync(orgId, ct) ?? throw NotConnected();
        var accommodation = await db.OctorateListingMappings.AsNoTracking()
            .Where(m => m.OrgId == orgId
                        && m.PropertyId == propertyId
                        && m.ConnectionId == connectionKey
                        && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => m.OctorateAccommodationId)
            .FirstOrDefaultAsync(ct);
        return accommodation is null ? throw NotLinked() : new Link(accommodation);
    }

    /// <summary>The id of the org's connected account in this environment; <c>null</c> while the connector is off or the org is not connected.</summary>
    private async Task<Guid?> ConnectedAccountAsync(Guid orgId, CancellationToken ct)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return null;

        var environment = OctorateEnvironments.Of(hostEnvironment);
        return await db.OctorateConnections.AsNoTracking()
            .Where(c => c.OrgId == orgId && c.Environment == environment && c.Status == OctorateConnectionStatus.Connected)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// What goes to the channel manager: only what the portal asks for (the identifier of the property, the username and the password) and the host gave, trimmed; anything
    /// more is dropped. A portal that asks for none of them has nothing to change, and a change that names nothing is never «everything».
    /// </summary>
    private async Task<OctorateConnectionPatch> PatchOfAsync(MarketplaceActivation activation, MarketplaceSettingsChange settings, CancellationToken ct)
    {
        var portal = await db.MarketplacePortals.AsNoTracking().FirstOrDefaultAsync(p => p.PortalCode == activation.TargetCode, ct);
        if (portal is null || (!portal.RequiresHotelId && !portal.RequiresPassword))
            throw NotSupported();

        var patch = new OctorateConnectionPatch
        {
            HotelId = portal.RequiresHotelId ? Clean(settings.HotelId) : null,
            Username = portal.RequiresPassword ? Clean(settings.Username) : null,
            Password = portal.RequiresPassword && !string.IsNullOrEmpty(settings.Password) ? settings.Password : null,
        };
        return patch.IsEmpty
            ? throw new DomainRuleException(MarketplaceChangeErrorCodes.SettingsRequired, "MarketplaceSettingsRequired")
            : patch;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The names of the members a change sets, for the log: never their values.</summary>
    private static string MembersOf(OctorateConnectionPatch patch)
    {
        var members = new List<string>();
        if (patch.HotelId is not null)
            members.Add("hotelId");

        if (patch.Username is not null || patch.Password is not null)
            members.Add("credentials");

        return string.Join(", ", members);
    }

    /// <summary>The name that goes into the sentences: the portal as the cache has it, the partner as the catalog has it, the function in the language asked; the code when none is known.</summary>
    private async Task<string> NameOfAsync(MarketplaceActivation activation, string lang, CancellationToken ct)
    {
        switch (activation.Kind)
        {
            case MarketplaceActivationKind.LicensedFunction:
                return texts.Get($"Marketplace.Licensed.{activation.TargetCode}.Name", lang);

            case MarketplaceActivationKind.Partner:
                var partner = await db.MarketplacePartners.AsNoTracking()
                    .Where(p => p.Code == activation.TargetCode)
                    .Select(p => p.Name)
                    .FirstOrDefaultAsync(ct);
                return string.IsNullOrWhiteSpace(partner) ? activation.TargetCode : partner;

            default:
                var portal = await db.MarketplacePortals.AsNoTracking()
                    .Where(p => p.PortalCode == activation.TargetCode)
                    .Select(p => p.Name)
                    .FirstOrDefaultAsync(ct);
                return string.IsNullOrWhiteSpace(portal) ? activation.TargetCode : portal;
        }
    }

    private static MarketplaceActivationOutcome OutcomeOf(MarketplaceActivation activation) =>
        new(activation.Id, activation.Kind, activation.TargetCode, activation.PropertyId, activation.Status, activation.ChangeRequestedAt);

    /// <summary>
    /// Applies <paramref name="change"/> and saves. A row changed by another request meanwhile (the version token of the row) is read again and the change applied on top once:
    /// the changes here are idempotent, so the later writer wins instead of the request failing.
    /// </summary>
    private async Task SaveAsync(MarketplaceActivation activation, Action<MarketplaceActivation> change, CancellationToken ct)
    {
        change(activation);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await db.Entry(activation).ReloadAsync(ct);
            change(activation);
            await db.SaveChangesAsync(ct);
        }
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static DomainConflictException NotConnected() =>
        new(MarketplaceActivationErrorCodes.OctorateNotConnected, "MarketplaceOctorateNotConnected");

    private static DomainRuleException NotLinked() =>
        new(MarketplaceActivationErrorCodes.PropertyNotLinked, "MarketplacePropertyNotLinked");

    private static DomainConflictException ConnectionMissing() =>
        new(MarketplaceActivationErrorCodes.ConnectionMissing, "MarketplaceConnectionMissing");

    private static DomainRuleException NotSupported() =>
        new(MarketplaceChangeErrorCodes.ChangeNotSupported, "MarketplaceChangeNotSupported");

    private static DomainConflictException AlreadyRequested() =>
        new(MarketplaceChangeErrorCodes.ChangeAlreadyRequested, "MarketplaceChangeAlreadyRequested");

    private static NotFoundException ActivationNotFound() =>
        new("The activation is not one of the organization inside the scope of the caller.")
        {
            Code = MarketplaceChangeErrorCodes.ActivationNotFound,
            MessageKey = "MarketplaceActivationNotFound",
        };
}
