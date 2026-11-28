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

        if (!confirmed)
            throw new DomainRuleException(MarketplaceChangeErrorCodes.ConfirmationRequired, "MarketplaceConfirmationRequired");

        var activation = await FindAsync(orgId, scope, activationId, track: true, ct);
        return activation.Kind switch
        {
            MarketplaceActivationKind.Channel or MarketplaceActivationKind.Tool => await DeactivateConnectionAsync(orgId, activation, userId, ct),
            MarketplaceActivationKind.Partner => await DeactivatePartnerAsync(orgId, scope, activation, userId, ct),

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

        if (note is { Length: > MarketplaceChangeLimits.MaxNoteLength })
            throw new DomainRuleException(MarketplaceChangeErrorCodes.NoteInvalid, "MarketplaceChangeNoteTooLong");

        var activation = await FindAsync(orgId, scope, activationId, track: true, ct);
        if (activation.Kind != MarketplaceActivationKind.LicensedFunction || activation.Status == MarketplaceActivationStatus.Deactivated)
            throw NotSupported();

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

    private async Task<MarketplaceActivationOutcome> DeactivateConnectionAsync(Guid orgId, MarketplaceActivation activation, string userId, CancellationToken ct)
    {
        if (activation.Status == MarketplaceActivationStatus.Deactivated)
            return OutcomeOf(activation);

        if (activation.PropertyId is not { } propertyId)
            throw NotSupported();

        if (activation.OctorateConnectionId is { Length: > 0 } connectionId)
        {
            var link = await RequireLinkAsync(orgId, propertyId, ct);
            try
            {
                using (orgScope.Use(orgId))
                    await client.DeleteConnectionAsync(link.AccommodationId, connectionId, ct);
            }
            catch (OctorateNotFoundException)
            {
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
        var turnedOff = await partners.UndeclareAsync(orgId, scope, activation.TargetCode, activation.PropertyId, userId, ct);
        return new MarketplaceActivationOutcome(turnedOff.ActivationId, MarketplaceActivationKind.Partner, turnedOff.PartnerCode, turnedOff.PropertyId, turnedOff.Status, null);
    }

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

    private sealed record Link(string AccommodationId);

    private async Task<MarketplaceActivation> FindAsync(Guid orgId, HostScope scope, Guid activationId, bool track, CancellationToken ct)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        IQueryable<MarketplaceActivation> rows = track ? db.MarketplaceActivations : db.MarketplaceActivations.AsNoTracking();
        var activation = await rows.FirstOrDefaultAsync(a => a.Id == activationId && a.OrgId == orgId, ct)
                         ?? throw ActivationNotFound();

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

    private async Task<Link> RequireLinkAsync(Guid orgId, Guid propertyId, CancellationToken ct) =>
        new Link(await MarketplaceReads.RequireLinkedAccommodationAsync(db, featureFlags, hostEnvironment, orgId, propertyId, ct));

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

    private static string MembersOf(OctorateConnectionPatch patch)
    {
        var members = new List<string>();
        if (patch.HotelId is not null)
            members.Add("hotelId");

        if (patch.Username is not null || patch.Password is not null)
            members.Add("credentials");

        return string.Join(", ", members);
    }

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
