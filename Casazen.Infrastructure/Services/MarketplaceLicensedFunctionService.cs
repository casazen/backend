using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IMarketplaceLicensedFunctionService"/> (E09/F02/US02/T01, NF-56, MKT-03, PO-12, D21): the functions of the channel manager's license for a
/// property, read from the property's <see cref="MarketplaceActivation"/> rows of kind <c>LicensedFunction</c> and from one live read of the license
/// (<see cref="IOctorateApiClient.GetAccommodationLicenseAsync"/>). CasaZen never writes the license: a request is a row that says the host asked.
/// </summary>
/// <remarks>
/// <para><b>One row for each property and module.</b> The license is the accommodation's, so the row has the property (<c>PropertyId</c>), and the
/// unique index on org, property, kind and target keeps it single: two requests that race get one row, the loser is answered with the winner's.</para>
/// <para><b>Only the license decides.</b> A module the license shows on is Active, verified and no longer declared; a verified Active module the license
/// shows off is To fix (<see cref="MarketplaceFunctionErrorCodes.LicenseModuleOff"/>); a module the license does not state changes nothing, because an
/// absent flag is not an off flag; a Requested row waits. The reads and the writes happen only with the connector on and the org connected
/// (<see cref="ConnectedAccountAsync"/>), and an Octorate failure is raised as it is: nothing is recorded on a read that failed.</para>
/// <para><b>Last read.</b> The plan and the room limit of the last live read are kept in memory for a week, by org, property and accommodation (a
/// property linked to another accommodation starts again), not in a table: after a restart they are absent until the next read, and
/// <see cref="MarketplaceFunctionList.LicenseReadAt"/> says so. Only the plan and the number are kept, never the answer of Octorate.</para>
/// <para><b>Logs.</b> Ids, the module code and counts: never a name of a property, a body of Octorate or a token.</para>
/// </remarks>
public sealed class MarketplaceLicensedFunctionService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    IMarketplaceTexts texts,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<MarketplaceLicensedFunctionService> logger) : IMarketplaceLicensedFunctionService
{
    /// <summary>The resource key of what the host asks its channel manager for a function: <c>{0}</c> is the name of the function, <c>{1}</c> the name of the property.</summary>
    internal const string RequestGuidanceKey = "Marketplace.Function.RequestGuidance";

    /// <summary>How long the plan and the room limit of a read are kept in memory.</summary>
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromDays(7);

    public async Task<MarketplaceFunctionList> ListAsync(
        Guid orgId,
        HostScope scope,
        Guid propertyId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        var lang = MarketplaceLanguages.Normalize(language);
        var propertyName = await PropertyNameAsync(orgId, scope, propertyId, ct);
        var link = await RequireLinkAsync(orgId, propertyId, ct);

        var rows = await RowsAsync(orgId, propertyId, ct);
        return BuildList(propertyId, propertyName, rows, SnapshotOf(orgId, propertyId, link), lang);
    }

    public async Task<MarketplaceFunctionList> RefreshAsync(
        Guid orgId,
        HostScope scope,
        Guid propertyId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        var lang = MarketplaceLanguages.Normalize(language);
        var propertyName = await PropertyNameAsync(orgId, scope, propertyId, ct);
        var link = await RequireLinkAsync(orgId, propertyId, ct);

        await ReadAndApplyAsync(orgId, propertyId, link, ct);

        var rows = await RowsAsync(orgId, propertyId, ct);
        return BuildList(propertyId, propertyName, rows, SnapshotOf(orgId, propertyId, link), lang);
    }

    public async Task<MarketplaceFunctionEntry> RequestAsync(
        Guid orgId,
        HostScope scope,
        Guid propertyId,
        string moduleCode,
        string userId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // The code is checked before anything is read: it does not depend on a tenant, and an unknown code is the same answer for everybody.
        var code = moduleCode?.Trim();
        if (code is null || !MarketplaceLicensedFunctions.IsKnown(code))
            throw FunctionUnknown();

        var lang = MarketplaceLanguages.Normalize(language);
        var propertyName = await PropertyNameAsync(orgId, scope, propertyId, ct);
        var link = await RequireLinkAsync(orgId, propertyId, ct);

        // The license is read first and applied: what it shows is recorded even when it is the reason of the refusal, so that the card of a module that
        // is already on says Active at once instead of waiting for the daily check.
        var license = await ReadAndApplyAsync(orgId, propertyId, link, ct);
        if (license.Modules.TryGetValue(code, out var isOn) && isOn)
            throw new DomainConflictException(MarketplaceFunctionErrorCodes.AlreadyActive, "MarketplaceFunctionAlreadyActive");

        var row = await RecordRequestAsync(orgId, propertyId, code, ct);
        logger.LogInformation(
            "Licensed function {Code} asked for property {PropertyId} of org {OrgId} by {UserId}: activation {ActivationId} is {Status}",
            code, propertyId, orgId, userId, row.Id, row.Status);
        return EntryOf(code, row, propertyName, lang);
    }

    public async Task ReconcileAsync(Guid orgId, Guid propertyId, CancellationToken ct = default)
    {
        if (orgId == Guid.Empty)
            throw new ArgumentException("A reconciliation is for an organization.", nameof(orgId));

        // No caller, so no scope: the property must be the org's, and a property that is not is the 404 of every other answer.
        var belongs = await db.Properties.AsNoTracking().AnyAsync(p => p.OrgId == orgId && p.Id == propertyId, ct);
        if (!belongs)
            throw MarketplaceReads.PropertyNotFound();

        var link = await RequireLinkAsync(orgId, propertyId, ct);
        await ReadAndApplyAsync(orgId, propertyId, link, ct);
    }

    // ─── The checks every method shares ──────────────────────────────────────────────────────────────────────

    /// <summary>The listing a property is confirmed to: the accommodation id, which is the <c>acc</c> of the call.</summary>
    private sealed record Link(string AccommodationId);

    /// <summary>
    /// The name of a property of the org inside the caller's scope, for the guidance. Another org's property, one outside the scope and one that
    /// does not exist (or was deleted) are the same 404: <c>marketplace_property_not_found</c>.
    /// </summary>
    private async Task<string> PropertyNameAsync(Guid orgId, HostScope scope, Guid propertyId, CancellationToken ct)
    {
        var found = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Id == propertyId)
            .InScope(scope)
            .Select(p => new { p.Name })
            .FirstOrDefaultAsync(ct);
        return found?.Name ?? throw MarketplaceReads.PropertyNotFound();
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

    private async Task<List<MarketplaceActivation>> RowsAsync(Guid orgId, Guid propertyId, CancellationToken ct) =>
        await db.MarketplaceActivations.AsNoTracking()
            .Where(a => a.OrgId == orgId && a.PropertyId == propertyId && a.Kind == MarketplaceActivationKind.LicensedFunction)
            .ToListAsync(ct);

    // ─── The live read and what it changes ───────────────────────────────────────────────────────────────────

    /// <summary>Reads the license of the property from Octorate, once, brings the rows in line with it and keeps the plan and the room limit.</summary>
    private async Task<OctorateLicense> ReadAndApplyAsync(Guid orgId, Guid propertyId, Link link, CancellationToken ct)
    {
        OctorateLicense license;
        using (orgScope.Use(orgId))
            license = await client.GetAccommodationLicenseAsync(link.AccommodationId, ct);

        await ApplyAsync(orgId, propertyId, license, ct);
        cache.Set(
            SnapshotKey(orgId, propertyId, link),
            new LicenseSnapshot(license.Tier, license.RoomsLimit, Now()),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = SnapshotLifetime });
        return license;
    }

    /// <summary>
    /// Applies the license to the rows of the property and saves. Another request that wrote the same rows first (the daily check and a click on
    /// «Check now» together: the same row changed, or the same row created) is read again and the license applied once more on top of it; a second
    /// failure is raised.
    /// </summary>
    private async Task ApplyAsync(Guid orgId, Guid propertyId, OctorateLicense license, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var outcome = await ReconcileRowsAsync(orgId, propertyId, license, ct);
            try
            {
                await db.SaveChangesAsync(ct);
                if (outcome.Activated + outcome.Flagged > 0)
                {
                    logger.LogInformation(
                        "License of property {PropertyId} of org {OrgId} reconciled: {Activated} functions Active, {Flagged} to fix",
                        propertyId, orgId, outcome.Activated, outcome.Flagged);
                }

                return;
            }
            catch (DbUpdateException ex) when (attempt < 2)
            {
                logger.LogInformation(
                    "License of property {PropertyId} of org {OrgId}: the rows were written meanwhile ({Error}), applying it again",
                    propertyId, orgId, ex.GetType().Name);
                ForgetTrackedRows();
            }
        }
    }

    /// <summary>
    /// The rules of the reconciliation, on the tracked rows of the property. A module the license does not state is skipped (an absent flag is not
    /// «off»). On: a row that is not Active becomes Active and verified, a missing row is created Active and verified, an Active row is stamped.
    /// Off: a verified Active row becomes To fix with <see cref="MarketplaceFunctionErrorCodes.LicenseModuleOff"/>; a request waits (stamped only).
    /// A row whose status changes closes its open change request: the license has answered it.
    /// </summary>
    private async Task<Reconciled> ReconcileRowsAsync(Guid orgId, Guid propertyId, OctorateLicense license, CancellationToken ct)
    {
        var now = Now();
        var rows = await db.MarketplaceActivations
            .Where(a => a.OrgId == orgId && a.PropertyId == propertyId && a.Kind == MarketplaceActivationKind.LicensedFunction)
            .ToListAsync(ct);

        var activated = 0;
        var flagged = 0;
        foreach (var code in MarketplaceLicensedFunctions.Codes)
        {
            if (!license.Modules.TryGetValue(code, out var isOn))
                continue;

            var row = rows.FirstOrDefault(r => string.Equals(r.TargetCode, code, StringComparison.Ordinal));
            if (isOn)
            {
                if (row is null)
                {
                    // The collection of the DbSet, not the navigation of another entity: a row with an id of its own is added, never attached as modified.
                    db.MarketplaceActivations.Add(new MarketplaceActivation
                    {
                        OrgId = orgId,
                        PropertyId = propertyId,
                        Kind = MarketplaceActivationKind.LicensedFunction,
                        TargetCode = code,
                        Status = MarketplaceActivationStatus.Active,
                        IsDeclared = false,
                        VerifiedAt = now,
                        LastSyncedAt = now,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                    activated++;
                }
                else if (row.Status != MarketplaceActivationStatus.Active)
                {
                    row.Status = MarketplaceActivationStatus.Active;
                    row.IsDeclared = false;
                    row.DeclaredAt = null;
                    row.VerifiedAt = now;
                    row.LastErrorCode = null;
                    row.LastErrorAt = null;
                    row.ChangeRequestedAt = null;
                    row.LastSyncedAt = now;
                    row.UpdatedAt = now;
                    activated++;
                }
                else
                {
                    row.IsDeclared = false;
                    row.VerifiedAt ??= now;
                    row.LastSyncedAt = now;
                    row.UpdatedAt = now;
                }
            }
            else if (row is not null)
            {
                if (row is { Status: MarketplaceActivationStatus.Active, VerifiedAt: not null })
                {
                    row.Status = MarketplaceActivationStatus.ToFix;
                    row.LastErrorCode = MarketplaceFunctionErrorCodes.LicenseModuleOff;
                    row.LastErrorAt = now;
                    row.ChangeRequestedAt = null;
                    row.LastSyncedAt = now;
                    row.UpdatedAt = now;
                    flagged++;
                }
                else
                {
                    // A request waits, and anything else stays as it is: the license answered, and the answer changes nothing for this row.
                    row.LastSyncedAt = now;
                    row.UpdatedAt = now;
                }
            }
        }

        return new Reconciled(activated, flagged);
    }

    /// <summary>
    /// Creates the request of the module for the property, or returns the one that exists. A request under way (and a module that is Active) is
    /// returned as it is; a module to fix, or turned off, is asked for again. A request that lost a race on the unique index returns the winner's row.
    /// </summary>
    private async Task<MarketplaceActivation> RecordRequestAsync(Guid orgId, Guid propertyId, string code, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var now = Now();
            var row = await db.MarketplaceActivations.FirstOrDefaultAsync(
                a => a.OrgId == orgId
                     && a.PropertyId == propertyId
                     && a.Kind == MarketplaceActivationKind.LicensedFunction
                     && a.TargetCode == code,
                ct);

            if (row is { Status: MarketplaceActivationStatus.Requested or MarketplaceActivationStatus.Pending or MarketplaceActivationStatus.Active })
                return row;

            if (row is null)
            {
                row = new MarketplaceActivation
                {
                    OrgId = orgId,
                    PropertyId = propertyId,
                    Kind = MarketplaceActivationKind.LicensedFunction,
                    TargetCode = code,
                    Status = MarketplaceActivationStatus.Requested,
                    IsDeclared = true,
                    DeclaredAt = now,
                    RequestedAt = now,
                    LastSyncedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.MarketplaceActivations.Add(row);
            }
            else
            {
                row.Status = MarketplaceActivationStatus.Requested;
                row.IsDeclared = true;
                row.DeclaredAt = now;
                row.RequestedAt = now;
                row.VerifiedAt = null;
                row.LastErrorCode = null;
                row.LastErrorAt = null;
                row.ChangeRequestedAt = null;
                row.LastSyncedAt = now;
                row.UpdatedAt = now;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return row;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Two requests for the same module at once: the other one wrote the row first. Read it again; it is returned as it is.
                ForgetTrackedRows();
            }
        }
    }

    /// <summary>The rows are read again after a failed save: nothing tracked from the failed attempt may be kept.</summary>
    private void ForgetTrackedRows()
    {
        foreach (var entry in db.ChangeTracker.Entries<MarketplaceActivation>().ToList())
            entry.State = EntityState.Detached;
    }

    private sealed record Reconciled(int Activated, int Flagged);

    // ─── What the page shows ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The plan and the room limit of a read, as kept in memory.</summary>
    private sealed record LicenseSnapshot(string? Tier, int? RoomsLimit, DateTime ReadAt);

    private LicenseSnapshot? SnapshotOf(Guid orgId, Guid propertyId, Link link) =>
        cache.TryGetValue(SnapshotKey(orgId, propertyId, link), out LicenseSnapshot? snapshot) ? snapshot : null;

    /// <summary>The org is in the key: the same property id of two orgs can never meet. The accommodation is in it too: a property linked to another one starts again.</summary>
    private static string SnapshotKey(Guid orgId, Guid propertyId, Link link) =>
        $"marketplace-license:{orgId:N}:{propertyId:N}:{link.AccommodationId}";

    private MarketplaceFunctionList BuildList(
        Guid propertyId,
        string propertyName,
        IReadOnlyCollection<MarketplaceActivation> rows,
        LicenseSnapshot? snapshot,
        string language)
    {
        var entries = MarketplaceLicensedFunctions.Codes
            .Select(code => EntryOf(code, rows.FirstOrDefault(r => string.Equals(r.TargetCode, code, StringComparison.Ordinal)), propertyName, language))
            .ToList();
        return new MarketplaceFunctionList(
            propertyId,
            snapshot?.Tier,
            snapshot?.RoomsLimit,
            snapshot?.RoomsLimit == 0,
            snapshot?.ReadAt,
            entries);
    }

    private MarketplaceFunctionEntry EntryOf(string code, MarketplaceActivation? row, string propertyName, string language)
    {
        var status = StatusOf(row);
        var name = texts.Get($"Marketplace.Licensed.{code}.Name", language);
        return new MarketplaceFunctionEntry(
            code,
            name,
            status,
            row?.Id,
            row?.IsDeclared ?? false,
            row?.RequestedAt,
            row?.VerifiedAt,
            row?.LastSyncedAt,
            row?.LastErrorCode,
            row?.ChangeRequestedAt,
            status == MarketplaceFunctionStatus.Active ? null : texts.Get(RequestGuidanceKey, language, name, propertyName));
    }

    /// <summary>No row, and a turned off one, is not active; a request under way (Requested, or Pending) is Requested.</summary>
    private static MarketplaceFunctionStatus StatusOf(MarketplaceActivation? row) => row?.Status switch
    {
        MarketplaceActivationStatus.Active => MarketplaceFunctionStatus.Active,
        MarketplaceActivationStatus.ToFix => MarketplaceFunctionStatus.ToFix,
        MarketplaceActivationStatus.Requested or MarketplaceActivationStatus.Pending => MarketplaceFunctionStatus.Requested,
        _ => MarketplaceFunctionStatus.NotActive,
    };

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static DomainConflictException NotConnected() =>
        new(MarketplaceActivationErrorCodes.OctorateNotConnected, "MarketplaceOctorateNotConnected");

    private static DomainRuleException NotLinked() =>
        new(MarketplaceActivationErrorCodes.PropertyNotLinked, "MarketplacePropertyNotLinked");

    private static NotFoundException FunctionUnknown() =>
        new("The function is not one of the modules of the license.")
        {
            Code = MarketplaceFunctionErrorCodes.Unknown,
            MessageKey = "MarketplaceFunctionUnknown",
        };
}
